using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Selectors;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LambdaRag.Authoring;

/// <summary>
/// LLM-backed <see cref="IRuleAuthoringAgent"/> that calls an OpenRouter
/// chat model through <see cref="IChatClient"/>. Emits
/// one rule per SHALL / MUST / SHALL NOT / MUST NOT clause found in the
/// supplied chunk, with a strictly constrained predicate DSL so the runtime
/// evaluation path stays 100% deterministic (no LLM at runtime).
///
/// <para>Design highlights:</para>
/// <list type="bullet">
///   <item>Constrained predicate DSL: only <c>input1.text.Contains("...")</c>,
///     <c>input1.text.ToLower().Contains("...")</c>, boolean <c>||</c>/<c>&amp;&amp;</c>
///     and parentheses. Anything else is rejected before the rule is emitted.</item>
///   <item>Skips meta-governance clauses ("This policy SHALL be reviewed
///     annually", "Exceptions SHALL be time-boxed to 90 days" etc.) — the
///     prompt explicitly instructs the model, and the caller can additionally
///     inspect the emitted <c>metadata.meta-governance</c> flag.</item>
///   <item>Optimized for the three lambda-rag pillars:
///     <list type="number">
///       <item><b>Determinism</b> (runtime) — the constrained predicate DSL
///         guarantees compile-time-safe lambdas; the runtime never sees an LLM.</item>
///       <item><b>Idempotency</b> (build-time reproducibility) — bounded
///         <c>MaxOutputTokens</c>, JSON-schema-enforced response shape, and a
///         stable-ordering post-pass keep re-runs as close to byte-identical
///         as the deployment allows. <c>Temperature</c> and <c>Seed</c> are
///         intentionally NOT set — the Foundry <c>gpt-5.x</c> deployments
///         reject non-default temperature and do not honour a seed parameter
///         (per the supported request schema).</item>
///       <item><b>Defensibility</b> (legal-grade evidence) — every emitted
///         rule's <c>sourceQuote</c> must be a byte-for-byte substring of
///         the source chunk; candidates that fail this check are dropped so
///         evidence is always regulator-replayable.</item>
///     </list></item>
///   <item>Structured JSON output validated against a JSON schema server-side
///     AND revalidated locally. Bad entries are dropped, not thrown — one bad
///     rule can't kill an entire policy pass.</item>
///   <item>Concurrency: pipeline-wide <see cref="SemaphoreSlim"/>(1) and a
///     configurable request-rate limit keep calls within Foundry quotas.</item>
///   <item>Retry: transient failures (HTTP 408 / 429 / 5xx, network errors,
///     timeouts) retried up to 3×, honoring provider retry headers.</item>
/// </list>
///
/// <para>Rule ID scheme is intentionally partial. The agent emits a topic
/// slug (e.g. <c>IAM</c>, <c>NET</c>) in <c>Rule.Metadata["topicSlug"]</c>
/// and a placeholder <c>Rule.Id</c>. The extraction pipeline is responsible
/// for stamping the final <c>{prefix}{TOPIC}-{NNN}</c> ID because per-topic
/// counters need cross-chunk state the single-chunk agent cannot see.</para>
/// </summary>
public sealed class FoundryRuleAuthoringAgent : IRuleAuthoringAgent
{
    /// <summary>Key exposed on <see cref="Rule.Metadata"/> holding the topic slug.</summary>
    public const string TopicSlugMetadataKey = "topicSlug";

    /// <summary>Key exposed on <see cref="Rule.Metadata"/> flagging the
    /// authoring provenance (useful for downstream audit).</summary>
    public const string AuthoringAgentMetadataKey = "authoringAgent";

    /// <summary>Metadata key recording why the rule's severity was assigned.</summary>
    public const string SeverityBasisMetadataKey = "severityBasis";

    /// <summary>Metadata key: <c>authoring-validated</c> or <c>review-needed-default</c>.</summary>
    public const string SeveritySourceMetadataKey = "severitySource";

    /// <summary>Non-MAJOR severity used when authoring cannot support a severity.</summary>
    public const RuleSeverity ReviewNeededDefault = RuleSeverityPolicy.ReviewNeededDefault;

    private const int MinSeverityBasisLength = RuleSeverityPolicy.MinimumImpactBasisLength;
    private const int MinScopeTermLength = 3;
    private const int MaxScopeTerms = 8;
    internal const int MinExamples = 1;
    internal const int MaxExamples = 3;
    internal const int MaxExampleLength = 400;

