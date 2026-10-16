using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LambdaRag.Core.Domain;
using LambdaRag.Markup;
using Microsoft.Agents.AI;

namespace LambdaRag.Authoring.Editing;

/// <summary>
/// Compliance-focused clause re-author backed by an OpenRouter chat client
/// wrapped as a Microsoft Agent Framework v2 <see cref="AIAgent"/>. The agent
/// is constructed via:
///
/// <code>
/// var agent = chatClient.AsAIAgent(instructions, name: "ComplianceEditor");
/// </code>
///
/// Each rewrite is a stateless one-shot
/// turn. Determinism is enforced via a SHA-256 disk cache and strict
/// normalization on the model response.
///
/// The agent's only job is **clause re-authoring**: take the original
/// clause + the rule guidance + (optional) remediation hint, return the
/// new clause text that would make the document comply. No commentary,
/// no markdown, no JSON.
/// </summary>
public sealed class ComplianceEditor : IClauseRewriter, IReasonedClauseRewriter
{
    public const string CacheSchemaVersion = "v2";

    public const string AgentName = "ComplianceEditor";

    public const string SystemPrompt = """
        You are ComplianceEditor, a focused compliance redlining agent.
        Your only job is to re-author a single contract clause so it
        complies with a stated rule.

        You will receive:
        - RULE: the compliance rule the clause currently violates.
        - REMEDIATION: optional drafting hint from the rule author.
        - CLAUSE: the original clause text from the document. The CLAUSE
          may span multiple paragraphs — newline characters ('\n') in the
          input separate paragraphs. Each line frequently corresponds to
          a bullet point, a numbered item, or a section heading in the
          source document.

        Output ONLY the rewritten clause text. No preamble, no markdown,
        no quotes, no JSON, no "Here is...".

        MINIMAL EDITS (critical): change only the words needed to address
        the cited rule. Keep every unaffected word, sentence, defined
        term (capitalized terms), number, name and fact exactly as in the
        CLAUSE. Do not invent obligations, implementation details, parties,
        amounts, dates or facts that are not in the CLAUSE, RULE or
        REMEDIATION.

        STRUCTURE PRESERVATION (critical): if the CLAUSE contains '\n',
        your output MUST use '\n' to preserve the same paragraph / list
        structure with exactly the same number of lines. Do not collapse a
        bulleted list into a single paragraph, do not add or remove lines,
        and do not output markdown bullet markers — emit raw line text
        separated by '\n'.

        DECLINING: if the CLAUSE does not itself contain the problem, the
        rule cannot be satisfied by editing this CLAUSE alone (for example
        the requirement is missing and would need new content), the
        target is ambiguous, or you are not confident a safe, necessary
        edit exists, output exactly NO_REWRITE and nothing else. Declining
        is always acceptable; a wrong rewrite is not.

        Keep total length proportional to the original; do not exceed
        1000 characters.
        """;

    // Bumped whenever SystemPrompt or the validation contract changes so cached
    // decisions made under an older contract are never replayed.
    public const string PromptContractVersion = "v2-minimal-norewrite";

