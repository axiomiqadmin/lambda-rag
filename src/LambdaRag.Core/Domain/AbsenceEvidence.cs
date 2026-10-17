using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LambdaRag.Core.Domain;

/// <summary>
/// Deterministic helpers that decide when a rule that found no evidence may be
/// reported as not-a-finding: either the document explicitly states the rule's
/// subject is absent / out of scope (excluded), or the rule concerns a human
/// process outside the document. Silence alone never qualifies.
/// </summary>
public static class AbsenceEvidence
{
    public const string ExcludedTagPrefix = "excluded:";
    public const string ProcessTag = "out_of_document_scope:process";

    private static readonly Regex NegationPattern = new(
        @"(?:\b(?:no|not|without|never|none|neither|nor|excluded?|exclude[sd]|cannot)\b|n't|\bout[- ]of[- ]scope\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ProcessPattern = new(
        @"\b(sign[- ]off|signed[- ]off|(?:review(?:ed)?|approved) by|committee|board|council|steering)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SentenceSplit = new(
        @"(?<=[.!?])\s+|[\r\n]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const int NegationWindow = 6;

    private static readonly Regex ClauseSplit = new(
        @"\s*[;:]\s*|\s+(?:but|however|although|whereas|while)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly HashSet<string> DeterminerNegations = new(StringComparer.Ordinal)
    {
        "no", "without", "none", "neither", "nor",
    };

    private static readonly HashSet<string> VerbNegations = new(StringComparer.Ordinal)
    {
        "not", "never", "cannot",
    };

    private static readonly HashSet<string> ScopeNegations = new(StringComparer.Ordinal)
    {
        "exclude", "excluded", "excludes",
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "shall", "must", "should", "will", "with", "that", "this", "from", "have", "been", "into",
        "each", "every", "such", "than", "then", "their", "there", "which", "where", "when",
        "including", "include", "includes", "required", "requirements", "requirement", "also",
        "document", "solution", "other", "between", "under", "over", "using", "used", "only",
        "within", "least", "unless", "without", "these", "those", "workload", "workloads",
    };

    /// <summary>True when the rule concerns an organizational process rather than document content.</summary>
    public static bool IsOutOfDocumentProcess(Rule rule) =>
        ProcessPattern.IsMatch(rule.NaturalLanguage ?? string.Empty);

    /// <summary>
    /// The first document sentence that both negates and names the rule's subject
    /// (at least two topic tokens, or the only one), or null when the document
    /// never states the subject is absent.
    /// </summary>
    public static string? FindExclusion(Rule rule, IEnumerable<string> documentTexts)
    {
        // Direct, rule-specific evidence only: the negation must sit next to the
        // rule's subject terms. A multi-word topic slug names the subject, so all
        // its terms must be present; a single-word slug is a coarse category
        // (e.g. "DATA") and is ignored in favour of two distinct rule terms.
        rule.Metadata.TryGetValue("topicSlug", out var slug);
        var slugTerms = Tokenize(slug ?? string.Empty, minLength: 3);
        var useSlug = slugTerms.Count >= 2;
        var topic = useSlug ? slugTerms : Tokenize(rule.NaturalLanguage ?? string.Empty);
        if (topic.Count == 0) return null;
        var required = useSlug ? slugTerms.Count : Math.Min(2, topic.Count);

        foreach (var text in documentTexts)
        {
            foreach (var raw in SentenceSplit.Split(text))
            {
                var sentence = raw.Trim();
                if (sentence.Length == 0 || !NegationPattern.IsMatch(sentence)) continue;
                foreach (var clause in ClauseSplit.Split(sentence))
                    if (NegationScopesTopic(clause, topic, required)) return sentence;
            }
        }
        return null;
    }

    private static bool NegationScopesTopic(string clause, HashSet<string> topic, int required)
    {
        var words = Regex.Matches(clause.ToLowerInvariant(), @"[a-z0-9']+")
            .Select(m => m.Value).ToArray();
        for (var i = 0; i < words.Length; i++)
        {
            // Determiners ("no X", "without X") negate what follows them only,
            // so "uses Managed Identity, no shared secrets" says nothing about
            // managed identity. Verb negation ("X is not used") and scope words
            // ("X is out of scope") negate a subject that precedes them.
            int left, right;
            if (DeterminerNegations.Contains(words[i])) (left, right) = (0, NegationWindow);
            else if (VerbNegations.Contains(words[i]) || words[i].EndsWith("n't", StringComparison.Ordinal))
                (left, right) = (NegationWindow, 3);
            else if (ScopeNegations.Contains(words[i])
                || (words[i] == "out" && i + 2 < words.Length && words[i + 1] == "of" && words[i + 2] == "scope"))
                (left, right) = (NegationWindow, 4);
            else continue;

            var from = Math.Max(0, i - left);
            var to = Math.Min(words.Length - 1, i + right);
            var near = new HashSet<string>(StringComparer.Ordinal);
            for (var j = from; j <= to; j++)
                if (j != i) near.Add(words[j]);
            if (topic.Count(near.Contains) >= required) return true;
        }
        return false;
    }

    /// <summary>
    /// Established scope facts: for each authored declaration whose evidence
    /// phrase occurs in the document, the declared values plus the first
    /// sentence containing the phrase. Pure and order-stable.
    /// </summary>
    public static IReadOnlyDictionary<string, (HashSet<string> Values, string Evidence)> EstablishScopeFacts(
        IReadOnlyList<ScopeFact>? facts, IEnumerable<string> documentTexts)
    {
        var result = new SortedDictionary<string, (HashSet<string> Values, string Evidence)>(StringComparer.Ordinal);
        if (facts is not { Count: > 0 }) return result;
        var sentences = documentTexts.SelectMany(t => SentenceSplit.Split(t)).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        foreach (var f in facts.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            foreach (var phrase in f.EvidencePhrases.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                var hit = sentences.FirstOrDefault(s => s.Contains(phrase.Trim(), StringComparison.OrdinalIgnoreCase));
                if (hit is null) continue;
                if (!result.TryGetValue(f.Name, out var cur))
                    cur = (new HashSet<string>(StringComparer.OrdinalIgnoreCase), hit);
                foreach (var v in f.Values) cur.Values.Add(v.Trim());
                result[f.Name] = cur;
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// Evidence tag when the rule's authored <see cref="Rule.AppliesWhen"/> is
    /// contradicted by an established scope fact; null otherwise.
    /// </summary>
    public static string? FindScopeExclusion(
        Rule rule, IReadOnlyDictionary<string, (HashSet<string> Values, string Evidence)> established)
    {
        if (rule.AppliesWhen is not { Count: > 0 } || established.Count == 0) return null;
        foreach (var (name, accepted) in rule.AppliesWhen.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!established.TryGetValue(name, out var fact)) continue;
            if (accepted.Any(a => fact.Values.Contains(a.Trim()))) continue;
            return $"scope_fact:{name}={string.Join("|", fact.Values.OrderBy(v => v, StringComparer.Ordinal))}; rule applies when {string.Join("|", accepted)}; evidence: {fact.Evidence}";
        }
        return null;
    }

    /// <summary>All "text" and "title" string values in the projected graph, in document order.</summary>
    public static IReadOnlyList<string> CollectText(JsonNode? node)
    {
        var result = new List<string>();
        Walk(node, result);
        return result;
    }

    private static void Walk(JsonNode? node, List<string> sink)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (value is JsonValue v && v.TryGetValue<string>(out var s)
                        && (key == "text" || key == "title"))
                        sink.Add(s);
                    else
                        Walk(value, sink);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) Walk(item, sink);
                break;
        }
    }

    private static HashSet<string> Tokenize(string text, int minLength = 4) =>
        Regex.Matches(text.ToLowerInvariant(), "[a-z0-9]{" + minLength + ",}")
            .Select(m => m.Value)
            .Where(t => !StopWords.Contains(t))
            .ToHashSet(StringComparer.Ordinal);
}
