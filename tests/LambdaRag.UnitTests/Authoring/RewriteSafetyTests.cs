using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Authoring.Editing;
using LambdaRag.Cli;
using LambdaRag.Core.Domain;
using LambdaRag.Markup;
using Xunit;

namespace LambdaRag.UnitTests.Authoring;

public sealed class RewriteSafetyTests
{
    private const string Clause = "The vendor may retain customer data for any period it chooses without limit.";
    private const string Doc = "1. Retention\n" + Clause + "\nOther unrelated clause text follows here.";

    private static readonly int ClauseStart = Doc.IndexOf(Clause, StringComparison.Ordinal);

    [Fact]
    public void Validate_accepts_minimal_edit()
    {
        var o = ComplianceEditor.Validate(Clause,
            "The vendor may retain customer data for no longer than 90 days without limit.", 1000);
        o.IsAccepted.Should().BeTrue();
    }

    [Theory]
    [InlineData("NO_REWRITE", RewriteSkipReason.Declined)]
    [InlineData("  no_rewrite  ", RewriteSkipReason.Declined)]
    [InlineData("\"NO_REWRITE\"", RewriteSkipReason.Declined)]
    [InlineData("", RewriteSkipReason.EmptyResponse)]
    [InlineData(null, RewriteSkipReason.EmptyResponse)]
    [InlineData(Clause, RewriteSkipReason.Unchanged)]
    public void Validate_rejects_decline_empty_and_unchanged(string? raw, RewriteSkipReason expected)
    {
        var o = ComplianceEditor.Validate(Clause, raw, 1000);
        o.IsAccepted.Should().BeFalse();
        o.Reason.Should().Be(expected);
    }

    [Fact]
    public void Validate_rejects_over_broad_replacement()
    {
        ComplianceEditor.Validate(Clause, new string('x', 500) + " " + Clause, 1000)
            .Reason.Should().Be(RewriteSkipReason.OverBroad);
        ComplianceEditor.Validate(Clause, "Completely different wording replaces everything here now.", 1000)
            .Reason.Should().Be(RewriteSkipReason.OverBroad);
    }

    [Fact]
    public void Validate_rejects_structural_change()
    {
        ComplianceEditor.Validate(Clause, Clause.Replace("customer data", "customer data\nfor 90 days"), 1000)
            .Reason.Should().Be(RewriteSkipReason.StructureChanged);
        ComplianceEditor.Validate("a b c d e f\ng h i j k l", "a b c d e f g h i j k l x", 1000)
            .Reason.Should().Be(RewriteSkipReason.StructureChanged);
        ComplianceEditor.Validate(Clause, "- " + Clause.Replace("any period", "90 days"), 1000)
            .Reason.Should().Be(RewriteSkipReason.StructureChanged);
    }

    [Fact]
    public void Prompt_contract_allows_decline_and_does_not_force_rewrite()
    {
        ComplianceEditor.SystemPrompt.Should().Contain("NO_REWRITE");
        ComplianceEditor.SystemPrompt.Should().NotContain("Always produce a rewrite");
    }

    [Fact]
    public void Cache_key_depends_on_prompt_contract()
    {
        var v = Verdict("v1", "r1", ClauseStart, Clause.Length);
        var key = ComplianceEditor.ComputeCacheKey(null, v, Clause);
        key.Should().NotBe(ComplianceEditor.ComputeCacheKey(null, v, Clause + " "));
        ComplianceEditor.PromptFingerprint.Should().MatchRegex("^[0-9a-f]{64}$");
        ComplianceEditor.CacheSchemaVersion.Should().NotBe("v1");
    }

    [Fact]
    public async Task Planner_emits_replace_for_valid_rewrite()
    {
        var stub = new StubRewriter(_ => RewriteOutcome.Accepted(
            "The vendor may retain customer data for no longer than 90 days without limit."));
        var plan = await Plan(stub, Verdict("v1", "r1", ClauseStart, Clause.Length));

        plan.Emitted.Should().Be(1);
        plan.Annotations.Should().ContainSingle(a => a.Kind == AnnotationKind.Replace);
        plan.Skips.Should().BeEmpty();
    }