    public static string PromptFingerprint { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(PromptContractVersion + "\u001f" + SystemPrompt)))
        .ToLowerInvariant();

    private readonly AIAgent _agent;
    private readonly ComplianceEditorOptions _options;

    public ComplianceEditor(AIAgent agent, ComplianceEditorOptions options)
    {
        _agent = agent ?? throw new ArgumentNullException(nameof(agent));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        Directory.CreateDirectory(_options.CacheDir);
    }

    public async Task<string?> RewriteAsync(
        Verdict verdict,
        string clauseText,
        Rule? rule,
        CancellationToken cancellationToken = default)
        => (await RewriteWithReasonAsync(verdict, clauseText, rule, cancellationToken).ConfigureAwait(false)).Text;

    public async Task<RewriteOutcome> RewriteWithReasonAsync(
        Verdict verdict,
        string clauseText,
        Rule? rule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verdict);
        if (string.IsNullOrWhiteSpace(clauseText))
            return RewriteOutcome.Rejected(RewriteSkipReason.NoClauseText);

        var key = ComputeCacheKey(rule, verdict, clauseText);
        var cachePath = Path.Combine(_options.CacheDir, key + ".json");

        if (File.Exists(cachePath))
        {
            var cached = TryReadCache(cachePath);
            if (cached is not null)
            {
                // Re-validate on replay so a cache entry can never bypass the contract.
                return cached.Reason == RewriteSkipReason.None
                    ? Validate(clauseText, cached.Rewrite, _options.MaxRewriteLength, rawAlreadyNormalized: true)
                    : RewriteOutcome.Rejected(cached.Reason);
            }
        }

        var userMessage = BuildUserMessage(rule, verdict, clauseText);
        string? raw;
        try
        {
            var response = await _agent
                .RunAsync(userMessage, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            raw = response?.Text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return RewriteOutcome.Rejected(RewriteSkipReason.ModelError);
        }

        var outcome = Validate(clauseText, raw, _options.MaxRewriteLength);
        File.WriteAllText(cachePath, JsonSerializer.Serialize(new CacheEntry(
            RuleId: rule?.Id,
            VerdictId: verdict.Id,
            Rewrite: outcome.Text ?? string.Empty,
            CreatedAt: DateTimeOffset.UtcNow)
        {
            Reason = outcome.Reason,
            PromptFingerprint = PromptFingerprint,
        }));

        return outcome;
    }

    /// <summary>
    /// Validates a raw model response against the targeted original span.
    /// Anything declined, empty, unchanged, over-broad or structurally
    /// different from the original is rejected with an explicit reason so the
    /// caller keeps the finding as a comment only.
    /// </summary>
    public static RewriteOutcome Validate(
        string originalClause, string? raw, int maxLength, bool rawAlreadyNormalized = false)
    {
        if (string.IsNullOrWhiteSpace(raw)) return RewriteOutcome.Rejected(RewriteSkipReason.EmptyResponse);
        var trimmed = raw.Trim().Trim('"', '\u201C', '\u201D').Trim();
        if (trimmed.StartsWith("NO_REWRITE", StringComparison.OrdinalIgnoreCase))
            return RewriteOutcome.Rejected(RewriteSkipReason.Declined);

        var normalized = rawAlreadyNormalized
            ? raw
            : Normalize(raw, int.MaxValue);
        if (string.IsNullOrEmpty(normalized)) return RewriteOutcome.Rejected(RewriteSkipReason.EmptyResponse);

        var original = Normalize(originalClause, int.MaxValue) ?? string.Empty;
        if (string.Equals(original, normalized, StringComparison.Ordinal))
            return RewriteOutcome.Rejected(RewriteSkipReason.Unchanged);

        if (normalized.Length > maxLength
            || normalized.Length > original.Length * 2 + 80)
            return RewriteOutcome.Rejected(RewriteSkipReason.OverBroad);

        var origLines = original.Split('\n');
        var newLines = normalized.Split('\n');
        if (origLines.Length != newLines.Length || newLines.Any(l => l.Length == 0))
            return RewriteOutcome.Rejected(RewriteSkipReason.StructureChanged);
        for (var i = 0; i < newLines.Length; i++)
        {
            if (StartsWithListMarker(newLines[i]) && !StartsWithListMarker(origLines[i]))
                return RewriteOutcome.Rejected(RewriteSkipReason.StructureChanged);
        }

        // Preservation: most original words must survive for the edit to count as minimal.
        var origWords = Words(original);
        if (origWords.Count >= 6)
        {
            var newWords = Words(normalized);
            var kept = origWords.Count(w => newWords.Contains(w));
            if (kept * 2 < origWords.Count)
                return RewriteOutcome.Rejected(RewriteSkipReason.OverBroad);
        }

        return RewriteOutcome.Accepted(normalized);
    }

    private static bool StartsWithListMarker(string line)
    {
        var l = line.TrimStart();
        return l.StartsWith("- ", StringComparison.Ordinal)
            || l.StartsWith("* ", StringComparison.Ordinal)
            || l.StartsWith("\u2022", StringComparison.Ordinal)
            || l.StartsWith('#')
            || l.StartsWith("```", StringComparison.Ordinal);
    }

    private static HashSet<string> Words(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in s.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var t = w.Trim('.', ',', ';', ':', '(', ')', '"', '\'');
            if (t.Length > 0) set.Add(t);
        }
        return set;
    }

    public static string ComputeCacheKey(Rule? rule, Verdict verdict, string clauseText)
    {
        var canonical = string.Join('\u001f',
            CacheSchemaVersion,
            PromptFingerprint,
            rule?.Id ?? string.Empty,
            rule?.Version ?? string.Empty,
            verdict.RuleId,
            verdict.RemediationText ?? string.Empty,
            clauseText);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string? Normalize(string? raw, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        if (s.Equals("NO_REWRITE", StringComparison.Ordinal)) return null;

        if (s.Length >= 2 && (s[0] == '"' || s[0] == '\u201C') && (s[^1] == '"' || s[^1] == '\u201D'))
            s = s[1..^1].Trim();

        var sb = new StringBuilder(s.Length);
        var prevSpace = false;
        foreach (var ch in s)
        {
            if (ch == '\n')
            {
                sb.Append('\n');
                prevSpace = false;
                continue;
            }
            if (char.IsWhiteSpace(ch))
            {
                if (prevSpace) continue;
                prevSpace = true;
                sb.Append(' ');
            }
            else
            {
                prevSpace = false;
                sb.Append(ch);
            }
        }
        s = sb.ToString().Trim();

        if (s.Length == 0) return null;
        if (s.Length > maxLength) s = s[..maxLength].TrimEnd() + "\u2026";
        return s;
    }

    private static string BuildUserMessage(Rule? rule, Verdict verdict, string clauseText)
    {
        var sb = new StringBuilder();
        sb.Append("RULE: ");
        sb.Append(rule?.NaturalLanguage ?? verdict.RuleId);
        sb.Append('\n');

        if (!string.IsNullOrWhiteSpace(verdict.RemediationText))
        {
            sb.Append("REMEDIATION: ").Append(verdict.RemediationText).Append('\n');
        }

        sb.Append("CLAUSE: ").Append(clauseText);
        return sb.ToString();
    }

    private static CacheEntry? TryReadCache(string path)
    {
        try
        {
            var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path));
            return entry is not null
                && string.Equals(entry.PromptFingerprint, PromptFingerprint, StringComparison.Ordinal)
                ? entry
                : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record CacheEntry(
        string? RuleId,
        string VerdictId,
        string Rewrite,
        DateTimeOffset CreatedAt)
    {
        public RewriteSkipReason Reason { get; init; }
        public string? PromptFingerprint { get; init; }
    }
}

/// <summary>Why a rewrite was not emitted. <see cref="None"/> means accepted.</summary>
public enum RewriteSkipReason
{
    None = 0,
    Declined,
    EmptyResponse,
    Unchanged,
    OverBroad,
    StructureChanged,
    ModelError,
    NoClauseText,
    AmbiguousTarget,
    SharedIncompatibleSpan,
    UnrelatedContentAtRisk,
    NotRewritable,
}

public sealed record RewriteOutcome(string? Text, RewriteSkipReason Reason)
{
    public bool IsAccepted => Reason == RewriteSkipReason.None && !string.IsNullOrEmpty(Text);
    public static RewriteOutcome Accepted(string text) => new(text, RewriteSkipReason.None);
    public static RewriteOutcome Rejected(RewriteSkipReason reason) => new(null, reason);
}

/// <summary>Rewriter that reports an explicit reason when it withholds a rewrite.</summary>
public interface IReasonedClauseRewriter
{
    Task<RewriteOutcome> RewriteWithReasonAsync(
        Verdict verdict,
        string clauseText,
        Rule? rule,
        CancellationToken cancellationToken = default);
}
