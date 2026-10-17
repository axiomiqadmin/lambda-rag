using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using LambdaRag.Markup;
using Xunit;

namespace LambdaRag.UnitTests.Markup;

public sealed class NotApplicableClassificationTests
{
    private static Rule MakeRule(string text, string? slug = null) => new(
        Id: "R1",
        Version: "1.0.0",
        NaturalLanguage: text,
        Lambda: "true",
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("policy", 0, 0, 1, null),
        EvidenceQuote: text,
        Metadata: slug is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["topicSlug"] = slug });

    [Fact]
    public void Explicit_exclusion_of_named_technology_is_EXCLUDED_TECH_NOT_REQUIRED()
    {
        var rule = MakeRule("Kubernetes pods SHALL use workload identity");
        var (category, reason) = NotApplicableAppendix.Classify(
            rule, AbsenceEvidence.ExcludedTagPrefix + "Kubernetes is not used.");

        category.Should().Be(NotApplicableCategory.ExcludedTechNotRequired);
        NotApplicableAppendix.CategoryLabel(category).Should().Be("EXCLUDED_TECH_NOT_REQUIRED");
        reason.Should().Contain("Kubernetes is not used.");
    }

    [Fact]
    public void Explicit_exclusion_without_technology_is_EXCLUDED_NOT_REQUIRED()
    {
        var rule = MakeRule("batch jobs shall retry failed uploads");
        var (category, _) = NotApplicableAppendix.Classify(
            rule, AbsenceEvidence.ExcludedTagPrefix + "There are no batch jobs.");

        category.Should().Be(NotApplicableCategory.ExcludedNotRequired);
        NotApplicableAppendix.CategoryLabel(category).Should().Be("EXCLUDED_NOT_REQUIRED");
    }

    [Fact]
    public void Silence_about_a_technology_is_never_classified_as_excluded()
    {
        var rule = MakeRule("Kubernetes pods SHALL use workload identity");
        var (category, _) = NotApplicableAppendix.Classify(rule, null);

        category.Should().NotBe(NotApplicableCategory.ExcludedTechNotRequired);
        category.Should().NotBe(NotApplicableCategory.ExcludedNotRequired);
    }

    [Fact]
    public void FindExclusion_ignores_negations_about_unrelated_subjects()
    {
        var tenancy = MakeRule("SaaS tenancy model SHALL isolate tenant data across services", "saas-tenancy");
        AbsenceEvidence.FindExclusion(tenancy, new[] { "There are no cross-service joins across tenant data." })
            .Should().BeNull();

        var retry = MakeRule("SDK clients SHALL configure retry policies", "sdk-retry");
        AbsenceEvidence.FindExclusion(retry, new[] { "Managed Identity is used, with no stored credentials." })
            .Should().BeNull();
        AbsenceEvidence.FindExclusion(retry, new[] { "No SDK retry logic is used; clients are not called." })
            .Should().NotBeNull();
    }

    [Fact]
    public void FindExclusion_ignores_coarse_single_word_slug()
    {
        var rule = MakeRule("Database fields holding direct identifiers SHALL use tokenization", "DATA");
        AbsenceEvidence.FindExclusion(rule, new[] { "Platform Engineer role has no access to customer data." })
            .Should().BeNull();
    }

    [Fact]
    public void FindExclusion_requires_negation_and_topic_overlap()
    {
        var rule = MakeRule("Idle resources SHALL be deleted within thirty days");
        AbsenceEvidence.FindExclusion(rule, new[] { "Idle resources are reported weekly." }).Should().BeNull();
        AbsenceEvidence.FindExclusion(rule, new[] { "Idle resources are out of scope for this design." })
            .Should().NotBeNull();
    }
}
