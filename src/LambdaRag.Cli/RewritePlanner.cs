using System.Text;
using LambdaRag.Authoring.Editing;
using LambdaRag.Core.Domain;
using LambdaRag.Markup;

namespace LambdaRag.Cli;

/// <summary>One finding that stayed comment-only, with an auditable reason.</summary>
public sealed record RewriteSkip(string VerdictId, string RuleId, RewriteSkipReason Reason, string Detail);

public sealed record RewritePlanResult(
    IReadOnlyList<Annotation> Annotations,
    IReadOnlyList<RewriteSkip> Skips,
    int Attempted,
    int Emitted,
    IReadOnlyDictionary<string, int> ErrorCounts);

/// <summary>
/// Decides, per failing verdict, whether a tracked-change rewrite may be
/// requested and whether the model's answer may be emitted. Fails closed: any
/// ambiguity yields a comment-only annotation plus a recorded skip reason.
/// Iteration order follows the report so output is deterministic.
/// </summary>
public sealed class RewritePlanner
{
    public const int MaxClauseChars = 1000;

    private readonly IClauseRewriter _rewriter;
    private readonly IReadOnlyDictionary<string, Rule> _rules;
    private readonly string _canonicalText;

    public RewritePlanner(IClauseRewriter rewriter, IReadOnlyDictionary<string, Rule> rules, string canonicalText)
    {
        _rewriter = rewriter ?? throw new ArgumentNullException(nameof(rewriter));
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _canonicalText = canonicalText ?? string.Empty;
    }

