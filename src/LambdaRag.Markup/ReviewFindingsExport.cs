using System.Text;
using LambdaRag.Core.Domain;

namespace LambdaRag.Markup;

/// <summary>
/// Flat, rule-level rendering of a <see cref="ComplianceReport"/> (one row per
/// rule, strongest verdict wins). Pure function of the report, so it is as
/// deterministic as the report itself; the JSON report stays authoritative.
/// </summary>
public static class ReviewFindingsExport
{
    private static readonly string[] Header =
    [
        "RuleId", "Outcome", "ReasonCode", "Section", "Requirement", "Evidence", "MissingConditions", "Detail",
    ];

    private static int Rank(VerdictOutcome o) => o switch
    {
        VerdictOutcome.Pass => 0,
        VerdictOutcome.Fail => 1,
        VerdictOutcome.Error => 2,
        VerdictOutcome.Partial => 3,
        VerdictOutcome.Gap => 4,
        VerdictOutcome.NotApplicable => 5,
        _ => 6,
    };

    public static string ToCsv(ComplianceReport report, IReadOnlyDictionary<string, Rule> rules)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(',', Header)).Append('\n');
        var best = report.Verdicts
            .GroupBy(v => v.RuleId, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.OrderBy(v => Rank(v.Outcome)).ThenBy(v => v.SourceSpan.CharStart).ThenBy(v => v.Id, StringComparer.Ordinal).First());
        foreach (var v in best)
        {
            var requirement = rules.GetValueOrDefault(v.RuleId)?.NaturalLanguage ?? string.Empty;
            var heading = v.MatchedSectionId is null ? string.Empty : v.SourceSpan.HeadingPath ?? string.Empty;
            var detail = v.Outcome == VerdictOutcome.Skipped || v.Outcome == VerdictOutcome.NotApplicable
                ? v.ErrorMessage ?? string.Empty
                : v.RemediationText ?? string.Empty;
            var fields = new[]
            {
                v.RuleId,
                v.Outcome.ToString(),
                v.ReasonCode ?? string.Empty,
                heading,
                requirement,
                v.DocumentEvidence ?? string.Empty,
                v.MissingConditions is { Count: > 0 } m ? string.Join("; ", m) : string.Empty,
                detail,
            };
            sb.Append(string.Join(',', fields.Select(Quote))).Append('\n');
        }
        return sb.ToString();
    }

    private static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
