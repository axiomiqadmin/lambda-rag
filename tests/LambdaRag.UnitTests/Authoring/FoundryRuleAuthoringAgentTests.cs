using System.Runtime.CompilerServices;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Core.Domain;
using Microsoft.Extensions.AI;
using Xunit;

namespace LambdaRag.UnitTests.Authoring;

public class FoundryRuleAuthoringAgentTests
{
    private static SourceSpan AnySpan(string doc = "chunk.txt", string? heading = "Identity and Access Management")
        => new(doc, 0, 500, 1, heading);

    private static RuleAuthoringRequest RequestWith(string content, string prefix = "EA-")
        => new(content, "architecture", prefix, AnySpan());

    // ============ predicate DSL validator ============

    [Theory]
    [InlineData("input1.text.Contains(\"WAF\")", true)]
    [InlineData("input1.text.ToLower().Contains(\"waf\")", true)]
    [InlineData("input1.text.Contains(\"a\") || input1.text.Contains(\"b\")", true)]
    [InlineData("(input1.text.ToLower().Contains(\"tls\") && (input1.text.Contains(\"1.2\") || input1.text.Contains(\"1.3\")))", true)]
    // Rejects: unknown method
    [InlineData("input1.text.StartsWith(\"WAF\")", false)]
    // Rejects: property access
    [InlineData("input1.text.Length > 0", false)]
    // Rejects: other identifiers
    [InlineData("input2.text.Contains(\"x\")", false)]
    // Rejects: category equality (the old mock's style)
    [InlineData("input1.category == \"iam\"", false)]
    // Rejects: empty
    [InlineData("", false)]
    // Rejects: naked literal
    [InlineData("true", false)]
    public void IsValidPredicate_matches_DSL_grammar(string predicate, bool expected)
    {
        FoundryRuleAuthoringAgent.IsValidPredicate(predicate).Should().Be(expected);
    }

    // ============ happy path — 3 SHALL clauses -> 3 rules ============

    [Fact]
    public async Task HappyPath_emits_one_rule_per_normative_clause()
    {
        // Input contains all 3 quotes verbatim so the defensibility validator passes.
        var input = "Privileged access SHALL require MFA. All public ingress SHALL traverse a WAF in prevention mode. Audit logs SHALL be forwarded to a tamper-resistant SIEM.";

        var json = """
        {
          "rules": [
            {
              "topicSlug": "IAM",
              "naturalLanguage": "Privileged access SHALL require MFA.",
              "predicate": "input1.text.ToLower().Contains(\"mfa\")",
              "remediation": "Add a clause requiring MFA for privileged access.",
              "sourceQuote": "Privileged access SHALL require MFA.",
              "obligation": "must", "scopeTerms": ["scopeword"], "relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence": ["evidence"], "severity": "Deviation", "severityBasis": ""
            },
            {
              "topicSlug": "NET",
              "naturalLanguage": "Public ingress SHALL traverse a WAF in prevention mode.",
              "predicate": "input1.text.ToLower().Contains(\"waf\") && (input1.text.ToLower().Contains(\"prevention\") || input1.text.ToLower().Contains(\"blocking\"))",
              "remediation": "State that ingress traverses a WAF configured in prevention (blocking) mode.",
              "sourceQuote": "All public ingress SHALL traverse a WAF in prevention mode.",
              "obligation": "must", "scopeTerms": ["scopeword"], "relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence": ["evidence"], "severity": "Deviation", "severityBasis": ""
            },
            {
              "topicSlug": "LOG",
              "naturalLanguage": "Audit logs SHALL be forwarded to a tamper-resistant SIEM.",
              "predicate": "input1.text.ToLower().Contains(\"siem\") || input1.text.ToLower().Contains(\"sentinel\")",
              "remediation": "Forward control-plane and data-plane audit logs to a SIEM (e.g. Sentinel).",
              "sourceQuote": "Audit logs SHALL be forwarded to a tamper-resistant SIEM.",
              "obligation": "must", "scopeTerms": ["scopeword"], "relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence": ["evidence"], "severity": "Deviation", "severityBasis": ""
            }
          ]
        }
        """;

        var agent = CreateAgent(new FakeChatClient(json));
        var result = await agent.AuthorAsync(RequestWith(input));

        result.Should().HaveCount(3);
        result.Select(r => r.Rule.Metadata["topicSlug"]).Should().Equal("IAM", "LOG", "NET"); // ordered by topic slug
        result.All(r => r.Rule.Lambda.StartsWith("input1.text")).Should().BeTrue();
        result.All(r => r.Rule.Metadata["authoringAgent"] == "FoundryRuleAuthoringAgent").Should().BeTrue();
        result.All(r => !string.IsNullOrEmpty(r.Rule.EvidenceQuote)).Should().BeTrue();
    }

