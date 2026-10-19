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
/// Pillar 10 (#152) tests for the lexical applicability floor + rule-level
/// rollup in <see cref="EvaluationService"/>. The floor is opt-in and MUST
/// default to off so pre-Pillar-10 golden masters stay byte-identical; the
/// rule-level rollup is likewise opt-in via <c>emitRuleLevelStats</c> (or
/// implied by <c>applicabilityFloor &gt; 0</c>).
/// </summary>
public class ApplicabilityFloorTests
{
    private static readonly TimeProvider Frozen = new FrozenTime(
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));

    private sealed class FrozenTime(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }

    private static EvaluationService Build(
        double applicabilityFloor = 0.0,
        bool emitRuleLevelStats = false)
    {
        var matcher = new JsonPathSelectorMatcher(NullLogger<JsonPathSelectorMatcher>.Instance);
        return new EvaluationService(
            matcher,
            NullLogger<EvaluationService>.Instance,
            Frozen,
            applicabilityFloor: applicabilityFloor,
            emitRuleLevelStats: emitRuleLevelStats);
    }

    private static ProjectedDocument Doc(params (string id, string text)[] sections) =>
        DocWithCategory(sections.Select(s => (s.id, "body", s.text)).ToArray());

    private static ProjectedDocument DocWithCategory(params (string id, string category, string text)[] sections)
    {
        var arr = new JsonArray();
        foreach (var (id, category, text) in sections)
        {
            arr.Add(new JsonObject
            {
                ["id"] = id,
                ["category"] = category,
                ["text"] = text,
                ["heading"] = id,
            });
        }
        return new ProjectedDocument(
            ContentHash.OfString("doc-bytes"),
            "test-projector",
            "1.0",
            new JsonObject { ["sections"] = arr },
            new Dictionary<string, SourceSpan>(StringComparer.Ordinal));
    }

    private static Rule MakeBroadRule(
        string id,
        string evidenceQuote,
        string lambda) => new(
        Id: id,
        Version: "1.0.0",
        NaturalLanguage: evidenceQuote,
        Lambda: lambda,
        AppliesToSchema: new JsonObject(),
        Selector: new PathSelector("$.sections[*]"),
        Severity: RuleSeverity.Violation,
        SourceSpan: new SourceSpan("policy", 0, 0, 1, null),
        EvidenceQuote: evidenceQuote,
        Metadata: new Dictionary<string, string>())
    {
        // Mimics the FoundryRuleAuthoringAgent default: no semantic gate,
        // no predicate gate, broad selector. This is the exact shape that
        // motivated the Pillar 10 floor.
        Predicate = "true",
        Applicability = RuleApplicability.Mandatory,
    };

    private static RuleSet Set(params Rule[] rules) => new(
        Id: "rs-test",
        Version: "1.0.0",
        Domain: "test",
        PublishedAt: DateTimeOffset.UnixEpoch,
        Rules: rules,
        Metadata: new Dictionary<string, string>());

    // ── Byte-identity guard ─────────────────────────────────────────────

    [Fact]
    public async Task Floor_off_emits_no_rule_level_fields_byte_identity_preserved()
    {
        var rule = MakeBroadRule(
            "R-01",
            evidenceQuote: "ADRs SHALL be stored under docs/adr/",
            lambda: "input1.text.Contains(\"docs/adr/\")");
        var doc = Doc(
            ("s1", "The architecture uses docs/adr/ for decisions."),
            ("s2", "Latency budget is 200ms end to end."),
            ("s3", "Cost model uses reserved instances only."));

        var report = await Build().EvaluateAsync(Set(rule), doc);

        report.TotalUniqueRules.Should().BeNull("rule-level stats are opt-in");
        report.RulesPassed.Should().BeNull();
        report.RulesFailed.Should().BeNull();
        report.RuleScore.Should().BeNull();
        report.RuleSummaries.Should().BeNull();
        // Unrelated sections s2/s3 are out of scope and emit no verdict.
        report.Verdicts.Count.Should().Be(1);
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Pass);
    }

    // ── Applicability floor: silent-topic rule ──────────────────────────

    [Fact]
    public async Task Floor_on_silent_topic_becomes_Gap_not_NotApplicable()
    {
        // Rule about AKS pods. Doc has three sections, none mentioning
        // Kubernetes / pods / AKS. Silence is not proof the rule is
        // irrelevant, so a Mandatory rule is a Gap.
        var rule = MakeBroadRule(
            "EA-AKS-001",
            evidenceQuote: "Pods SHALL authenticate to cloud APIs using workload identity federation.",
            lambda: "input1.text.Contains(\"workload identity federation\")");
        var doc = Doc(
            ("s1", "The signing budget for contracts is fifteen days."),
            ("s2", "All PII fields are redacted before archival."),
            ("s3", "Auditor sign-off required prior to release."));

        var report = await Build(applicabilityFloor: 0.5, emitRuleLevelStats: true)
            .EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().ContainSingle();
        var only = report.Verdicts[0];
        only.Outcome.Should().Be(VerdictOutcome.Gap);
        only.ErrorMessage.Should().StartWith("applicability_floor:no_relevant_section");
        report.RulesGap.Should().Be(1);
        report.RulesNotApplicable.Should().Be(0);
    }

    [Fact]
    public async Task Floor_on_topic_explicitly_excluded_by_document_is_NotApplicable()
    {
        var rule = MakeBroadRule(
            "EA-AKS-001",
            evidenceQuote: "Pods SHALL authenticate to cloud APIs using workload identity federation.",
            lambda: "input1.text.Contains(\"workload identity federation\")");
        var doc = Doc(
            ("s1", "The signing budget for contracts is fifteen days."),
            ("s2", "Kubernetes pods are not deployed, so identity federation is out of scope."));

        var report = await Build(applicabilityFloor: 0.5, emitRuleLevelStats: true)
            .EvaluateAsync(Set(rule), doc);

        var only = report.Verdicts.Should().ContainSingle().Subject;
        only.Outcome.Should().Be(VerdictOutcome.NotApplicable);
        only.ErrorMessage.Should().StartWith("excluded:");
    }

    [Fact]
    public async Task Glossary_and_unrelated_sections_never_host_a_pass()
    {
        var rule = MakeBroadRule(
            "EA-DBAUTH",
            evidenceQuote: "Database authentication SHALL use workload identity / managed identity where supported",
            lambda: "input1.text.ToLower().Contains(\"workload identity\") || input1.text.ToLower().Contains(\"managed identity\")");
        var doc = DocWithCategory(
            ("s-glossary", "definitions", "MI: Managed Identity - workload identity requiring no stored credentials. Database: a data store."),
            ("s-svc", "body", "Service-to-service calls use Managed Identity tokens; no shared secrets."),
            ("s-db", "body", "Each service's Managed Identity is granted Cosmos DB access to its own database."));

        var report = await Build(applicabilityFloor: 0.2, emitRuleLevelStats: true)
            .EvaluateAsync(Set(rule), doc);

        var pass = report.Verdicts.Should().ContainSingle(v => v.Outcome == VerdictOutcome.Pass).Subject;
        pass.EvaluatedInput!["id"]!.GetValue<string>().Should().Be("s-db");
    }

    // ── Strict placement: sentence-level proof ──────────────────────────

    [Fact]
    public async Task Pass_is_anchored_on_the_single_proving_sentence()
    {
        var rule = MakeBroadRule(
            "EA-DBAUTH",
            evidenceQuote: "Database authentication SHALL use managed identity",
            lambda: "input1.text.ToLower().Contains(\"managed identity\")");
        var text = "The database tier is hosted in one region.\nDatabase access uses managed identity for every service. Backups run nightly.\nRestores are tested.";
        var doc = Doc(("s-db", text));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(Set(rule), doc);

        var pass = report.Verdicts.Should().ContainSingle(v => v.Outcome == VerdictOutcome.Pass).Subject;
        pass.AnchorKind.Should().Be(AnchorKinds.EvidenceSentence);
        pass.DocumentEvidence.Should().Be("Database access uses managed identity for every service. Backups run nightly.");
        pass.SourceSpan.CharLength.Should().Be(pass.DocumentEvidence!.Length);
    }

    [Fact]
    public async Task Pass_requires_the_only_qualifier_in_the_same_sentence()
    {
        var rule = MakeBroadRule(
            "EA-TLS",
            evidenceQuote: "External traffic SHALL use only TLS 1.2 or higher",
            lambda: "input1.text.ToLower().Contains(\"tls 1.2\")");
        var doc = Doc(("s-net", "External traffic is protected. Internal calls use TLS 1.2 or higher."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().NotContain(v => v.Outcome == VerdictOutcome.Pass);
        report.Verdicts.Should().Contain(v => v.Outcome == VerdictOutcome.Partial
            && v.ReasonCode == ReasonCodes.MissingRequiredQualifier);
    }

    [Fact]
    public async Task Authored_concepts_prove_a_pass_the_literal_lambda_misses()
    {
        var rule = MakeBroadRule(
            "EA-SEMVER",
            evidenceQuote: "Breaking changes SHALL increment the major version",
            lambda: "input1.text.ToLower().Contains(\"increment the major version\")") with
        {
            RequiredConcepts = [["breaking change"], ["new major version", "next major version"]],
        };
        var doc = Doc(("s-v", "Breaking changes require a new major version."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().ContainSingle(v => v.Outcome == VerdictOutcome.Pass)
            .Which.DocumentEvidence.Should().Contain("new major version");
    }

    [Fact]
    public async Task Enumerated_items_in_one_concept_group_must_all_be_present()
    {
        var rule = MakeBroadRule(
            "EA-IDEM",
            evidenceQuote: "GET, PUT and DELETE operations SHALL be idempotent",
            lambda: "input1.text.ToLower().Contains(\"zzz\")") with
        {
            RequiredConcepts = [["get", "put", "delete"], ["idempotent"]],
        };

        var partial = await Build(applicabilityFloor: 0.2)
            .EvaluateAsync(Set(rule), Doc(("s-a", "POST and PATCH are idempotent via keys, as is get.")));
        partial.Verdicts.Should().NotContain(v => v.Outcome == VerdictOutcome.Pass);

        var full = await Build(applicabilityFloor: 0.2)
            .EvaluateAsync(Set(rule), Doc(("s-b", "The get, put and delete operations are idempotent.")));
        full.Verdicts.Should().Contain(v => v.Outcome == VerdictOutcome.Pass);
    }

    [Fact]
    public async Task References_section_is_not_evidence()
    {
        var rule = MakeBroadRule(
            "EA-REF",
            evidenceQuote: "Breaking changes SHALL increment the major version",
            lambda: "input1.text.ToLower().Contains(\"increment the major version\")") with
        {
            RequiredConcepts = [["breaking change"], ["new major version"]],
        };
        var doc = Doc(("1.4 References", "Breaking changes require a new major version."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().NotContain(v => v.Outcome == VerdictOutcome.Pass);
    }

    [Fact]
    public async Task Missing_concept_group_does_not_pass()
    {
        var rule = MakeBroadRule(
            "EA-SEMVER2",
            evidenceQuote: "Breaking changes SHALL increment the major version",
            lambda: "input1.text.ToLower().Contains(\"increment the major version\")") with
        {
            RequiredConcepts = [["breaking change"], ["new major version"]],
        };
        var doc = Doc(("s-v", "Breaking changes are announced in release notes."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().NotContain(v => v.Outcome == VerdictOutcome.Pass);
    }

    [Fact]
    public async Task Scope_fact_established_by_document_evidence_makes_other_platform_rule_not_applicable()
    {
        var rule = MakeBroadRule(
            "EA-OTHER",
            evidenceQuote: "Cluster nodes SHALL be patched weekly",
            lambda: "input1.text.ToLower().Contains(\"patched weekly\")") with
        {
            AppliesWhen = new Dictionary<string, IReadOnlyList<string>> { ["platform"] = ["cluster"] },
        };
        var set = Set(rule) with
        {
            ScopeFacts = [new ScopeFact("platform", ["serverless"], ["runs exclusively on serverless"])],
        };
        var doc = Doc(("s-p", "The system runs exclusively on serverless functions."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(set, doc);

        var v = report.Verdicts.Should().ContainSingle().Subject;
        v.Outcome.Should().Be(VerdictOutcome.NotApplicable);
        v.ReasonCode.Should().Be(ReasonCodes.ScopeFactExcluded);
        v.ErrorMessage.Should().Contain("runs exclusively on serverless");
    }

    [Fact]
    public async Task Scope_fact_without_document_evidence_does_not_exclude_rule()
    {
        var rule = MakeBroadRule(
            "EA-OTHER2",
            evidenceQuote: "Cluster nodes SHALL be patched weekly",
            lambda: "input1.text.ToLower().Contains(\"patched weekly\")") with
        {
            AppliesWhen = new Dictionary<string, IReadOnlyList<string>> { ["platform"] = ["cluster"] },
        };
        var set = Set(rule) with
        {
            ScopeFacts = [new ScopeFact("platform", ["serverless"], ["runs exclusively on serverless"])],
        };
        var doc = Doc(("s-p", "The system is described below."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(set, doc);

        report.Verdicts.Should().NotContain(v => v.Outcome == VerdictOutcome.NotApplicable);
    }

    [Fact]
    public async Task Vendor_wide_product_statement_does_not_exclude_a_rule_about_one_of_its_products()
    {
        var rule = MakeBroadRule(
            "EA-VENDOR",
            evidenceQuote: "Every Service Bus queue SHALL have a dead-letter queue policy",
            lambda: "input1.text.ToLower().Contains(\"dead-letter\")") with
        {
            AppliesWhen = new Dictionary<string, IReadOnlyList<string>> { ["product"] = ["azure service bus"] },
        };
        var set = Set(rule) with
        {
            ScopeFacts = [new ScopeFact("product", ["azure"], ["deployed exclusively on azure"])],
        };
        var doc = Doc(("s-p", "The platform is deployed exclusively on Azure."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(set, doc);

        report.Verdicts.Should().NotContain(v => v.Outcome == VerdictOutcome.NotApplicable);
    }

    [Fact]
    public async Task Glued_modal_from_pdf_extraction_is_still_a_requirement()
    {
        var rule = MakeBroadRule(
            "EA-GLUED",
            evidenceQuote: "MUSTNOT derive the tenant from an unvalidated client-supplied value.",
            lambda: "input1.text.ToLower().Contains(\"zzz\")");
        var doc = Doc(("s-p", "Tenancy is described elsewhere."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().NotContain(v => v.Outcome == VerdictOutcome.Skipped);
    }

    [Fact]
    public async Task Rule_passing_in_many_sections_gets_one_primary_pass_with_supporting_evidence()
    {
        var rule = MakeBroadRule(
            "EA-DBAUTH",
            evidenceQuote: "Database authentication SHALL use managed identity",
            lambda: "input1.text.ToLower().Contains(\"managed identity\")");
        var doc = Doc(
            ("s-a", "Database access uses managed identity."),
            ("s-b", "Database clients authenticate with managed identity as well."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(Set(rule), doc);

        var pass = report.Verdicts.Should().ContainSingle(v => v.Outcome == VerdictOutcome.Pass).Subject;
        pass.SupportingEvidence.Should().ContainSingle();
    }

    [Fact]
    public async Task Rule_without_normative_word_is_skipped_not_passed()
    {
        var rule = MakeBroadRule(
            "EA-FACT",
            evidenceQuote: "The corporate-approved tools are Terraform and Bicep.",
            lambda: "input1.text.ToLower().Contains(\"terraform\")");
        var doc = Doc(("s-iac", "Infrastructure provisioning via IaC (Terraform)."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().ContainSingle().Which.Outcome.Should().Be(VerdictOutcome.Skipped);
    }

    [Fact]
    public async Task Optional_rule_with_no_relevant_section_is_Gap_not_NotApplicable()
    {
        var rule = MakeBroadRule(
            "EA-OPT",
            evidenceQuote: "Latency histograms SHOULD expose exemplars",
            lambda: "input1.text.ToLower().Contains(\"exemplar\")") with
        {
            Applicability = RuleApplicability.Optional,
        };
        var doc = Doc(("s-x", "Totally unrelated payroll content."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().ContainSingle().Which.Outcome.Should().Be(VerdictOutcome.Gap);
    }

    [Fact]
    public async Task Conditional_rule_with_unestablished_trigger_is_Gap_not_NotApplicable()
    {
        var rule = MakeBroadRule(
            "EA-COND",
            evidenceQuote: "Where batch jobs run, they SHALL emit exemplars",
            lambda: "input1.text.ToLower().Contains(\"exemplar\")") with
        {
            Applicability = RuleApplicability.Conditional,
        };
        var doc = Doc(("s-x", "Totally unrelated payroll content."));

        var report = await Build(applicabilityFloor: 0.2).EvaluateAsync(Set(rule), doc);

        report.Verdicts.Should().ContainSingle().Which.Outcome.Should().Be(VerdictOutcome.Gap);
    }

    // ── Applicability floor: keeps topic-matched sections ───────────────

    [Fact]
    public async Task Floor_on_topic_matched_section_still_evaluates_lambda()
    {
        // Rule about ADR storage. Two sections: one mentions "adr" and
        // the required "docs/adr/" path (should Pass), one is unrelated
        // (should be gated out by the floor).
        var rule = MakeBroadRule(
            "EA-ADR-001",
            evidenceQuote: "ADRs SHALL be stored in the workload source repository under docs/adr/",
            lambda: "input1.text.Contains(\"docs/adr/\")");
        var doc = Doc(
            ("s-ok", "The workload source repository stores ADRs under docs/adr/."),
            ("s-junk", "Retention policy for backups is thirty days on the primary cluster."));

        var report = await Build(applicabilityFloor: 0.2, emitRuleLevelStats: true)
            .EvaluateAsync(Set(rule), doc);

        // The topic-matched section produced a Pass; the junk section was
        // filtered by the floor and produced NO verdict — so we get one
        // verdict, not two, and the rule's aggregate outcome is Pass.
        report.Verdicts.Should().ContainSingle();
        report.Verdicts[0].Outcome.Should().Be(VerdictOutcome.Pass);
        report.RulesPassed.Should().Be(1);
        report.RulesFailed.Should().Be(0);
        report.RuleScore.Should().Be(1.0);
    }

    // ── Rule-level rollup arithmetic ────────────────────────────────────

    [Fact]
    public async Task RuleSummaries_aggregate_precedence_pass_wins_over_fail()
    {
        // One rule against three sections. Section 1 satisfies the lambda,
        // sections 2 and 3 do not → per-verdict pass rate is 1/3 (bad-
        // looking score), but the rule-level aggregate is Pass (the doc
        // *does* address the requirement, once).
        var rule = MakeBroadRule(
            "R-42",
            evidenceQuote: "docs/adr/",
            lambda: "input1.text.Contains(\"docs/adr/\")");
        var doc = Doc(
            ("s1", "ADRs live under docs/adr/ per policy."),
            ("s2", "ADR templates for docs live in the wiki, not the repo."),
            ("s3", "See the ADR retention section for archival."));

        var report = await Build(applicabilityFloor: 0.0, emitRuleLevelStats: true)
            .EvaluateAsync(Set(rule), doc);

        // s2 is in scope but omits the path; s1 satisfies it, so the rolled-up
        // rule has no gap. s3 is unrelated (no verdict).
        report.Passed.Should().Be(1);
        report.Failed.Should().Be(0);
        report.Gaps.Should().Be(0);
        report.TotalUniqueRules.Should().Be(1);
        report.RulesPassed.Should().Be(1);
        report.RulesFailed.Should().Be(0);
        report.RuleScore.Should().Be(1.0);
        report.Score.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public async Task RuleSummaries_has_one_entry_per_distinct_rule()
    {
        var r1 = MakeBroadRule("A-01", "alpha", "input1.text.Contains(\"alpha\")");
        var r2 = MakeBroadRule("B-02", "beta", "input1.text.Contains(\"beta\")");
        var doc = Doc(("s1", "alpha only"), ("s2", "beta only"), ("s3", "neither"));

        var report = await Build(emitRuleLevelStats: true).EvaluateAsync(Set(r1, r2), doc);

        report.RuleSummaries.Should().NotBeNull();
        report.RuleSummaries!.Should().HaveCount(2);
        report.RuleSummaries!.Select(s => s.RuleId).Should().BeEquivalentTo(new[] { "A-01", "B-02" });
        var a = report.RuleSummaries!.Single(s => s.RuleId == "A-01");
        a.AggregateOutcome.Should().Be(VerdictOutcome.Pass);
        a.PassCount.Should().Be(1);
        a.FailCount.Should().Be(0);
        a.SectionsEvaluated.Should().Be(1);
    }
}
