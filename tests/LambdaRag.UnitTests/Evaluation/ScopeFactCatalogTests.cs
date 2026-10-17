using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using Xunit;

namespace LambdaRag.UnitTests.Evaluation;

public class ScopeFactCatalogTests
{
    private static Rule R(string id, params (string Name, string[] Values)[] facts) =>
        Plain(id) with
        {
            AppliesWhen = facts.ToDictionary(f => f.Name, f => (IReadOnlyList<string>)f.Values),
        };

    private static Rule Plain(string id) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: "x",
        Lambda: "true",
        AppliesToSchema: new System.Text.Json.Nodes.JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("policy", 0, 0, 1, null),
        EvidenceQuote: "x",
        Metadata: new Dictionary<string, string>());

    [Fact]
    public void Build_is_order_independent_and_deduplicates_declarations()
    {
        var a = R("A", ("platform", ["cluster", "k8s"]));
        var b = R("B", ("platform", ["k8s", "cluster"]));
        var c = R("C", ("provider", ["acme"]));

        var one = ScopeFactCatalog.Build([a, b, c]);
        var two = ScopeFactCatalog.Build([c, b, a]);

        one.Should().HaveCount(2);
        two.Select(f => (f.Name, string.Join("|", f.Values), string.Join("|", f.EvidencePhrases)))
            .Should().Equal(one.Select(f => (f.Name, string.Join("|", f.Values), string.Join("|", f.EvidencePhrases))));
    }

    [Fact]
    public void Build_emits_exclusive_phrase_templates_and_ignores_rules_without_applies_when()
    {
        var plain = Plain("P");
        var facts = ScopeFactCatalog.Build([plain, R("A", ("provider", ["acme"]))]);

        var fact = facts.Should().ContainSingle().Subject;
        fact.EvidencePhrases.Should().Contain("exclusively on acme");
    }
}