    // ============ predicate-shape rejection ============

    [Fact]
    public async Task BadPredicate_is_dropped_but_valid_siblings_survive()
    {
        // Only the "Good." quote is verbatim in the input — the survivor must
        // clear BOTH the DSL check (its predicate is legal) AND the
        // defensibility check (its quote appears in the input).
        var input = "Something Good. Something Bad. Also bad.";

        var json = """
        {
          "rules": [
            {
              "topicSlug": "IAM",
              "naturalLanguage": "Good rule.",
              "predicate": "input1.text.Contains(\"mfa\")",
              "remediation": "keep",
              "sourceQuote": "Good.",
              "obligation": "must", "scopeTerms": ["scopeword"], "relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence": ["evidence"], "severity": "Deviation", "severityBasis": ""
            },
            {
              "topicSlug": "IAM",
              "naturalLanguage": "Bad rule with LINQ.",
              "predicate": "input1.text.Split(' ').Any(w => w == \"mfa\")",
              "remediation": "drop",
              "sourceQuote": "Bad.",
              "obligation": "must", "scopeTerms": ["scopeword"], "relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence": ["evidence"], "severity": "Deviation", "severityBasis": ""
            },
            {
              "topicSlug": "iam",
              "naturalLanguage": "Bad slug (lowercase).",
              "predicate": "input1.text.Contains(\"x\")",
              "remediation": "drop",
              "sourceQuote": "Also bad.",
              "obligation": "must", "scopeTerms": ["scopeword"], "relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence": ["evidence"], "severity": "Deviation", "severityBasis": ""
            }
          ]
        }
        """;

        var agent = CreateAgent(new FakeChatClient(json));
        var result = await agent.AuthorAsync(RequestWith(input));

        result.Should().HaveCount(1);
        result[0].Rule.NaturalLanguage.Should().Be("Good rule.");
    }

    // ============ defensibility — non-verbatim quotes are dropped ============

    [Fact]
    public async Task NonVerbatimSourceQuote_isDropped_evenIfEverythingElseIsValid()
    {
        // The LLM paraphrased the quote instead of copying verbatim. This
        // breaks regulator-replayable evidence, so the rule MUST be dropped.
        var input = "Privileged access SHALL require phishing-resistant MFA.";

        var json = """
        {
          "rules": [
            {
              "topicSlug": "IAM",
              "naturalLanguage": "MFA required.",
              "predicate": "input1.text.ToLower().Contains(\"mfa\")",
              "remediation": "Add MFA.",
              "sourceQuote": "All privileged users need MFA.",
              "obligation": "must", "scopeTerms": ["scopeword"], "relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence": ["evidence"], "severity": "Deviation", "severityBasis": ""
            }
          ]
        }
        """;

        var agent = CreateAgent(new FakeChatClient(json));
        var result = await agent.AuthorAsync(RequestWith(input));

        result.Should().BeEmpty();
    }

    // ============ empty content short-circuit ============

    [Fact]
    public async Task EmptyContent_returns_empty_without_calling_LLM()
    {
        var chat = new FakeChatClient("SHOULD NOT BE READ");
        var agent = CreateAgent(chat);
        var result = await agent.AuthorAsync(RequestWith(""));

        result.Should().BeEmpty();
        chat.Calls.Should().Be(0);
    }

