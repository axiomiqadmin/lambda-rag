using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Selectors;
using LambdaRag.Evaluation.Engine;
using LambdaRag.Selectors;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Evaluation;

/// <summary>
/// Review-annotation-quality contract §1/§3 (evaluation side): rule scope,
/// contradiction vs. missing requirement, and evidence/clause span honesty.
/// </summary>
public class ScopedFindingTests
{
    private static EvaluationService Build() => new(
        new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance),
        NullLogger<EvaluationService>.Instance,
        TimeProvider.System);

    private static ProjectedDocument Doc(params (string id, string text)[] sections)
    {
        var arr = new JsonArray();
        var offset = 0;
        foreach (var (id, text) in sections)
        {
            arr.Add(new JsonObject
            {
                ["id"] = id,
                ["category"] = "body",
                ["heading"] = id,
                ["text"] = text,
                ["text_char_start"] = offset,
                ["paragraphs"] = new JsonArray(new JsonObject
                {
                    ["char_start"] = 0,
                    ["char_length"] = text.Length,
                }),
            });
            offset += text.Length + 10;
        }
        return new ProjectedDocument(
            ContentHash.OfString("doc-bytes"),
            "test-projector",
            "1.0",
            new JsonObject { ["sections"] = arr },
            new Dictionary<string, SourceSpan>(StringComparer.Ordinal));
    }

    private static Rule BroadRule(string id, string evidence, string lambda, string? selector = null) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: evidence,
        Lambda: lambda,
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector(selector ?? "$.sections[*]"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("policy", 0, 0, 1, null),
        EvidenceQuote: evidence,
        Metadata: new Dictionary<string, string>())
    {
        Predicate = "true",
    };

    private static RuleSet Set(params Rule[] rules) => new(
        Id: "rs-scope",
        Version: "1.0.0",
        Domain: "test",
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: rules,
        Metadata: new Dictionary<string, string>());

    [Fact]
    public async Task Rule_fan_out_unrelated_sections_produce_no_findings()
    {
        var rule = BroadRule(
            "R-AKS",
            "Pods SHALL use workload identity federation for cloud API access",
            "input1.text.Contains(\"workload identity\")");
        var sections = Enumerable.Range(1, 12)
            .Select(i => ($"s{i}", $"Section {i} describes the billing cadence and invoice format."))
            .ToArray();

        var report = await Build().EvaluateAsync(Set(rule), Doc(sections));

        report.Verdicts.Should().ContainSingle();
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Gap);
        report.Verdicts[0].ErrorMessage.Should().StartWith("scope:not_established");
        report.Failed.Should().Be(0);
        report.Gaps.Should().Be(1);
    }

    [Fact]
    public async Task Missing_requirement_in_scope_is_a_gap_without_fabricated_quote()
    {
        var rule = BroadRule(
            "R-WIF",
            "Pods SHALL use workload identity federation",
            "input1.text.Contains(\"workload identity federation\")");
        var doc = Doc(("s1", "Pods use workload identity today but keep static service principal keys for cloud APIs."));

        var report = await Build().EvaluateAsync(Set(rule), doc);

        var v = report.Verdicts.Should().ContainSingle().Subject;
        v.Outcome.Should().Be(VerdictOutcome.Gap);
        v.MatchedSectionId.Should().BeNull("an inferred-scope omission is document-level, not a section defect");
        v.AnchorKind.Should().NotBe(AnchorKinds.SectionScope);
        v.RewriteEligible.Should().NotBe(true);
        v.ClauseSpan.Should().BeNull("an unresolved target must never widen to the whole section");
    }

    [Fact]
    public async Task Missing_requirement_is_dropped_when_another_section_satisfies_a_match_all_rule()
    {
        var rule = BroadRule(
            "R-WIF",
            "Pods SHALL use workload identity federation",
            "input1.text.Contains(\"workload identity federation\")");
        var doc = Doc(
            ("s1", "Pods use workload identity today but keep static service principal keys."),
            ("s2", "Pods use workload identity federation for cloud APIs."));

        var report = await Build().EvaluateAsync(Set(rule), doc);

        var v = report.Verdicts.Should().ContainSingle().Subject;
        v.Outcome.Should().Be(VerdictOutcome.Pass);
        report.Gaps.Should().Be(0);
    }

    [Fact]
    public async Task Explicit_contradiction_fails_with_span_over_the_contradictory_text()
    {
        var rule = BroadRule(
            "R-NOSECRET",
            "Pods SHALL NOT store long-lived secrets",
            "!input1.text.ToLower().Contains(\"long-lived secrets\")");
        const string text = "Pods authenticate to cloud APIs using long-lived secrets stored in config.";
        var doc = Doc(("s1", text));

        var report = await Build().EvaluateAsync(Set(rule), doc);

        var v = report.Verdicts.Should().ContainSingle().Subject;
        v.Outcome.Should().Be(VerdictOutcome.Fail);
        v.FindingKind.Should().Be(FindingKinds.Contradiction);
        v.AnchorKind.Should().Be(AnchorKinds.ContradictoryText);
        v.RewriteEligible.Should().BeTrue();
        var idx = text.IndexOf("long-lived secrets", StringComparison.Ordinal);
        v.SourceSpan.CharStart.Should().Be(idx);
        v.SourceSpan.CharLength.Should().Be("long-lived secrets".Length);
        v.ClauseSpan.Should().NotBeNull();
        v.ClauseSpan!.CharStart.Should().BeLessThanOrEqualTo(v.SourceSpan.CharStart);
        v.ClauseKind.Should().Be(ClauseKinds.Paragraph);
    }

    [Fact]
    public async Task Same_rule_failing_at_multiple_locations_keeps_distinct_anchors()
    {
        var rule = BroadRule(
            "R-NOSECRET",
            "Pods SHALL NOT store long-lived secrets",
            "!input1.text.ToLower().Contains(\"long-lived secrets\")");
        var doc = Doc(
            ("s1", "Pods rely on long-lived secrets for the registry."),
            ("s2", "Build agents also keep long-lived secrets in pipelines."));

        var report = await Build().EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().HaveCount(2);
        report.Verdicts.Should().OnlyContain(v => v.Outcome == VerdictOutcome.Fail);
        report.Verdicts.Select(v => v.SourceSpan.CharStart).Distinct().Should().HaveCount(2);
        report.Verdicts.Select(v => v.Id).Distinct().Should().HaveCount(2);
    }

    [Fact]
    public async Task Unresolvable_lambda_fail_is_not_anchored_to_the_heading_and_is_not_rewritable()
    {
        var rule = BroadRule(
            "R-LEN",
            "Retention SHALL exceed seven years",
            "input1.text.Length > 500",
            selector: "$.sections[?(@.id=='s1')]");
        var doc = Doc(("s1", "Retention is short."));

        var report = await Build().EvaluateAsync(Set(rule), doc);

        var v = report.Verdicts.Should().ContainSingle().Subject;
        v.Outcome.Should().Be(VerdictOutcome.Fail);
        v.ScopeBasis.Should().Be(ScopeBases.SelectorScoped);
        v.AnchorKind.Should().Be(AnchorKinds.Unresolved);
        v.UnanchoredReason.Should().Be(UnanchoredReasons.NoLiteralEvidence);
        v.RewriteEligible.Should().BeFalse();
        v.ClauseSpan.Should().BeNull();
    }

    [Fact]
    public async Task Long_paragraph_clause_narrows_to_the_enclosing_sentence()
    {
        var rule = BroadRule(
            "R-NOSECRET",
            "Pods SHALL NOT store long-lived secrets",
            "!input1.text.ToLower().Contains(\"long-lived secrets\")");
        var filler = string.Join(" ", Enumerable.Repeat("Unrelated filler sentence about logging.", 30));
        var text = filler + " Pods keep long-lived secrets in config. " + filler;
        var doc = Doc(("s1", text));

        var report = await Build().EvaluateAsync(Set(rule), doc);

        var v = report.Verdicts.Should().ContainSingle().Subject;
        v.ClauseKind.Should().Be(ClauseKinds.Sentence);
        v.ClauseSpan!.CharLength.Should().BeLessThan(200);
        v.ClauseSpan.CharStart.Should().BeLessThanOrEqualTo(v.SourceSpan.CharStart);
        (v.ClauseSpan.CharStart + v.ClauseSpan.CharLength)
            .Should().BeGreaterThanOrEqualTo(v.SourceSpan.CharStart + v.SourceSpan.CharLength);
    }

    [Fact]
    public async Task Authored_scope_terms_gate_sections_and_do_not_bypass_predicate()
    {
        var rule = BroadRule("R-SCOPE", "x", "input1.text.Contains(\"encrypted\")") with
        {
            Scope = new RuleScope(["Kubernetes", "AKS"], "container workloads"),
            RequiredEvidence = ["encryption at rest is stated"],
        };
        var doc = Doc(
            ("s1", "Billing runs monthly."),
            ("s2", "The aks cluster stores data on disks."),
            ("s3", "Kubernetes secrets are encrypted."));

        var report = await Build().EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().HaveCount(2);
        report.Verdicts.Select(v => v.MatchedSectionId).Should().BeEquivalentTo("s2", "s3");
        var gap = report.Verdicts.Single(v => v.MatchedSectionId == "s2");
        gap.Outcome.Should().Be(VerdictOutcome.Gap);
        gap.ScopeBasis.Should().Be(ScopeBases.AuthoredScope);
        gap.RemediationText.Should().Be("Document must state: encryption at rest is stated");
        report.Verdicts.Single(v => v.MatchedSectionId == "s3").Outcome.Should().Be(VerdictOutcome.Pass);

        // A false predicate still excludes an in-terms section.
        var gated = rule with { Predicate = "input1.category == \"other\"" };
        var gatedReport = await Build().EvaluateAsync(Set(gated), doc);
        gatedReport.Verdicts.Should().ContainSingle().Which.Outcome.Should().Be(VerdictOutcome.Gap);
        gatedReport.Verdicts[0].MatchedSectionId.Should().BeNull("no section passed the predicate");
    }

    [Fact]
    public async Task Authored_scope_matching_no_section_yields_gap()
    {
        var rule = BroadRule("R-SCOPE", "x", "input1.text.Contains(\"encrypted\")") with
        {
            Scope = new RuleScope(["Kubernetes"]),
        };

        var report = await Build().EvaluateAsync(Set(rule), Doc(("s1", "Billing runs monthly.")));

        report.Verdicts.Should().ContainSingle().Which.Outcome.Should().Be(VerdictOutcome.Gap);
        report.Failed.Should().Be(0);
        report.Gaps.Should().Be(1);
    }

    [Fact]
    public async Task Prohibited_literal_inside_negated_group_is_a_contradiction()
    {
        var rule = BroadRule(
            "R-CONSENT",
            "Consent SHALL be specific and not bundled",
            "input1.text.Contains(\"specific\") && !(input1.text.Contains(\"consent to all\"))");
        const string text = "By creating an account you consent to all of the processing, and this consent is specific.";

        var report = await Build().EvaluateAsync(Set(rule), Doc(("s1", text)));

        var v = report.Verdicts.Should().ContainSingle().Subject;
        v.Outcome.Should().Be(VerdictOutcome.Fail);
        v.FindingKind.Should().Be(FindingKinds.Contradiction);
        v.SourceSpan.CharStart.Should().Be(text.IndexOf("consent to all", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evaluation_is_deterministic_for_identical_inputs()
    {
        var rule = BroadRule(
            "R-NOSECRET",
            "Pods SHALL NOT store long-lived secrets",
            "!input1.text.ToLower().Contains(\"long-lived secrets\")");
        var doc = Doc(("s1", "Pods rely on long-lived secrets."), ("s2", "Unrelated billing text."));

        var a = await Build().EvaluateAsync(Set(rule), doc);
        var b = await Build().EvaluateAsync(Set(rule), doc);

        a.Verdicts.Select(v => (v.Id, v.Outcome, v.AnchorKind, v.SourceSpan.CharStart))
            .Should().Equal(b.Verdicts.Select(v => (v.Id, v.Outcome, v.AnchorKind, v.SourceSpan.CharStart)));
    }
}