    public async Task<RewritePlanResult> PlanAsync(
        IReadOnlyList<Verdict> failVerdicts,
        Action<int, int, int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var annotations = new List<Annotation>(failVerdicts.Count);
        var skips = new List<RewriteSkip>();
        var errors = new Dictionary<string, int>(StringComparer.Ordinal);
        var attempted = 0;
        var emitted = 0;

        // Findings sharing one clause span: compatible only when they are the
        // same obligation; then exactly one (the first in report order) may rewrite.
        var shared = failVerdicts
            .Where(v => v.Outcome == VerdictOutcome.Fail)
            .Select(v => (Verdict: v, Span: v.ClauseSpan ?? v.SourceSpan))
            .Where(x => x.Span is not null)
            .GroupBy(x => (x.Span!.CharStart, x.Span!.CharLength))
            .Where(g => g.Count() > 1)
            .ToDictionary(
                g => g.Key,
                g => (
                    Compatible: g.Select(x => ObligationKey(x.Verdict)).Distinct(StringComparer.Ordinal).Count() == 1,
                    Leader: g.First().Verdict.Id));

        for (var i = 0; i < failVerdicts.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var v = failVerdicts[i];
            var rule = _rules.GetValueOrDefault(v.RuleId);
            var comment = AnnotationFactory.BuildCommentForVerdict(v, rule);
            progress?.Invoke(i + 1, failVerdicts.Count, emitted, v.RuleId);

            void Skip(RewriteSkipReason reason, string detail)
            {
                skips.Add(new RewriteSkip(v.Id, v.RuleId, reason, detail));
                annotations.Add(comment);
            }

            if (v.Outcome != VerdictOutcome.Fail)
            {
                Skip(RewriteSkipReason.NotRewritable, "outcome is not Fail");
                continue;
            }
            if (AnnotationFactory.IsSkippableSpan(v))
            {
                Skip(RewriteSkipReason.NotRewritable, "title/preamble span (path=\"/\")");
                continue;
            }
            if (v.RewriteEligible == false)
            {
                Skip(RewriteSkipReason.NotRewritable,
                    v.UnanchoredReason ?? "finding is marked ineligible for tracked-change rewriting");
                continue;
            }
            if (v.ClauseSpan is null)
            {
                Skip(RewriteSkipReason.NoClauseText,
                    v.UnanchoredReason ?? "no precise editable clause span was resolved");
                continue;
            }

            var span = v.ClauseSpan;
            if (!TryResolveClause(v, span, out var clauseText, out var ambiguity))
            {
                Skip(RewriteSkipReason.AmbiguousTarget, ambiguity);
                continue;
            }
            if (clauseText.Length > MaxClauseChars)
            {
                Skip(RewriteSkipReason.UnrelatedContentAtRisk,
                    $"clause span is {clauseText.Length} chars (limit {MaxClauseChars}); a rewrite would touch unrelated content");
                continue;
            }
            if (shared.TryGetValue((span!.CharStart, span.CharLength), out var group))
            {
                if (!group.Compatible)
                {
                    Skip(RewriteSkipReason.SharedIncompatibleSpan,
                        "clause span is shared by findings for different obligations");
                    continue;
                }
                if (!string.Equals(group.Leader, v.Id, StringComparison.Ordinal))
                {
                    Skip(RewriteSkipReason.SharedIncompatibleSpan,
                        $"same obligation already handled by finding {group.Leader} on this clause");
                    continue;
                }
            }

            attempted++;
            RewriteOutcome outcome;
            try
            {
                outcome = _rewriter is IReasonedClauseRewriter reasoned
                    ? await reasoned.RewriteWithReasonAsync(v, clauseText, rule, cancellationToken).ConfigureAwait(false)
                    : ValidateLegacy(clauseText, await _rewriter.RewriteAsync(v, clauseText, rule, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                var kind = ex.GetType().Name;
                errors[kind] = errors.GetValueOrDefault(kind) + 1;
                Skip(RewriteSkipReason.ModelError, $"{kind}: {ex.Message}");
                continue;
            }

            if (outcome.IsAccepted)
            {
                emitted++;
                annotations.Add(AnnotationFactory.BuildReplaceAnnotation(v, comment, outcome.Text!));
            }
            else
            {
                Skip(outcome.Reason == RewriteSkipReason.None ? RewriteSkipReason.EmptyResponse : outcome.Reason,
                    "rewriter withheld the replacement");
            }
        }

        return new RewritePlanResult(annotations, skips, attempted, emitted, errors);
    }

    // Rewriters that cannot explain themselves still go through the same validation.
    private static RewriteOutcome ValidateLegacy(string clause, string? text)
        => ComplianceEditor.Validate(clause, text, int.MaxValue);

    private bool TryResolveClause(Verdict v, SourceSpan? span, out string clause, out string reason)
    {
        clause = string.Empty;
        if (span is null || span.CharLength <= 0 || span.CharStart < 0
            || span.CharStart + span.CharLength > _canonicalText.Length)
        {
            reason = "no resolvable clause span in the document";
            return false;
        }
        clause = _canonicalText.Substring(span.CharStart, span.CharLength);
        if (string.IsNullOrWhiteSpace(clause))
        {
            reason = "clause span is empty";
            return false;
        }

        // The evidence anchor must lie inside the editable clause.
        var ev = v.SourceSpan;
        if (v.ClauseSpan is not null && ev is not null && ev.CharLength > 0
            && (ev.CharStart < v.ClauseSpan.CharStart
                || ev.CharStart + ev.CharLength > v.ClauseSpan.CharStart + v.ClauseSpan.CharLength))
        {
            reason = "evidence span lies outside the clause span";
            return false;
        }

        var collapsed = Collapse(clause);
        if (v.EvidenceQuotes.Count > 0
            && v.EvidenceQuotes.Where(q => !string.IsNullOrWhiteSpace(q))
                .All(q => !collapsed.Contains(Collapse(q), StringComparison.OrdinalIgnoreCase)))
        {
            reason = "evidence quote not found in the target clause";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private string ObligationKey(Verdict v)
    {
        var rule = _rules.GetValueOrDefault(v.RuleId);
        var text = rule?.NaturalLanguage;
        return string.IsNullOrWhiteSpace(text) ? "rule:" + v.RuleId : "ob:" + Collapse(text).ToLowerInvariant();
    }

    private static string Collapse(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
