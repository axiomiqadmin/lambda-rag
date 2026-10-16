using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Markup;
using Xunit;

namespace LambdaRag.UnitTests.Markup;

/// <summary>
/// Aggregation at annotation construction: coalesce only on the same
/// normalized obligation + same target location.
/// </summary>
public sealed class FindingAggregationTests
{
    private static Rule MakeRule(string id, string nl, RuleSeverity severity = RuleSeverity.Violation) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: nl,
        Lambda: "true",
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: severity,
        SourceSpan: new SourceSpan("doc", 0, 0, 1, null),
        EvidenceQuote: id,
        Metadata: new Dictionary<string, string>())
    {
        Predicate = "true",
        SeverityBasis = severity is RuleSeverity.Violation or RuleSeverity.Critical
            ? "Documented material impact basis for this test rule."
            : null,
    };

    private static Verdict MakeVerdict(
        string ruleId, int start, int length, string? remediation = null,
        string[]? quotes = null, SourceSpan? clause = null) => new(
        Id: ContentHash.Compose("verdict", ruleId, start.ToString(), length.ToString()).Value,
        RuleId: ruleId,
        RuleSetVersion: "1.0.0",
        Outcome: VerdictOutcome.Fail,
        LambdaText: "true",
        EvaluatedInput: new JsonObject(),
        SourceSpan: new SourceSpan("doc", start, length, 1, null),
        ErrorMessage: null,
        EvidenceQuotes: quotes ?? new[] { "quote" },
        EvaluatedAt: DateTimeOffset.UnixEpoch)
        {
            RemediationText = remediation,
            ClauseSpan = clause,
        };

    private static IReadOnlyDictionary<string, Rule> Rules(params Rule[] rules)
        => rules.ToDictionary(r => r.Id, StringComparer.Ordinal);

    [Fact]
    public void Same_obligation_same_location_is_one_comment_keeping_every_rule_and_remediation()
    {
        var rules = Rules(
            MakeRule("R-B", "Data MUST be encrypted at rest."),
            MakeRule("R-A", "data must be  encrypted at rest"));
        var verdicts = new[]
        {
            MakeVerdict("R-B", 10, 20, "Use AES-256."),
            MakeVerdict("R-A", 10, 20, "Enable storage encryption."),
        };

        var groups = AnnotationFactory.GroupFindings(verdicts, rules);

        groups.Should().ContainSingle();
        var c = groups[0].Comment;
        c.RuleIds.Should().Equal("R-A", "R-B");
        c.VerdictIds.Should().HaveCount(2);
        c.Text.Should().Contain("[Policy Reference: R-A v1.0.0]")
              .And.Contain("[Policy Reference: R-B v1.0.0]")
              .And.Contain("Use AES-256.")
              .And.Contain("Enable storage encryption.");
        c.Reasons.Should().ContainSingle(r => r.StartsWith("grouped:same-obligation-same-location:rules=R-A,R-B"));
    }

    [Fact]
    public void Distinct_obligations_at_same_location_stay_separate_and_are_marked_as_sharing_a_clause()
    {
        var clause = new SourceSpan("doc", 0, 200, 1, null);
        var rules = Rules(
            MakeRule("R-1", "Data MUST be encrypted."),
            MakeRule("R-2", "Backups MUST be tested quarterly."));
        var verdicts = new[]
        {
            MakeVerdict("R-1", 10, 20, clause: clause),
            MakeVerdict("R-2", 10, 20, clause: clause),
        };

        var groups = AnnotationFactory.GroupFindings(verdicts, rules);

        groups.Should().HaveCount(2);
        groups.Should().OnlyContain(g => !g.IsGrouped && g.ClauseSharedWithOtherGroups);
        groups.Select(g => g.Comment.RuleIds.Single()).Should().Equal("R-1", "R-2");
    }

    [Fact]
    public void Same_rule_at_different_locations_yields_separate_comments()
    {
        var rules = Rules(MakeRule("R-1", "Data MUST be encrypted."));
        var verdicts = new[]
        {
            MakeVerdict("R-1", 10, 20),
            MakeVerdict("R-1", 500, 20),
        };

        var groups = AnnotationFactory.GroupFindings(verdicts, rules);

        groups.Should().HaveCount(2);
        groups.Select(g => g.Comment.Span.CharStart).Should().Equal(10, 500);
        groups.Select(g => g.Comment.Id).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public void Single_finding_comment_is_unchanged_and_keeps_the_major_banner()
    {
        var rule = MakeRule("R-1", "Data MUST be encrypted.");
        var verdict = MakeVerdict("R-1", 10, 20, "Fix it.");

        var group = AnnotationFactory.GroupFindings(new[] { verdict }, Rules(rule)).Single();

        group.Comment.Text.Should().Be(CommentFormatting.BuildBody(rule, verdict));
        group.Comment.Text.Should().StartWith(CommentFormatting.SeverityBanner(RuleSeverity.Violation));
        group.Comment.Id.Should().Be(ContentHash.Compose("annot", verdict.Id, "comment").Value);
    }

    [Fact]
    public void Grouped_comment_uses_most_severe_banner_of_its_members()
    {
        var rules = Rules(
            MakeRule("R-1", "Data MUST be encrypted.", RuleSeverity.Suggestion),
            MakeRule("R-2", "Data must be encrypted", RuleSeverity.Critical));
        var group = AnnotationFactory.GroupFindings(
            new[] { MakeVerdict("R-1", 0, 5), MakeVerdict("R-2", 0, 5) }, rules).Single();

        group.Comment.Text.Should().StartWith(CommentFormatting.SeverityBanner(RuleSeverity.Critical));
    }

    [Fact]
    public void Fail_without_evidence_quote_is_marked_as_scoped_section_anchor_with_reason()
    {
        var rules = Rules(MakeRule("R-1", "Data MUST be encrypted."));
        var group = AnnotationFactory.GroupFindings(
            new[] { MakeVerdict("R-1", 0, 22, quotes: Array.Empty<string>()) }, rules).Single();

        group.Comment.AnchorKind.Should().Be(AnnotationAnchorKind.ScopedSection);
        group.Comment.Reasons.Should().Contain("anchor:scoped-section:no-evidence-quote");
    }

    [Fact]
    public void Replace_annotation_keeps_group_metadata_and_clause_span()
    {
        var clause = new SourceSpan("doc", 0, 100, 1, null);
        var rules = Rules(MakeRule("R-1", "Data MUST be encrypted."), MakeRule("R-2", "Data must be encrypted"));
        var group = AnnotationFactory.GroupFindings(
            new[] { MakeVerdict("R-1", 5, 10, clause: clause), MakeVerdict("R-2", 5, 10, clause: clause) }, rules).Single();

        var replace = AnnotationFactory.BuildReplaceAnnotation(group, "New text.");

        replace.Kind.Should().Be(AnnotationKind.Replace);
        replace.ClauseSpan.Should().Be(clause);
        replace.RuleIds.Should().Equal("R-1", "R-2");
        replace.Reasons.Should().ContainSingle(r => r.StartsWith("grouped:"));
    }

    [Fact]
    public void Grouping_is_deterministic_regardless_of_rule_enumeration_order()
    {
        var rules = Rules(MakeRule("R-1", "Data MUST be encrypted."), MakeRule("R-2", "Data must be encrypted"));
        var a = AnnotationFactory.GroupFindings(
            new[] { MakeVerdict("R-1", 5, 10), MakeVerdict("R-2", 5, 10) }, rules).Single().Comment;
        var b = AnnotationFactory.GroupFindings(
            new[] { MakeVerdict("R-2", 5, 10), MakeVerdict("R-1", 5, 10) }, rules).Single().Comment;

        b.Id.Should().Be(a.Id);
        b.Text.Should().Be(a.Text);
    }
}