    [Fact]
    public async Task Planner_keeps_comment_when_editor_declines()
    {
        var stub = new StubRewriter(_ => RewriteOutcome.Rejected(RewriteSkipReason.Declined));
        var plan = await Plan(stub, Verdict("v1", "r1", ClauseStart, Clause.Length));

        plan.Emitted.Should().Be(0);
        plan.Annotations.Should().ContainSingle().Which.Kind.Should().Be(AnnotationKind.Comment);
        plan.Skips.Should().ContainSingle().Which.Reason.Should().Be(RewriteSkipReason.Declined);
    }

    [Fact]
    public async Task Planner_rejects_invalid_and_over_broad_text_from_a_plain_rewriter()
    {
        var plain = new PlainRewriter("NO_REWRITE");
        var p1 = await Plan(plain, Verdict("v1", "r1", ClauseStart, Clause.Length));
        p1.Skips.Single().Reason.Should().Be(RewriteSkipReason.Declined);

        var broad = new PlainRewriter(new string('y', 900));
        var p2 = await Plan(broad, Verdict("v1", "r1", ClauseStart, Clause.Length));
        p2.Skips.Single().Reason.Should().Be(RewriteSkipReason.OverBroad);
        p2.Annotations.Should().OnlyContain(a => a.Kind == AnnotationKind.Comment);
    }

    [Fact]
    public async Task Planner_does_not_call_rewriter_for_missing_or_ambiguous_target()
    {
        var stub = new StubRewriter(_ => RewriteOutcome.Accepted("never"));
        var outOfRange = Verdict("v1", "r1", Doc.Length + 5, 10);
        var wrongEvidence = Verdict("v2", "r2", ClauseStart, Clause.Length, quote: "text that is not in the clause");
        var plan = await Plan(stub, outOfRange, wrongEvidence);

        stub.Calls.Should().Be(0);
        plan.Skips.Should().HaveCount(2).And.OnlyContain(s => s.Reason == RewriteSkipReason.AmbiguousTarget);
        plan.Annotations.Should().HaveCount(2).And.OnlyContain(a => a.Kind == AnnotationKind.Comment);
    }

    [Fact]
    public async Task Planner_does_not_rewrite_without_a_precise_clause_span()
    {
        var stub = new StubRewriter(_ => RewriteOutcome.Accepted("never"));
        var finding = Verdict("v1", "r1", ClauseStart, Clause.Length) with { ClauseSpan = null };
        var plan = await Plan(stub, finding);

        stub.Calls.Should().Be(0);
        plan.Skips.Should().ContainSingle().Which.Reason.Should().Be(RewriteSkipReason.NoClauseText);
        plan.Annotations.Should().ContainSingle().Which.Kind.Should().Be(AnnotationKind.Comment);
    }

    [Fact]
    public async Task Planner_fails_closed_for_oversized_clause()
    {
        var big = new string('a', RewritePlanner.MaxClauseChars + 50);
        var stub = new StubRewriter(_ => RewriteOutcome.Accepted("never"));
        var plan = await new RewritePlanner(stub, Rules(), big)
            .PlanAsync(new[] { Verdict("v1", "r1", 0, big.Length, quote: null) });

        stub.Calls.Should().Be(0);
        plan.Skips.Single().Reason.Should().Be(RewriteSkipReason.UnrelatedContentAtRisk);
    }

    [Fact]
    public async Task Planner_blocks_shared_span_with_incompatible_obligations()
    {
        var stub = new StubRewriter(_ => RewriteOutcome.Accepted("never"));
        var plan = await Plan(stub,
            Verdict("v1", "r1", ClauseStart, Clause.Length),
            Verdict("v2", "r2", ClauseStart, Clause.Length));

        stub.Calls.Should().Be(0);
        plan.Skips.Should().HaveCount(2).And.OnlyContain(s => s.Reason == RewriteSkipReason.SharedIncompatibleSpan);
        plan.Annotations.Should().HaveCount(2).And.OnlyContain(a => a.Kind == AnnotationKind.Comment);
    }