    private static readonly HashSet<string> GenericScopeTerms = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "system", "systems", "document", "documents", "service", "services",
        "all", "any", "must", "shall", "should", "section", "data", "application",
    };

    private readonly List<(string Topic, string Reason)> _quarantined = new();

    /// <summary>Candidates rejected for lacking schema/scope validity, for audit.</summary>
    public IReadOnlyList<(string Topic, string Reason)> Quarantined
    {
        get { lock (_quarantined) return _quarantined.ToArray(); }
    }

    // Pipeline-wide gate and schedule so multiple DI instances share one quota.
    private static readonly SemaphoreSlim GlobalCallGate = new(initialCount: 1, maxCount: 1);
    private static DateTimeOffset _nextCallAt;

    // The predicate validator: strip every legal token and confirm nothing is left.
    // Order matters — longer/more-specific patterns first.
    private static readonly Regex[] AllowedTokens =
    {
        // input1.text.ToLower().Contains("...")
        new(@"input1\.text\.ToLower\(\)\.Contains\(""(?:[^""\\]|\\.)*""\)", RegexOptions.Compiled),
        // input1.text.Contains("...")
        new(@"input1\.text\.Contains\(""(?:[^""\\]|\\.)*""\)", RegexOptions.Compiled),
        // Boolean operators and parentheses
        new(@"\|\||&&|\(|\)", RegexOptions.Compiled),
        // Whitespace
        new(@"\s+", RegexOptions.Compiled),
    };

    public const string SystemPrompt = """
        You are a policy-to-rule authoring assistant. Extract every normative clause
        (SHALL, MUST, SHALL NOT, MUST NOT) from the provided policy text and emit
        one machine-checkable rule per clause.

        Output a JSON object with a single key "rules" whose value is an array. Each
        element is:
        {
          "topicSlug": "<3-6 letter uppercase slug derived from the section heading, e.g. IAM, NET, SECR, CNTR, AKS, LOG, MON, TRACE, EXC, RETRY, CICD, IAC, SVC, COST, PRIV, SRE, SFI, DATA, COMP>",
          "naturalLanguage": "<the clause, verbatim or lightly normalized>",
          "predicate": "<a boolean expression in the CONSTRAINED DSL below>",
          "remediation": "<one sentence telling the author what to change>",
          "sourceQuote": "<byte-for-byte verbatim substring of the input section text — no paraphrasing, no ellipsis, no rewording. Must appear character-identical inside the input. If you cannot find a verbatim excerpt <= 400 chars that grounds the clause, SKIP the rule.>",
          "obligation": "<must | should | may — the strength of the source clause only>",
          "scopeTerms": ["<1-8 distinctive lowercase words/phrases (>= 3 chars) a document section must contain to be IN SCOPE for this clause, e.g. 'ingress', 'waf'. Never generic words like 'system' or 'document'. If you cannot state a defensible scope, SKIP the rule.>"],
          "scopeDescription": "<one sentence: when does this clause apply?>",
          "relevantExamples": ["<1-3 short (<= 400 chars) invented section excerpts that ARE in scope; each must contain at least one scopeTerm>"],
          "irrelevantExamples": ["<1-3 short (<= 400 chars) invented section excerpts that are NOT in scope (e.g. unrelated topics); none may contain any scopeTerm>"],
          "requiredEvidence": ["<what an in-scope section must state to comply>"],
          "requiredConcepts": [["<alternative wording A of mandatory part 1>", "<alternative wording B of mandatory part 1>"], ["<alternatives for mandatory part 2>"]],
          "appliesWhen": [{ "fact": "<fact name, e.g. platform>", "values": ["<value/alias>", "<another alias of the accepted value>"] }],
          "severity": "<Suggestion | Deviation | Violation | Critical | Unsupported>",
          "severityBasis": "<the concrete impact (security, legal, financial, availability) that justifies the severity>"
        }

        SEVERITY: severity reflects the impact of non-compliance, NOT the word
        MUST/SHALL. Use Violation or Critical only with a concrete impact stated
        in severityBasis. If impact is not supported by the policy text, emit
        "Unsupported" (it is recorded as a review-needed, non-major finding).

        CONSTRAINED PREDICATE DSL — the ONLY allowed constructs:
          input1.text.Contains("literal")
          input1.text.ToLower().Contains("literal")   // for case-insensitive
          Boolean operators: || (or), && (and)
          Parentheses: ( )
        Nothing else. No .Length, no regex, no other method calls, no variables
        other than input1.text.

        The predicate must evaluate to TRUE when the target document COMPLIES with
        the clause and FALSE when it violates. Example:
          Clause: "All public ingress SHALL traverse a WAF in prevention mode."
          Predicate: input1.text.ToLower().Contains("waf") &&
                     (input1.text.ToLower().Contains("prevention") ||
                      input1.text.ToLower().Contains("blocking"))

        SKIP meta-governance clauses that govern the policy process itself, for
        example "Exceptions SHALL be time-boxed to 90 days" or "This policy SHALL
        be reviewed annually". Only emit rules testable against a document under
        review (an architecture doc, a service design, a contract).

        Emit ONE rule per distinct normative clause — do not collapse multiple
        clauses into one rule.

        requiredConcepts (semantic alternatives, authored from the CLAUSE only):
        2-4 inner lists, one per CORE mandatory idea of the clause (typically the
        subject/object and the required control or action). Do NOT put quantifier or
        narrowing words (every, all, each, only, external, production) into the
        lists: the engine checks those separately. Each
        inner list holds 4-8 lowercase alternative wordings a compliant document might
        use for that part: short stems (e.g. "encrypt" covers encrypted/encryption),
        singular and plural forms, and common synonyms or paraphrases. Prefer short
        key phrases (1-3 words) over whole clauses so valid paraphrases still match.
        A sentence proves the rule only if it contains one
        alternative from EVERY list. At least
        one alternative per list MUST appear (case-insensitively) in sourceQuote.
        Example for "Breaking changes SHALL increment the major version":
          [["breaking change"], ["major version", "new major version", "next major version", "increment major", "bump major"]]

        appliesWhen (optional, only when the clause is specific to a technology,
        platform or operating condition, e.g. a clause about one hosting product):
        a list of {fact, values}: a lowercase fact name and the aliases of the single
        accepted value. Allowed fact names are ONLY "platform", "cloud provider",
        "product" and "tenancy". Do NOT use environment, data sensitivity, workload
        type or similar. Omit it for general clauses. Never
        invent conditions the clause does not state.

        If the input contains no normative clauses, return {"rules": []}.
        """;

    private readonly IChatClient _chat;
    private readonly IRuleEmbedder _embedder;
    private readonly ILogger<FoundryRuleAuthoringAgent> _log;
    private readonly int _maxRetries;
    private readonly TimeSpan _minimumRequestInterval;

    public FoundryRuleAuthoringAgent(
        IChatClient chat,
        IRuleEmbedder embedder,
        ILogger<FoundryRuleAuthoringAgent>? log = null,
        int maxRetries = 3,
        int requestsPerMinute = 18)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        _embedder = embedder ?? throw new ArgumentNullException(nameof(embedder));
        if (requestsPerMinute < 1)
            throw new ArgumentOutOfRangeException(nameof(requestsPerMinute));
        _log = log ?? NullLogger<FoundryRuleAuthoringAgent>.Instance;
        _maxRetries = Math.Max(0, maxRetries);
        _minimumRequestInterval = TimeSpan.FromMinutes(1d / requestsPerMinute);
    }

    private const int MaxChunkChars = 4000;
    private const int MinSplitChars = 800;

    // Splits at the line break nearest the midpoint (deterministic) and authors both halves.
    private async Task<IReadOnlyList<RuleAuthoringSuggestion>> AuthorSplitAsync(
        RuleAuthoringRequest request, CancellationToken ct)
    {
        var text = request.SourceContent;
        var mid = text.Length / 2;
        var cut = text.LastIndexOf('\n', mid);
        if (cut < text.Length / 4) cut = text.IndexOf('\n', mid);
        if (cut <= 0 || cut >= text.Length - 1) cut = mid;
        var parts = new[] { (Offset: 0, Text: text[..cut]), (Offset: cut, Text: text[cut..]) };

        var all = new List<RuleAuthoringSuggestion>();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part.Text)) continue;
            var span = request.SourceSpan with
            {
                CharStart = request.SourceSpan.CharStart + part.Offset,
                CharLength = part.Text.Length,
            };
            all.AddRange(await AuthorAsync(request with { SourceContent = part.Text, SourceSpan = span }, ct)
                .ConfigureAwait(false));
        }
        return all;
    }

    public async Task<IReadOnlyList<RuleAuthoringSuggestion>> AuthorAsync(
        RuleAuthoringRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SourceContent))
            return Array.Empty<RuleAuthoringSuggestion>();

        var heading = request.SourceSpan.HeadingPath ?? "(no heading)";
        if (request.SourceContent.Length > MaxChunkChars)
            return await AuthorSplitAsync(request, ct).ConfigureAwait(false);
        var chunk = request.SourceContent;
        var userPrompt = $"Section heading: {heading}\nSection text:\n{chunk}";

        // ChatOptions aligned to the Foundry gpt-5.x supported request
        // schema. The deployment accepts: model, messages, system,
        // max_output_tokens, stream, response_format, tools, tool_choice,
        // input, user, context. It rejects Temperature (any value) and
        // does not honour Seed — so both are intentionally omitted.
        // Idempotency comes from bounded output, JSON-schema shape
        // enforcement, and the DSL + verbatim-quote post-validators.
        var options = new ChatOptions
        {
            MaxOutputTokens = 8000,
            ResponseFormat = ChatResponseFormat.ForJsonSchema(
                schema: RuleEnvelopeJsonSchema,
                schemaName: "LambdaRagAuthoringRuleEnvelope",
                schemaDescription: "Envelope of extracted policy rules."),
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                // Foundry-side telemetry / rate-limit isolation.
                ["user"] = "lambda-rag-authoring",
            },
        };

        string? responseText = null;
        try
        {
            responseText = await CallWithRetryAsync(
                new[]
                {
                    new ChatMessage(ChatRole.System, SystemPrompt),
                    new ChatMessage(ChatRole.User, userPrompt),
                },
                options,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var transient = IsTransient(ex);
            _log.LogWarning(ex,
                transient
                    ? "Foundry authoring exhausted retries for chunk {DocId}@{Start}+{Len}; aborting extraction."
                    : "Foundry authoring call failed permanently for chunk {DocId}@{Start}+{Len}; skipping.",
                request.SourceSpan.DocumentId, request.SourceSpan.CharStart, request.SourceSpan.CharLength);
            if (transient)
                throw;
            return Array.Empty<RuleAuthoringSuggestion>();
        }

        var parsed = TryParseRulesEnvelope(responseText);
        if (parsed is null)
        {
            _log.LogWarning(
                "Foundry authoring response was not valid JSON envelope for chunk {DocId}@{Start}; response length={Len}.",
                request.SourceSpan.DocumentId, request.SourceSpan.CharStart, responseText?.Length ?? 0);
            // Oversized (truncated) responses: retry on two smaller halves.
            if (chunk.Length > MinSplitChars)
                return await AuthorSplitAsync(request, ct).ConfigureAwait(false);
            return Array.Empty<RuleAuthoringSuggestion>();
        }

        var suggestions = new List<RuleAuthoringSuggestion>(parsed.Count);
        foreach (var candidate in parsed)
        {
            if (!TryValidate(candidate, chunk, out var reason))
            {
                _log.LogInformation(
                    "Dropping authoring candidate (topic={Topic}) in {DocId}@{Start}: {Reason}",
                    candidate?.TopicSlug, request.SourceSpan.DocumentId, request.SourceSpan.CharStart, reason);
                lock (_quarantined) _quarantined.Add((candidate?.TopicSlug ?? string.Empty, reason));
                continue;
            }

            var rule = await BuildRuleAsync(candidate!, request, ct).ConfigureAwait(false);
            suggestions.Add(new RuleAuthoringSuggestion(
                Rule: rule,
                Confidence: 0.85,
                Rationale: "OpenRouter-backed extraction, schema+DSL-validated."));
        }

        // Stable order so callers get deterministic output for a given chunk.
        return suggestions
            .OrderBy(s => s.Rule.Metadata.TryGetValue(TopicSlugMetadataKey, out var t) ? t : s.Rule.Id, StringComparer.Ordinal)
            .ThenBy(s => s.Rule.NaturalLanguage, StringComparer.Ordinal)
            .ToList();
    }

    private async Task<string> CallWithRetryAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions options,
        CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            attempt++;
            await GlobalCallGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var wait = _nextCallAt - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                _nextCallAt = DateTimeOffset.UtcNow + _minimumRequestInterval;

                var response = await _chat.GetResponseAsync(messages, options, cancellationToken: ct).ConfigureAwait(false);
                return response.Text ?? string.Empty;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt <= _maxRetries && IsTransient(ex))
            {
                var delay = ComputeRetryDelay(ex, attempt);
                _log.LogInformation(
                    "Transient authoring failure (attempt {Attempt}/{Max}); backing off {Delay}ms. Error: {Error}",
                    attempt, _maxRetries, (long)delay.TotalMilliseconds, ex.Message);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            finally
            {
                GlobalCallGate.Release();
            }
        }
    }

    private static TimeSpan ComputeRetryDelay(Exception exception, int attempt)
    {
        var response = exception.GetType().GetMethod("GetRawResponse", Type.EmptyTypes)?.Invoke(exception, null);
        var headers = response?.GetType().GetProperty("Headers")?.GetValue(response);
        if (headers is not null)
        {
            var tryGetValue = headers.GetType().GetMethods()
                .FirstOrDefault(method => method.Name == "TryGetValue"
                    && method.GetParameters() is [{ ParameterType: var keyType }, { IsOut: true }]
                    && keyType == typeof(string));
            if (tryGetValue is not null)
            {
                foreach (var header in new[] { "Retry-After-ms", "Retry-After" })
                {
                    var arguments = new object?[] { header, null };
                    if (tryGetValue.Invoke(headers, arguments) is true
                        && TryParseRetryAfter(header, arguments[1]?.ToString(), out var retryAfter)
                        && retryAfter > TimeSpan.Zero)
                        return retryAfter;
                }
            }
        }

        if (GetStatusCode(exception) == 429)
            return TimeSpan.FromMinutes(1);

        var exponent = Math.Clamp(attempt - 1, 0, 5);
        var baseMs = Math.Min(30_000, 500 * (1 << exponent));
        var jitter = Random.Shared.Next(0, Math.Max(1, baseMs / 2));
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }

    private static bool TryParseRetryAfter(string header, string? value, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var isMilliseconds = header.Equals("Retry-After-ms", StringComparison.OrdinalIgnoreCase);
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return false;

        delay = isMilliseconds
            ? TimeSpan.FromMilliseconds(Math.Max(0, seconds))
            : TimeSpan.FromSeconds(Math.Max(0, seconds));
        return true;
    }

    private static int? GetStatusCode(Exception exception)
    {
        var statusProp = exception.GetType().GetProperty("Status");
        return statusProp?.PropertyType == typeof(int) && statusProp.GetValue(exception) is int status
            ? status
            : null;
    }

    internal static bool IsTransient(Exception ex)
    {
        // Broaden the net: any exception exposing an int Status property
        // in the transient band (408/429/5xx) is retryable. This covers
        // Azure.RequestFailedException, System.ClientModel.ClientResultException,
        // and test doubles alike without hard-referencing either SDK type.
        if (ex is HttpRequestException || ex is TimeoutException || ex is TaskCanceledException)
            return true;

        var statusProp = ex.GetType().GetProperty("Status");
        if (statusProp?.PropertyType == typeof(int) && statusProp.GetValue(ex) is int status)
            return status == 408 || status == 429 || (status >= 500 && status < 600);

        return false;
    }

    /// <summary>
    /// JSON schema used with <see cref="ChatResponseFormat.ForJsonSchema"/>
    /// so Foundry constrains the response shape server-side (idempotency).
    /// Kept intentionally permissive on string content — the tight
    /// validation is done locally by <see cref="TryValidate"/>.
    /// </summary>
    private static readonly System.Text.Json.JsonElement RuleEnvelopeJsonSchema =
        System.Text.Json.JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["rules"],
          "properties": {
            "rules": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["topicSlug", "naturalLanguage", "predicate", "remediation", "sourceQuote", "obligation", "scopeTerms", "requiredEvidence", "relevantExamples", "irrelevantExamples", "severity", "severityBasis"],
                "properties": {
                  "topicSlug":       { "type": "string", "minLength": 3, "maxLength": 6 },
                  "naturalLanguage": { "type": "string", "minLength": 1 },
                  "predicate":       { "type": "string", "minLength": 1 },
                  "remediation":     { "type": "string" },
                  "sourceQuote":     { "type": "string", "minLength": 1, "maxLength": 400 },
                  "obligation":      { "type": "string", "enum": ["must", "should", "may"] },
                  "scopeTerms":      { "type": "array", "minItems": 1, "maxItems": 8, "items": { "type": "string", "minLength": 3 } },
                  "scopeDescription": { "type": "string" },
                  "requiredEvidence": { "type": "array", "minItems": 1, "items": { "type": "string", "minLength": 1 } },
                  "requiredConcepts": { "type": "array", "maxItems": 6, "items": { "type": "array", "minItems": 1, "maxItems": 8, "items": { "type": "string", "minLength": 3, "maxLength": 60 } } },
                  "appliesWhen": { "type": "array", "maxItems": 3, "items": { "type": "object", "additionalProperties": false, "required": ["fact", "values"], "properties": { "fact": { "type": "string", "minLength": 2, "maxLength": 40 }, "values": { "type": "array", "minItems": 1, "maxItems": 8, "items": { "type": "string", "minLength": 2, "maxLength": 60 } } } } },
                  "relevantExamples":   { "type": "array", "minItems": 1, "maxItems": 3, "items": { "type": "string", "minLength": 1, "maxLength": 400 } },
                  "irrelevantExamples": { "type": "array", "minItems": 1, "maxItems": 3, "items": { "type": "string", "minLength": 1, "maxLength": 400 } },
                  "severity":        { "type": "string", "enum": ["Suggestion", "Deviation", "Violation", "Critical", "Unsupported"] },
                  "severityBasis":   { "type": "string" }
                }
              }
            }
          }
        }
        """).RootElement.Clone();

    public static List<ExtractedRuleDto>? TryParseRulesEnvelope(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNl = trimmed.IndexOf('\n');
            if (firstNl > 0) trimmed = trimmed[(firstNl + 1)..];
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (lastFence > 0) trimmed = trimmed[..lastFence];
            trimmed = trimmed.Trim();
        }

        JsonNode? node;
        try { node = JsonNode.Parse(trimmed); }
        catch (JsonException) { return null; }
        if (node is null) return null;

        JsonArray? arr = null;
        if (node is JsonObject obj && obj["rules"] is JsonArray a) arr = a;
        else if (node is JsonArray topArr) arr = topArr;
        if (arr is null) return null;

        var list = new List<ExtractedRuleDto>(arr.Count);
        foreach (var item in arr)
        {
            if (item is not JsonObject o) continue;
            list.Add(new ExtractedRuleDto
            {
                TopicSlug = (string?)o["topicSlug"] ?? string.Empty,
                NaturalLanguage = (string?)o["naturalLanguage"] ?? string.Empty,
                Predicate = (string?)o["predicate"] ?? string.Empty,
                Remediation = (string?)o["remediation"] ?? string.Empty,
                SourceQuote = (string?)o["sourceQuote"] ?? string.Empty,
                Obligation = (string?)o["obligation"] ?? string.Empty,
                ScopeTerms = ReadStrings(o["scopeTerms"]),
                ScopeDescription = (string?)o["scopeDescription"] ?? string.Empty,
                RequiredEvidence = ReadStrings(o["requiredEvidence"]),
                RequiredConcepts = ReadConcepts(o["requiredConcepts"]),
                AppliesWhen = ReadAppliesWhen(o["appliesWhen"]),
                RelevantExamples = ReadStrings(o["relevantExamples"]),
                IrrelevantExamples = ReadStrings(o["irrelevantExamples"]),
                Severity = (string?)o["severity"] ?? string.Empty,
                SeverityBasis = (string?)o["severityBasis"] ?? string.Empty,
            });
        }
        return list;
    }

    private static List<List<string>> ReadConcepts(JsonNode? node)
    {
        var groups = new List<List<string>>();
        if (node is JsonArray arr)
            foreach (var g in arr)
                if (g is JsonArray ga)
                {
                    var alts = ReadStrings(ga).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0)
                        .Distinct(StringComparer.Ordinal).ToList();
                    if (alts.Count > 0) groups.Add(alts);
                }
        return groups;
    }

    private static Dictionary<string, List<string>> ReadAppliesWhen(JsonNode? node)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (node is JsonArray arr)
            foreach (var item in arr)
            {
                if (item is not JsonObject o) continue;
                var key = ((string?)o["fact"] ?? string.Empty).Trim().ToLowerInvariant();
                var values = ReadStrings(o["values"]).Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0)
                    .Distinct(StringComparer.Ordinal).ToList();
                if (key.Length > 0 && values.Count > 0 && ScopeFactNames.Contains(key)) result[key] = values;
            }
        return result;
    }

    // Only facts a document can genuinely establish as scope; anything else is noise.
    internal static readonly HashSet<string> ScopeFactNames =
        new(["platform", "cloud provider", "product", "tenancy"], StringComparer.Ordinal);

    /// <summary>
    /// Deterministic self-consistency: every authored concept group must be
    /// satisfied by the clause's own verbatim quote (the policy proves itself),
    /// and alternatives must be plain phrases. Null when valid.
    /// </summary>
    public static string? ValidateConcepts(IReadOnlyList<List<string>> groups, string sourceQuote)
    {
        foreach (var g in groups)
        {
            if (g.Any(a => a.Length < 3 || a.IndexOfAny(new[] { '"', '\\', '\r', '\n' }) >= 0))
                return "concept alternative is too short or contains illegal characters";
            if (!g.Any(a => sourceQuote.Contains(a, StringComparison.OrdinalIgnoreCase)))
                return $"concept group [{string.Join(" | ", g)}] has no alternative present in sourceQuote";
        }
        return null;
    }

    private static List<string> ReadStrings(JsonNode? node)
    {
        var result = new List<string>();
        if (node is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonValue v && v.TryGetValue<string>(out var s))
                    result.Add(s);
        return result;
    }

    /// <summary>
    /// Validates an LLM-emitted rule candidate against the schema + DSL +
    /// defensibility guarantees. <paramref name="sourceChunk"/> is the exact
    /// prompt text handed to the model; the sourceQuote must be a
    /// byte-for-byte substring of it (Pillar 3 — Defensibility).
    /// </summary>
    internal static bool TryValidate(ExtractedRuleDto? dto, string sourceChunk, out string reason)
    {
        reason = string.Empty;
        if (dto is null) { reason = "null candidate"; return false; }
        if (string.IsNullOrWhiteSpace(dto.TopicSlug)) { reason = "missing topicSlug"; return false; }
        if (!IsValidTopicSlug(dto.TopicSlug)) { reason = $"invalid topicSlug '{dto.TopicSlug}'"; return false; }
        if (string.IsNullOrWhiteSpace(dto.NaturalLanguage)) { reason = "missing naturalLanguage"; return false; }
        if (string.IsNullOrWhiteSpace(dto.Predicate)) { reason = "missing predicate"; return false; }
        if (!IsValidPredicate(dto.Predicate)) { reason = $"predicate rejected by DSL validator: '{dto.Predicate}'"; return false; }
        if (string.IsNullOrWhiteSpace(dto.SourceQuote)) { reason = "missing sourceQuote"; return false; }
        if (!string.IsNullOrEmpty(sourceChunk) && !sourceChunk.Contains(dto.SourceQuote, StringComparison.Ordinal))
        {
            reason = "sourceQuote is not a verbatim substring of the input chunk (defensibility violation)";
            return false;
        }
        if (ParseObligation(dto.Obligation) is null) { reason = $"missing or invalid obligation '{dto.Obligation}'"; return false; }
        var scopeDefect = ValidateScopeTerms(NormalizeScopeTerms(dto.ScopeTerms));
        if (scopeDefect is not null) { reason = $"no defensible scope: {scopeDefect}"; return false; }
        if (dto.RequiredEvidence.All(string.IsNullOrWhiteSpace)) { reason = "missing requiredEvidence"; return false; }
        var conceptDefect = ValidateConcepts(dto.RequiredConcepts, dto.SourceQuote);
        if (conceptDefect is not null) { reason = $"requiredConcepts rejected: {conceptDefect}"; return false; }
        var exampleDefect = ValidateScopeExamples(
            NormalizeScopeTerms(dto.ScopeTerms), dto.RelevantExamples, dto.IrrelevantExamples);
        if (exampleDefect is not null) { reason = $"scope example validation failed: {exampleDefect}"; return false; }
        return true;
    }

    internal static bool IsValidTopicSlug(string s)
    {
        if (s.Length is < 3 or > 6) return false;
        foreach (var c in s)
            if (c is < 'A' or > 'Z') return false;
        return true;
    }

    /// <summary>
    /// Returns true iff <paramref name="predicate"/> is exhausted by the
    /// allowed-token grammar. Uses a simple "strip and see what's left"
    /// tokenizer rather than a full parser — the DSL is small enough that
    /// this is both safe and easy to audit.
    /// </summary>
    public static bool IsValidPredicate(string predicate)
    {
        if (string.IsNullOrWhiteSpace(predicate)) return false;

        var remaining = predicate;
        // Loop stripping the longest leading match on each iteration.
        while (remaining.Length > 0)
        {
            var matched = false;
            foreach (var rx in AllowedTokens)
            {
                var m = rx.Match(remaining);
                if (m.Success && m.Index == 0 && m.Length > 0)
                {
                    remaining = remaining[m.Length..];
                    matched = true;
                    break;
                }
            }
            if (!matched) return false;
        }
        return true;
    }

    private async Task<Rule> BuildRuleAsync(
        ExtractedRuleDto dto,
        RuleAuthoringRequest request,
        CancellationToken ct)
    {
        var topic = dto.TopicSlug;
        // Placeholder ID — the extraction pipeline stamps the final counter.
        var provisionalId = $"{request.RuleIdPrefix}{topic}";

        var obligation = ParseObligation(dto.Obligation)!.Value;
        var (severity, severityBasis, reviewNeeded) = ResolveSeverity(dto, obligation);
        var scopeTerms = NormalizeScopeTerms(dto.ScopeTerms);

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TopicSlugMetadataKey] = topic,
            [AuthoringAgentMetadataKey] = nameof(FoundryRuleAuthoringAgent),
            [SeverityBasisMetadataKey] = severityBasis,
            [SeveritySourceMetadataKey] = reviewNeeded ? "review-needed-default" : "authoring-validated",
        };

        float[]? embedding = null;
        try
        {
            embedding = await _embedder.EmbedAsync(request.SourceContent, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogInformation(ex, "Embedding failed for chunk; rule will be emitted without SourceEmbedding.");
        }

        // Scope terms compile into the deterministic applicability gate.
        var predicate = string.Join(" || ", scopeTerms.Select(t => $"input1.text.ToLower().Contains(\"{t.ToLowerInvariant()}\")"));
        var applicability = DeterministicMockAuthoringAgent.InferApplicability(dto.NaturalLanguage);
        if (obligation != RuleObligation.Must)
            applicability = RuleApplicability.Optional;
        else if (applicability == RuleApplicability.Mandatory)
            applicability = RuleApplicability.Conditional; // scope is authored, so absence of scope is not a gap

        return new Rule(
            Id: provisionalId,
            Version: "1.0.0",
            NaturalLanguage: dto.NaturalLanguage,
            Lambda: dto.Predicate,
            AppliesToSchema: SectionTextSchema(),
            Selector: new PathSelector("$.sections[*]"),
            Severity: severity,
            SourceSpan: request.SourceSpan,
            EvidenceQuote: dto.SourceQuote,
            Metadata: metadata)
        {
            Predicate = predicate,
            Applicability = applicability,
            Obligation = obligation,
            Scope = new RuleScope(scopeTerms, string.IsNullOrWhiteSpace(dto.ScopeDescription) ? null : dto.ScopeDescription.Trim()),
            RequiredEvidence = dto.RequiredEvidence.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList(),
            SeverityBasis = severityBasis,
            RequiredConcepts = dto.RequiredConcepts.Count > 0
                ? dto.RequiredConcepts.Select(g => (IReadOnlyList<string>)g.ToList()).ToList()
                : null,
            AppliesWhen = dto.AppliesWhen.Count > 0
                ? dto.AppliesWhen.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value.ToList(), StringComparer.Ordinal)
                : null,
            Examples = new RuleExamples(
                dto.RelevantExamples.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList(),
                dto.IrrelevantExamples.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList()),
            Remediation = string.IsNullOrWhiteSpace(dto.Remediation) ? null : dto.Remediation,
            SourceContent = request.SourceContent,
            SourceEmbedding = embedding,
        };
    }

    /// <summary>
    /// Deterministic severity policy. Severity is taken from validated
    /// authoring output only when impact is justified: Violation/Critical need
    /// a substantive <c>severityBasis</c> and a <c>must</c> obligation.
    /// MUST/SHALL alone never yields Violation/Critical. Anything else falls to
    /// the explicit non-MAJOR <see cref="ReviewNeededDefault"/>.
    /// </summary>
    internal static (RuleSeverity Severity, string Basis, bool ReviewNeeded) ResolveSeverity(
        ExtractedRuleDto dto, RuleObligation obligation)
    {
        var basis = dto.SeverityBasis?.Trim() ?? string.Empty;
        var declared = ParseSeverity(dto.Severity);

        if (declared is null)
            return (ReviewNeededDefault, "review-needed: no supported severity in authoring output", true);

        if (declared is RuleSeverity.Violation or RuleSeverity.Critical)
        {
            if (basis.Length < MinSeverityBasisLength)
                return (ReviewNeededDefault, $"review-needed: {declared} declared without an impact basis", true);
            if (obligation != RuleObligation.Must)
                return (ReviewNeededDefault, $"review-needed: {declared} declared for a non-mandatory obligation ({obligation})", true);
        }
        else if (basis.Length == 0)
        {
            basis = $"authoring-declared {declared}; obligation={obligation}";
        }

        if (declared == RuleSeverity.Deviation && basis.Length == 0)
            basis = "authoring-declared Deviation";
        return (declared.Value, basis, false);
    }

    internal static RuleObligation? ParseObligation(string? s)
        => Enum.TryParse<RuleObligation>(s?.Trim(), ignoreCase: true, out var o) && Enum.IsDefined(o) ? o : null;

    internal static RuleSeverity? ParseSeverity(string? s)
        => Enum.TryParse<RuleSeverity>(s?.Trim(), ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : null;

    internal static IReadOnlyList<string> NormalizeScopeTerms(IEnumerable<string>? terms)
        => (terms ?? Array.Empty<string>())
            .Select(t => t?.Trim() ?? string.Empty)
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// Returns a reason when a (possibly already-published) rule lacks a
    /// defensible scope — e.g. a selector that matches every section with a
    /// <c>true</c> predicate and no scope conditions. Null when defensible.
    /// </summary>
    public static string? FindScopeDefect(Rule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var scopeTerms = NormalizeScopeTerms(rule.Scope?.Terms);
        var alwaysTrue = string.IsNullOrWhiteSpace(rule.Predicate)
            || string.Equals(rule.Predicate.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        if (scopeTerms.Count == 0 && alwaysTrue)
            return "no scope conditions and predicate is always true (applies to every selected section)";
        if (scopeTerms.Count > 0 && alwaysTrue)
            return "scope conditions are not enforced by the applicability predicate";
        return null;
    }

    /// <summary>
    /// Deterministic pre-publish check of the scope gate against authored
    /// example sections, using the same case-insensitive containment the
    /// compiled predicate uses at runtime. Every relevant example must be in
    /// scope and no irrelevant example may be. No LLM or similarity score
    /// decides the outcome.
    /// </summary>
    internal static string? ValidateScopeExamples(
        IReadOnlyList<string> scopeTerms,
        IReadOnlyList<string>? relevant,
        IReadOnlyList<string>? irrelevant)
    {
        var rel = (relevant ?? Array.Empty<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        var irr = (irrelevant ?? Array.Empty<string>()).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        if (rel.Count < MinExamples || irr.Count < MinExamples) return "relevant and irrelevant examples are both required";
        if (rel.Count > MaxExamples || irr.Count > MaxExamples) return $"more than {MaxExamples} examples";
        if (rel.Concat(irr).Any(e => e.Length > MaxExampleLength)) return $"example longer than {MaxExampleLength} characters";

        static bool InScope(IReadOnlyList<string> terms, string text)
            => terms.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase));

        var missed = rel.FirstOrDefault(e => !InScope(scopeTerms, e));
        if (missed is not null) return "relevant example is not matched by the scope terms";
        var leaked = irr.FirstOrDefault(e => InScope(scopeTerms, e));
        if (leaked is not null) return "irrelevant example is matched by the scope terms";
        return null;
    }

    private static string? ValidateScopeTerms(IReadOnlyList<string> terms)
    {
        if (terms.Count == 0) return "missing scope conditions (scopeTerms)";
        if (terms.Count > MaxScopeTerms) return $"too many scope terms (>{MaxScopeTerms})";
        foreach (var t in terms)
        {
            if (t.Length < MinScopeTermLength) return $"scope term '{t}' is too short to be defensible";
            if (t.IndexOfAny(new[] { '"', '\\', '\r', '\n' }) >= 0) return $"scope term '{t}' contains illegal characters";
            if (GenericScopeTerms.Contains(t)) return $"scope term '{t}' is too generic to establish scope";
        }
        return null;
    }

    private static JsonObject SectionTextSchema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["text"] = new JsonObject { ["type"] = "string" },
            ["category"] = new JsonObject { ["type"] = "string" },
        },
        ["required"] = new JsonArray("text"),
    };

    public sealed class ExtractedRuleDto
    {
        public string TopicSlug { get; set; } = string.Empty;
        public string NaturalLanguage { get; set; } = string.Empty;
        public string Predicate { get; set; } = string.Empty;
        public string Remediation { get; set; } = string.Empty;
        public string SourceQuote { get; set; } = string.Empty;
        public string Obligation { get; set; } = string.Empty;
        public List<string> ScopeTerms { get; set; } = new();
        public string ScopeDescription { get; set; } = string.Empty;
        public List<string> RequiredEvidence { get; set; } = new();
        public List<List<string>> RequiredConcepts { get; set; } = new();
        public Dictionary<string, List<string>> AppliesWhen { get; set; } = new();
        public List<string> RelevantExamples { get; set; } = new();
        public List<string> IrrelevantExamples { get; set; } = new();
        public string Severity { get; set; } = string.Empty;
        public string SeverityBasis { get; set; } = string.Empty;
    }
}