    // ============ malformed JSON handling ============

    [Fact]
    public async Task MalformedJson_returns_empty_and_does_not_throw()
    {
        var agent = CreateAgent(new FakeChatClient("not json at all {"));
        var result = await agent.AuthorAsync(RequestWith("Something SHALL happen."));
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task EmptyRulesArray_returns_empty()
    {
        var agent = CreateAgent(new FakeChatClient("""{"rules": []}"""));
        var result = await agent.AuthorAsync(RequestWith("Prose with no normative content."));
        result.Should().BeEmpty();
    }

    // ============ retry on transient 429 ============

    [Fact]
    public async Task Transient429_is_retried_and_eventually_succeeds()
    {
        // Quote "q" must appear verbatim in the input for the defensibility
        // validator to admit it.
        var good = """
        {"rules":[{"topicSlug":"IAM","naturalLanguage":"n","predicate":"input1.text.Contains(\"x\")","remediation":"r","sourceQuote":"q","obligation":"must","scopeTerms":["scopeword"],"relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence":["e"],"severity":"Deviation","severityBasis":""}]}
        """;
        var chat = new FakeChatClient(new Queue<Func<string>>(new Func<string>[]
        {
          () => throw new FakeThrottleException(429, "Too Many Requests", retryAfterMs: "1"),
            () => throw new FakeThrottleException(503, "Service Unavailable"),
            () => good,
        }));
        var agent = new FoundryRuleAuthoringAgent(chat, new DeterministicHashEmbedder(), log: null,
          maxRetries: 3, requestsPerMinute: 60_000);
        var result = await agent.AuthorAsync(RequestWith("Something SHALL be q done."));

        result.Should().HaveCount(1);
        chat.Calls.Should().Be(3);
    }

    [Fact]
    public void ValidateConcepts_requires_each_group_to_be_proven_by_the_quote()
    {
        var quote = "Breaking changes SHALL increment the major version.";
        FoundryRuleAuthoringAgent.ValidateConcepts(
            [["breaking change"], ["major version", "next major"]], quote).Should().BeNull();
        FoundryRuleAuthoringAgent.ValidateConcepts(
            [["breaking change"], ["semver bump"]], quote).Should().NotBeNull();
    }

    [Fact]
    public void ParsedAppliesWhen_keeps_only_scope_fact_names()
    {
        var json = """
        {"rules":[{"topicSlug":"IAM","naturalLanguage":"n","predicate":"input1.text.Contains(\"x\")","remediation":"r","sourceQuote":"q","obligation":"must","scopeTerms":["s"],"relevantExamples":["s a"],"irrelevantExamples":["b"],"requiredEvidence":["e"],"severity":"Deviation","severityBasis":"",
          "appliesWhen":[{"fact":"Platform","values":["Cluster"]},{"fact":"environment","values":["production"]}]}]}
        """;
        var parsed = FoundryRuleAuthoringAgent.TryParseRulesEnvelope(json);

        parsed.Should().NotBeNull();
        parsed![0].AppliesWhen.Keys.Should().Equal("platform");
    }

    [Fact]
    public async Task Invalid_envelope_on_large_chunk_is_retried_on_two_halves()
    {
        var good = """
        {"rules":[{"topicSlug":"IAM","naturalLanguage":"n","predicate":"input1.text.Contains(\"x\")","remediation":"r","sourceQuote":"alpha clause","obligation":"must","scopeTerms":["scopeword"],"relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence":["e"],"severity":"Deviation","severityBasis":""}]}
        """;
        var chat = new FakeChatClient(new Queue<Func<string>>(new Func<string>[]
        {
            () => "{ truncated",
            () => good,
            () => "{\"rules\":[]}",
        }));
        var agent = CreateAgent(chat);
        var content = "alpha clause SHALL hold.\n" + new string('x', 900) + "\nbeta clause SHALL hold.";

        var result = await agent.AuthorAsync(RequestWith(content));

        chat.Calls.Should().Be(3);
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task PermanentFailure_swallowed_returns_empty()
    {
        var chat = new FakeChatClient(new Queue<Func<string>>(new Func<string>[]
        {
            () => throw new InvalidOperationException("permanent 400 bad request"),
        }));
        var agent = CreateAgent(chat);
        var result = await agent.AuthorAsync(RequestWith("Something SHALL be done."));

        result.Should().BeEmpty();
        chat.Calls.Should().Be(1); // non-transient -> no retry
    }

      [Fact]
      public async Task ExhaustedTransientFailure_propagates_to_caller()
      {
        var chat = new FakeChatClient(new Queue<Func<string>>(new Func<string>[]
        {
          () => throw new FakeThrottleException(429, "Too Many Requests"),
        }));
        var agent = CreateAgent(chat, maxRetries: 0);

        var act = () => agent.AuthorAsync(RequestWith("Something SHALL be done."));
        await act.Should().ThrowAsync<FakeThrottleException>();
        chat.Calls.Should().Be(1);
      }

    // ============ meta-governance skipping is prompt-driven ============
    // The agent trusts the model to skip. We verify the prompt contains the
    // right instruction so a regression would be visible in code review.

    [Fact]
    public void SystemPrompt_instructs_model_to_skip_meta_governance()
    {
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("SKIP meta-governance");
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("reviewed annually");
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("time-boxed");
    }

    [Fact]
    public void SystemPrompt_states_constrained_DSL()
    {
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("CONSTRAINED PREDICATE DSL");
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("input1.text.Contains");
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("input1.text.ToLower().Contains");
    }

    [Fact]
    public void SystemPrompt_requires_verbatim_sourceQuote()
    {
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("byte-for-byte verbatim");
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("SKIP the rule");
    }

    // ============ scope / obligation / severity (review-annotation-quality) ============

    private const string ScopedInput = "Public ingress SHALL traverse a WAF.";

    private static string RuleJson(string extra) => $$"""
        {"rules":[{"topicSlug":"NET","naturalLanguage":"Public ingress SHALL traverse a WAF.",
        "predicate":"input1.text.ToLower().Contains(\"waf\")","remediation":"r",
        "sourceQuote":"Public ingress SHALL traverse a WAF.",{{extra}}}]}
        """;

    [Fact]
    public async Task Scope_compiles_into_predicate_and_rule_is_not_blanket_true()
    {
        var json = RuleJson("""
            "obligation":"must","scopeTerms":["Ingress","public endpoint"],"scopeDescription":"Edge traffic",
            "relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence":["WAF named"],"severity":"Violation",
            "severityBasis":"Unfiltered ingress exposes internet-facing services to attack"
            """);
        var result = await CreateAgent(new FakeChatClient(json)).AuthorAsync(RequestWith(ScopedInput));

        var rule = result.Single().Rule;
        rule.Predicate.Should().NotBe("true");
        FoundryRuleAuthoringAgent.IsValidPredicate(rule.Predicate).Should().BeTrue();
        rule.Predicate.Should().Contain("\"ingress\"").And.Contain("\"public endpoint\"");
        rule.Scope!.Terms.Should().Equal("Ingress", "public endpoint");
        rule.Obligation.Should().Be(RuleObligation.Must);
        rule.RequiredEvidence.Should().Equal("WAF named");
        rule.Severity.Should().Be(RuleSeverity.Violation);
        rule.SeverityBasis.Should().Contain("internet-facing");
        rule.Metadata["severitySource"].Should().Be("authoring-validated");
        rule.Applicability.Should().Be(RuleApplicability.Conditional);
        FoundryRuleAuthoringAgent.FindScopeDefect(rule).Should().BeNull();
    }

    [Fact]
    public async Task MustWithoutImpactBasis_is_not_major_and_records_review_needed()
    {
        var json = RuleJson("""
            "obligation":"must","scopeTerms":["ingress"],"relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence":["WAF named"],
            "severity":"Critical","severityBasis":""
            """);
        var rule = (await CreateAgent(new FakeChatClient(json)).AuthorAsync(RequestWith(ScopedInput))).Single().Rule;

        rule.Severity.Should().Be(FoundryRuleAuthoringAgent.ReviewNeededDefault).And.NotBe(RuleSeverity.Violation);
        rule.Metadata["severitySource"].Should().Be("review-needed-default");
        rule.SeverityBasis.Should().StartWith("review-needed");
    }

    [Theory]
    [InlineData("Unsupported")]
    [InlineData("")]
    [InlineData("Bogus")]
    public async Task UnsupportedSeverity_uses_non_major_default(string severity)
    {
        var json = RuleJson($$"""
            "obligation":"must","scopeTerms":["ingress"],"relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence":["x"],"severity":"{{severity}}","severityBasis":"n/a"
            """);
        var rule = (await CreateAgent(new FakeChatClient(json)).AuthorAsync(RequestWith(ScopedInput))).Single().Rule;
        rule.Severity.Should().Be(RuleSeverity.Deviation);
        rule.SeverityBasis.Should().Contain("no supported severity");
    }

    [Fact]
    public async Task ShouldObligation_cannot_be_major_and_is_optional()
    {
        var json = RuleJson("""
            "obligation":"should","scopeTerms":["ingress"],"relevantExamples":["scopeword ingress here"],"irrelevantExamples":["unrelated billing text"],"requiredEvidence":["x"],
            "severity":"Violation","severityBasis":"Substantial impact on exposed customer-facing systems"
            """);
        var rule = (await CreateAgent(new FakeChatClient(json)).AuthorAsync(RequestWith(ScopedInput))).Single().Rule;
        rule.Severity.Should().Be(RuleSeverity.Deviation);
        rule.Applicability.Should().Be(RuleApplicability.Optional);
    }

    [Theory]
    [InlineData("\"obligation\":\"must\",\"relevantExamples\":[\"scopeword ingress here\"],\"irrelevantExamples\":[\"unrelated billing text\"],\"requiredEvidence\":[\"x\"],\"severity\":\"Deviation\",\"severityBasis\":\"\"")] // no scope
    [InlineData("\"obligation\":\"must\",\"scopeTerms\":[],\"relevantExamples\":[\"scopeword ingress here\"],\"irrelevantExamples\":[\"unrelated billing text\"],\"requiredEvidence\":[\"x\"],\"severity\":\"Deviation\",\"severityBasis\":\"\"")]
    [InlineData("\"obligation\":\"must\",\"scopeTerms\":[\"system\"],\"relevantExamples\":[\"scopeword ingress here\"],\"irrelevantExamples\":[\"unrelated billing text\"],\"requiredEvidence\":[\"x\"],\"severity\":\"Deviation\",\"severityBasis\":\"\"")] // generic
    [InlineData("\"obligation\":\"must\",\"scopeTerms\":[\"ab\"],\"relevantExamples\":[\"scopeword ingress here\"],\"irrelevantExamples\":[\"unrelated billing text\"],\"requiredEvidence\":[\"x\"],\"severity\":\"Deviation\",\"severityBasis\":\"\"")] // too short
    [InlineData("\"scopeTerms\":[\"ingress\"],\"relevantExamples\":[\"scopeword ingress here\"],\"irrelevantExamples\":[\"unrelated billing text\"],\"requiredEvidence\":[\"x\"],\"severity\":\"Deviation\",\"severityBasis\":\"\"")] // no obligation
    [InlineData("\"obligation\":\"must\",\"scopeTerms\":[\"ingress\"],\"severity\":\"Deviation\",\"severityBasis\":\"\"")] // no evidence
    public async Task RulesWithoutDefensibleScope_areQuarantined(string extra)
    {
        var agent = CreateAgent(new FakeChatClient(RuleJson(extra)));
        var result = await agent.AuthorAsync(RequestWith(ScopedInput));

        result.Should().BeEmpty();
        agent.Quarantined.Should().HaveCount(1);
    }

    [Fact]
    public void FindScopeDefect_flags_legacy_blanket_rule()
    {
        var legacy = new Rule("R-1", "1.0.0", "nl", "input1.text.Contains(\"x\")", new System.Text.Json.Nodes.JsonObject(),
            new LambdaRag.Core.Selectors.PathSelector("$.sections[*]"), RuleSeverity.Violation, AnySpan(), "q",
            new Dictionary<string, string>());

        FoundryRuleAuthoringAgent.FindScopeDefect(legacy).Should().NotBeNull();
        FoundryRuleAuthoringAgent.FindScopeDefect(legacy with { Predicate = "input1.text.Contains(\"ingress\")" }).Should().BeNull();
    }

    [Fact]
    public async Task IrrelevantExampleMatchingScope_isQuarantined()
    {
        var json = RuleJson("""
            "obligation":"must","scopeTerms":["ingress"],"requiredEvidence":["x"],
            "relevantExamples":["Public ingress is terminated at the edge."],
            "irrelevantExamples":["Invoices are paid net 30.","Ingress of new staff is covered in HR."],
            "severity":"Deviation","severityBasis":""
            """);
        var agent = CreateAgent(new FakeChatClient(json));

        (await agent.AuthorAsync(RequestWith(ScopedInput))).Should().BeEmpty();
        agent.Quarantined.Single().Reason.Should().Contain("irrelevant example is matched");
    }

    [Fact]
    public async Task RelevantExampleNotMatchingScope_isQuarantined()
    {
        var json = RuleJson("""
            "obligation":"must","scopeTerms":["ingress"],"requiredEvidence":["x"],
            "relevantExamples":["Backups run nightly."],"irrelevantExamples":["Invoices are paid net 30."],
            "severity":"Deviation","severityBasis":""
            """);
        var agent = CreateAgent(new FakeChatClient(json));

        (await agent.AuthorAsync(RequestWith(ScopedInput))).Should().BeEmpty();
        agent.Quarantined.Single().Reason.Should().Contain("relevant example is not matched");
    }

    [Theory]
    [InlineData("\"relevantExamples\":[],\"irrelevantExamples\":[\"b\"]")]
    [InlineData("\"relevantExamples\":[\"ingress\"]")]
    [InlineData("\"relevantExamples\":[\"ingress\",\"ingress\",\"ingress\",\"ingress\"],\"irrelevantExamples\":[\"b\"]")]
    public async Task MissingOrOversizedExamples_areQuarantined(string examples)
    {
        var json = RuleJson($$"""
            "obligation":"must","scopeTerms":["ingress"],"requiredEvidence":["x"],{{examples}},
            "severity":"Deviation","severityBasis":""
            """);
        var agent = CreateAgent(new FakeChatClient(json));

        (await agent.AuthorAsync(RequestWith(ScopedInput))).Should().BeEmpty();
        agent.Quarantined.Should().HaveCount(1);
    }

    [Fact]
    public async Task ValidExamples_are_recorded_on_rule_and_fingerprinted()
    {
        var json = RuleJson("""
            "obligation":"must","scopeTerms":["ingress"],"requiredEvidence":["x"],
            "relevantExamples":["Public ingress is terminated at the edge."],
            "irrelevantExamples":["Invoices are paid net 30."],
            "severity":"Deviation","severityBasis":""
            """);
        var rule = (await CreateAgent(new FakeChatClient(json)).AuthorAsync(RequestWith(ScopedInput))).Single().Rule;

        rule.Examples!.Positive.Should().Equal("Public ingress is terminated at the edge.");
        rule.Examples.Negative.Should().Equal("Invoices are paid net 30.");
        rule.Fingerprint().Should().NotBe((rule with { Examples = null }).Fingerprint());
    }

    [Fact]
    public void SystemPrompt_requires_scope_examples()
    {
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("relevantExamples").And.Contain("irrelevantExamples");
    }

    [Fact]
    public void SystemPrompt_and_schema_describe_scope_and_severity_contract()
    {
        FoundryRuleAuthoringAgent.SystemPrompt.Should().Contain("scopeTerms")
            .And.Contain("severityBasis").And.Contain("NOT the word");
    }

    // ============ compatibility / fingerprint ============

    private static Rule LegacyRule() => new("R-1", "1.0.0", "nl", "input1.text.Contains(\"x\")",
        new System.Text.Json.Nodes.JsonObject(), new LambdaRag.Core.Selectors.PathSelector("$.sections[*]"),
        RuleSeverity.Violation, AnySpan(), "q", new Dictionary<string, string>());

    [Fact]
    public void LegacyRule_fingerprint_is_unchanged_by_new_optional_fields()
    {
        var baseline = LegacyRule();
        (baseline with { RequiredEvidence = Array.Empty<string>(), SeverityBasis = "  " }).Fingerprint()
            .Should().Be(baseline.Fingerprint());
    }

    [Fact]
    public void NewFields_participate_in_fingerprint()
    {
        var baseline = LegacyRule();
        var fp = baseline.Fingerprint();
        (baseline with { Obligation = RuleObligation.Must }).Fingerprint().Should().NotBe(fp);
        (baseline with { Scope = new RuleScope(new[] { "ingress" }) }).Fingerprint().Should().NotBe(fp);
        (baseline with { Scope = new RuleScope(new[] { "egress" }) }).Fingerprint()
            .Should().NotBe((baseline with { Scope = new RuleScope(new[] { "ingress" }) }).Fingerprint());
        (baseline with { RequiredEvidence = new[] { "e" } }).Fingerprint().Should().NotBe(fp);
        (baseline with { SeverityBasis = "impact" }).Fingerprint().Should().NotBe(fp);
    }

    [Fact]
    public void LegacyRuleJson_without_new_fields_loads_and_roundtrips_without_emitting_them()
    {
        var json = LambdaRag.Core.CanonicalJson.Serialize(LegacyRule());
        json.Should().NotContain("obligation").And.NotContain("scope").And.NotContain("severityBasis");

        var back = LambdaRag.Core.CanonicalJson.Deserialize<Rule>(json)!;
        back.Obligation.Should().BeNull();
        back.Scope.Should().BeNull();
        back.Fingerprint().Should().Be(LegacyRule().Fingerprint());

        var full = LegacyRule() with
        {
            Obligation = RuleObligation.Should,
            Scope = new RuleScope(new[] { "ingress" }, "edge"),
            RequiredEvidence = new[] { "waf" },
            SeverityBasis = "impact",
        };
        var rt = LambdaRag.Core.CanonicalJson.Deserialize<Rule>(LambdaRag.Core.CanonicalJson.Serialize(full))!;
        rt.Fingerprint().Should().Be(full.Fingerprint());
    }

    // ============ helpers ============

    private static FoundryRuleAuthoringAgent CreateAgent(IChatClient chat, int maxRetries = 3)
      => new(chat, new DeterministicHashEmbedder(), maxRetries: maxRetries, requestsPerMinute: 60_000);

    /// <summary>
    /// Minimal IChatClient stub. Either returns a fixed response every time,
    /// or dequeues a scripted response (which may throw) per call.
    /// </summary>
    private sealed class FakeChatClient : IChatClient
    {
        private readonly string? _fixed;
        private readonly Queue<Func<string>>? _script;

        public int Calls { get; private set; }

        public FakeChatClient(string @fixed) { _fixed = @fixed; }
        public FakeChatClient(Queue<Func<string>> script) { _script = script; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            var text = _fixed ?? _script!.Dequeue().Invoke();
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, text));
            return Task.FromResult(response);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => EmptyStream();

        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream(
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>Fake Azure-style transient exception with a Status property.</summary>
    private sealed class FakeThrottleException : Exception
    {
        public int Status { get; }
      private readonly string? _retryAfterMs;

      public FakeThrottleException(int status, string message, string? retryAfterMs = null) : base(message)
      {
        Status = status;
        _retryAfterMs = retryAfterMs;
      }

      public FakeResponse GetRawResponse()
      {
        var headers = new Dictionary<string, string>();
        if (_retryAfterMs is not null)
          headers["Retry-After-ms"] = _retryAfterMs;
        return new FakeResponse(headers);
      }
    }

    private sealed record FakeResponse(Dictionary<string, string> Headers);
}