    [Fact]
    public async Task Planner_allows_one_rewrite_for_shared_span_of_same_obligation()
    {
        var stub = new StubRewriter(_ => RewriteOutcome.Accepted(
            "The vendor may retain customer data for no longer than 90 days without limit."));
        var rules = Rules(sameObligation: true);
        var plan = await new RewritePlanner(stub, rules, Doc).PlanAsync(new[]
        {
            Verdict("v1", "r1", ClauseStart, Clause.Length),
            Verdict("v2", "r2", ClauseStart, Clause.Length),
        });

        stub.Calls.Should().Be(1);
        plan.Annotations.Count(a => a.Kind == AnnotationKind.Replace).Should().Be(1);
        plan.Skips.Should().ContainSingle().Which.VerdictId.Should().Be("v2");
    }

    [Fact]
    public async Task Planner_is_deterministic_across_runs()
    {
        var verdicts = new[]
        {
            Verdict("v1", "r1", ClauseStart, Clause.Length),
            Verdict("v2", "r2", ClauseStart, Clause.Length),
        };
        var a = await Plan(new StubRewriter(_ => RewriteOutcome.Rejected(RewriteSkipReason.Declined)), verdicts);
        var b = await Plan(new StubRewriter(_ => RewriteOutcome.Rejected(RewriteSkipReason.Declined)), verdicts);

        a.Annotations.Select(x => x.Id).Should().Equal(b.Annotations.Select(x => x.Id));
        a.Skips.Should().Equal(b.Skips);
    }

    private static Task<RewritePlanResult> Plan(IClauseRewriter rewriter, params Verdict[] verdicts)
        => new RewritePlanner(rewriter, Rules(), Doc).PlanAsync(verdicts);

    private static Verdict Verdict(string id, string ruleId, int start, int len, string? quote = "retain customer data")
        => new(
            Id: id,
            RuleId: ruleId,
            RuleSetVersion: "1.0",
            Outcome: VerdictOutcome.Fail,
            LambdaText: "false",
            EvaluatedInput: new JsonObject(),
            SourceSpan: new SourceSpan("doc", start, len, null, "/1. Retention"),
            ErrorMessage: null,
            EvidenceQuotes: quote is null ? Array.Empty<string>() : new[] { quote },
            EvaluatedAt: DateTimeOffset.UnixEpoch)
        {
            RemediationText = "limit retention",
            ClauseSpan = new SourceSpan("doc", start, len, null, "/1. Retention"),
        };

    private static Dictionary<string, Rule> Rules(bool sameObligation = false)
        => new(StringComparer.Ordinal)
        {
            ["r1"] = MakeRule("r1", "Retention must be limited."),
            ["r2"] = MakeRule("r2", sameObligation ? "retention  must be LIMITED." : "Vendor must encrypt data."),
        };

    private static Rule MakeRule(string id, string text)
    {
        var ctor = typeof(Rule).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();
        var args = ctor.GetParameters().Select(p => p.Name switch
        {
            "Id" => (object?)id,
            "Version" => "1",
            "NaturalLanguage" => text,
            _ => DefaultFor(p.ParameterType),
        }).ToArray();
        return (Rule)ctor.Invoke(args);
    }

    private static object? DefaultFor(Type t)
    {
        if (t == typeof(string)) return string.Empty;
        if (t.IsValueType) return Activator.CreateInstance(t);
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(t.GetGenericArguments()));
        if (t == typeof(IReadOnlyDictionary<string, string>)) return new Dictionary<string, string>();
        return null;
    }

    private sealed class StubRewriter(Func<Verdict, RewriteOutcome> respond) : IClauseRewriter, IReasonedClauseRewriter
    {
        public int Calls { get; private set; }

        public Task<RewriteOutcome> RewriteWithReasonAsync(
            Verdict verdict, string clauseText, Rule? rule, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(respond(verdict));
        }

        public async Task<string?> RewriteAsync(
            Verdict verdict, string clauseText, Rule? rule, CancellationToken cancellationToken = default)
            => (await RewriteWithReasonAsync(verdict, clauseText, rule, cancellationToken)).Text;
    }

    private sealed class PlainRewriter(string text) : IClauseRewriter
    {
        public Task<string?> RewriteAsync(
            Verdict verdict, string clauseText, Rule? rule, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(text);
    }
}
