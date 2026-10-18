namespace LambdaRag.Core.Domain;

/// <summary>
/// Builds the ruleset-level <see cref="ScopeFact"/> declarations from the
/// <see cref="Rule.AppliesWhen"/> vocabulary the rules were authored with.
/// Pure and order-stable: evidence phrases are fixed templates over the
/// authored aliases, so nothing is derived from a document under review.
/// </summary>
public static class ScopeFactCatalog
{
    private static readonly string[] Templates =
    [
        "exclusively on {0}",
        "only on {0}",
        "runs on {0}",
        "deployed on {0}",
        "hosted on {0}",
    ];

    // Products/tenancy are routinely combined, so only an explicit exclusivity statement establishes them.
    private static readonly string[] ExclusiveTemplates = Templates[..2];

    private static string[] TemplatesFor(string factName) =>
        factName is "platform" or "cloud provider" ? Templates : ExclusiveTemplates;

    public static IReadOnlyList<ScopeFact> Build(IEnumerable<Rule> rules)
    {
        var sets = new SortedDictionary<string, (string Name, List<string> Values)>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            if (rule.AppliesWhen is not { Count: > 0 }) continue;
            foreach (var (name, values) in rule.AppliesWhen)
            {
                var vals = values.Select(v => v.Trim().ToLowerInvariant()).Where(v => v.Length > 0)
                    .Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
                if (vals.Count == 0) continue;
                var key = name.Trim().ToLowerInvariant() + "=" + string.Join("|", vals);
                sets[key] = (name.Trim().ToLowerInvariant(), vals);
            }
        }
        return sets.Values
            .Select(s => new ScopeFact(
                s.Name,
                s.Values,
                s.Values.SelectMany(v => TemplatesFor(s.Name).Select(t => string.Format(System.Globalization.CultureInfo.InvariantCulture, t, v)))
                    .OrderBy(p => p, StringComparer.Ordinal).ToList()))
            .ToList();
    }
}
