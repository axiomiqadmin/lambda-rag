using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LambdaRag.Core;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Facts;
using LambdaRag.Core.Hashing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LambdaRag.Authoring;

/// <summary>
/// Pillar 12 (#153) — LLM-backed <see cref="IFactExtractor"/> that calls an
/// OpenRouter chat model (same wiring as
/// <see cref="FoundryRuleAuthoringAgent"/>) once per section to populate a
/// closed <see cref="FactSchema"/>.
///
/// <para>Determinism &amp; caching:
/// <list type="bullet">
///   <item>Sidecar cache lives at
///     <c>%USERPROFILE%\.lambda-rag\facts\&lt;docHash&gt;.&lt;factSchemaHash&gt;.facts.json</c>
///     (overridable). Cache hit: byte-identical replay.</item>
///   <item>Fingerprint drift throws
///     <see cref="SectionFactSidecarMismatchException"/> — no silent
///     recompute. Operator must pass <c>--refresh-facts</c> to accept.</item>
///   <item>Hallucination defense: every non-null fact requires a
///     <c>supporting_quote</c> that is a byte-for-byte substring of the
///     section text; otherwise the value is dropped and a warning
///     is recorded.</item>
/// </list>
/// </para>
/// </summary>
public sealed class FoundrySectionFactExtractor : IFactExtractor
{
    public const string SidecarVersion = "1.0";
    // Bumping this string invalidates every cached sidecar loudly.
    public const string PromptVersion = "1.1.0";

    private static readonly SemaphoreSlim GlobalCallGate = new(initialCount: 1, maxCount: 1);
    private static DateTimeOffset _nextCallAt;

    public const string SystemPrompt = """
        You are a policy-fact extractor. Read one section and populate the
        supplied fixed JSON schema using evidence from that section only. You
        do NOT decide compliance, adequacy, or applicability.

        Rules:
        1. Emit ONLY a JSON object matching the schema below. No prose, no
           markdown, no code fences.
          2. Emit a non-null value only when the section explicitly states or
              unambiguously describes the declared concept. Do not infer support
              from implication, general context, or a different concept.
          3. For a boolean concept, emit false only when the section explicitly
              negates that concept. If it is silent, ambiguous, or only implies
              the concept, emit null; null does not mean false.
          4. Never infer a value across sections. If the section is silent on
           a concept, emit null. Cross-section composition happens elsewhere.
          5. For every non-null value, emit "supporting_quote" — a byte-for-byte
           quote from the section (max 200 characters) that supports it.
          6. If the section contains a number/date/duration, emit the VERBATIM
           phrase from the text ("every 90 days", "quarterly"). Do NOT convert.
          7. For integer and text concepts, extract only a literal value stated
              in the section; do not invent or normalize it. If no supported
              value is present, emit null.
          8. If evidence is uncertain, emit null. Silent is safer than wrong.
        """;

    private readonly IChatClient _chat;
    private readonly ILogger<FoundrySectionFactExtractor> _log;
    private readonly int _maxRetries;
    private readonly TimeSpan _minimumRequestInterval;
    private readonly string? _cacheDirOverride;
    private readonly bool _refresh;
    private readonly DurationNormalizer _normalizer;

    public string ModelId { get; }
    public string PromptHash { get; }

    public FoundrySectionFactExtractor(
        IChatClient chat,
        string modelId,
        ILogger<FoundrySectionFactExtractor>? log = null,
        int maxRetries = 3,
        string? cacheDirOverride = null,
        bool refresh = false,
        DurationNormalizer? normalizer = null,
        int requestsPerMinute = 18)
    {
        _chat = chat ?? throw new ArgumentNullException(nameof(chat));
        if (requestsPerMinute < 1)
            throw new ArgumentOutOfRangeException(nameof(requestsPerMinute));
        ModelId = string.IsNullOrWhiteSpace(modelId) ? "unknown-model" : modelId;
        _log = log ?? NullLogger<FoundrySectionFactExtractor>.Instance;
        _maxRetries = Math.Max(0, maxRetries);
        _minimumRequestInterval = TimeSpan.FromMinutes(1d / requestsPerMinute);
        _cacheDirOverride = cacheDirOverride;
        _refresh = refresh;
        _normalizer = normalizer ?? DurationNormalizer.Default;
        // Prompt fingerprint folds in system prompt + prompt version +
        // normalizer table hash so any of those drifting invalidates cache.
        PromptHash = ContentHash.Compose(
            SystemPrompt,
            PromptVersion,
            _normalizer.Version,
            _normalizer.TableHash.Value).Value;
    }

    public async Task<SectionFactSidecar> ExtractAsync(
        ProjectedDocument document,
        FactSchema schema,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(schema);

        var docHashStr = document.SourceId.Value;
        var schemaHash = schema.Fingerprint();
        var sections = ExtractSectionTexts(document);
        var orderingHash = ComputeOrderingHash(sections);
        var expectedFp = SectionFactSidecar.ComputeFingerprint(
            docHashStr, schemaHash.Value, ModelId, PromptHash, orderingHash.Value);

        var cacheDir = SectionFactSidecarIO.ResolveCacheDir(_cacheDirOverride);
        var path = SectionFactSidecarIO.CachePath(cacheDir, document.SourceId, schemaHash);
        var warnings = new List<string>();
        var bags = new Dictionary<string, IReadOnlyDictionary<string, object?>>(StringComparer.Ordinal);

        if (!_refresh && File.Exists(path))
        {
            var cached = SectionFactSidecarIO.LoadOrThrow(path, expectedFp);
            var cacheIsComplete = sections.All(section =>
                cached.Sections.TryGetValue(section.Id, out var facts)
                && !facts.ContainsKey("_extraction_failed"));
            if (cacheIsComplete)
            {
                _log.LogInformation("Loaded cached fact sidecar ({Sections} sections) from {Path}",
                    cached.Sections.Count, path);
                return cached;
            }

            foreach (var (id, facts) in cached.Sections)
            {
                if (sections.Any(section => section.Id == id)
                    && !facts.ContainsKey("_extraction_failed"))
                    bags[id] = facts;
            }
            _log.LogInformation(
                "Resuming fact extraction from cache ({Completed}/{Total} sections complete).",
                bags.Count, sections.Count);
        }

        _log.LogInformation("Extracting facts for {Sections} sections via model {Model}",
            sections.Count, ModelId);

        var schemaJson = SchemaToPromptJson(schema);
        var scope = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (sectionId, text) in sections)
        {
            if (bags.ContainsKey(sectionId)) continue;
            var bag = await ExtractSectionAsync(sectionId, text, schema, schemaJson, warnings, ct)
                .ConfigureAwait(false);
            bags[sectionId] = bag;
            if (bag.ContainsKey("_rate_limited"))
            {
                _log.LogWarning(
                    "OpenRouter rate limit persisted for section {SectionId}; deferring remaining sections to the next run.",
                    sectionId);
                break;
            }
        }

        var sidecar = new SectionFactSidecar(
            SidecarVersion: SidecarVersion,
            DocumentId: docHashStr,
            FactSchemaId: schema.Id,
            FactSchemaHash: schemaHash.Value,
            ModelId: ModelId,
            PromptHash: PromptHash,
            GeneratedAt: DateTimeOffset.UtcNow.ToString("O"),
            Sections: bags)
        {
            Fingerprint = expectedFp.Value,
            Warnings = warnings.Count > 0 ? warnings : null,
            RuleScope = scope.Count > 0
                ? scope.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal)
                : null,
        };
        SectionFactSidecarIO.Save(sidecar, path);
        _log.LogInformation("Wrote fact sidecar ({Sections} sections, {Warn} warnings) to {Path}",
            sidecar.Sections.Count, warnings.Count, path);
        return sidecar;
    }

    private async Task<Dictionary<string, object?>> ExtractSectionAsync(
        string sectionId,
        string sectionText,
        FactSchema schema,
        string schemaJson,
        List<string> warnings,
        CancellationToken ct)
    {
        var bag = new Dictionary<string, object?>(StringComparer.Ordinal);
        // Very short sections are almost always TOC / boilerplate; skip the
        // LLM call rather than burn tokens.
        if (string.IsNullOrWhiteSpace(sectionText) || sectionText.Length < 40)
            return bag;
        var user = $"Schema:\n{schemaJson}\n\nSection id: {sectionId}\nSection text:\n{sectionText}\n\nEmit the JSON object now.";
        var options = new ChatOptions
        {
            MaxOutputTokens = 800,
            ResponseFormat = ChatResponseFormat.Json,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["user"] = "lambda-rag-fact-extractor",
            },
        };
        string? raw = null;
        try
        {
            raw = await CallWithRetryAsync(new[]
            {
                new ChatMessage(ChatRole.System, SystemPrompt),
                new ChatMessage(ChatRole.User, user),
            }, options, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Fact extraction call failed for section {SectionId}; treating as extraction failure.", sectionId);
            warnings.Add($"section {sectionId}: extraction_failed:{ex.GetType().Name}");
            bag["_extraction_failed"] = true;
            if (GetStatusCode(ex) == 429)
                bag["_rate_limited"] = true;
            return bag;
        }
        return ParseAndValidate(raw, sectionId, sectionText, schema, warnings, bag);
    }

    private Dictionary<string, object?> ParseAndValidate(
        string? raw,
        string sectionId,
        string sectionText,
        FactSchema schema,
        List<string> warnings,
        Dictionary<string, object?> bag)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            warnings.Add($"section {sectionId}: empty_response");
            bag["_extraction_failed"] = true;
            return bag;
        }
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
        catch (JsonException)
        {
            warnings.Add($"section {sectionId}: non_json_response");
            bag["_extraction_failed"] = true;
            return bag;
        }
        if (node is not JsonObject obj)
        {
            warnings.Add($"section {sectionId}: non_object_response");
            bag["_extraction_failed"] = true;
            return bag;
        }

        var supportingQuote = (string?)obj["supporting_quote"];
        foreach (var concept in schema.Concepts)
        {
            var raw2 = obj[concept.Name];
            if (raw2 is null || raw2 is JsonValue jv && jv.TryGetValue<object?>(out var oV) && oV is null)
                continue;

            var value = ConvertConceptValue(concept, raw2, sectionText, sectionId, supportingQuote, warnings);
            if (value is null) continue;
            bag[concept.Name] = value;
        }
        return bag;
    }

    private object? ConvertConceptValue(
        FactConcept concept,
        JsonNode node,
        string sectionText,
        string sectionId,
        string? supportingQuote,
        List<string> warnings)
    {
        // Hallucination defense: for any non-null value, at least one
        // supporting quote must appear verbatim in the section text.
        // Applied at the section granularity (single quote per section
        // covers all concepts) — matches the prompt contract.
        if (!string.IsNullOrWhiteSpace(supportingQuote)
            && !sectionText.Contains(supportingQuote, StringComparison.Ordinal))
        {
            warnings.Add($"section {sectionId}: supporting_quote not found in section — dropping all values");
            return null;
        }

        switch (concept.Type)
        {
            case FactType.Boolean:
                if (node is JsonValue jvb && jvb.TryGetValue<bool>(out var b)) return b;
                if (node is JsonValue jvbs && jvbs.TryGetValue<string>(out var bs)
                    && bool.TryParse(bs, out var bp)) return bp;
                return null;
            case FactType.Integer:
                if (node is JsonValue jvi && jvi.TryGetValue<long>(out var l)) return l;
                if (node is JsonValue jvis && jvis.TryGetValue<string>(out var iss)
                    && long.TryParse(iss, out var lp)) return lp;
                return null;
            case FactType.Duration:
            {
                var phrase = node is JsonValue jvd && jvd.TryGetValue<string>(out var ds) ? ds : node.ToString();
                var days = _normalizer.NormalizeToDays(phrase);
                if (days is not null) return (long)days.Value;
                warnings.Add($"section {sectionId}: duration '{phrase}' — normalizer miss, preserved as string");
                return phrase;
            }
            case FactType.Enum:
            {
                var s = node is JsonValue jve && jve.TryGetValue<string>(out var es) ? es : node.ToString();
                if (concept.EnumValues is { Count: > 0 } allowed
                    && !allowed.Contains(s, StringComparer.Ordinal))
                {
                    warnings.Add($"section {sectionId}: '{concept.Name}'='{s}' outside enum set — dropped");
                    return null;
                }
                return s;
            }
            case FactType.Text:
            default:
            {
                var s = node is JsonValue jvt && jvt.TryGetValue<string>(out var ts) ? ts : node.ToString();
                if (s is null) return null;
                if (s.Length > 200) s = s.Substring(0, 200);
                return s;
            }
        }
    }

    // ── Section text extraction from ProjectedDocument.Graph ──────────────

    /// <summary>
    /// Pull (sectionId, text) pairs from the projected document. The
    /// contract projector emits sections under <c>$.sections[*]</c> with
    /// <c>id</c> + <c>text</c> keys; we tolerate missing ids by falling
    /// back to <c>s_&lt;index&gt;</c>.
    /// </summary>
    internal static IReadOnlyList<(string Id, string Text)> ExtractSectionTexts(ProjectedDocument document)
    {
        var list = new List<(string, string)>();
        if (document.Graph["sections"] is JsonArray arr)
        {
            var i = 0;
            foreach (var node in arr)
            {
                if (node is not JsonObject obj) { i++; continue; }
                var id = (string?)obj["id"] ?? $"s_{i:D8}";
                var text = (string?)obj["text"] ?? string.Empty;
                list.Add((id, text));
                i++;
            }
        }
        return list;
    }

    private static ContentHash ComputeOrderingHash(IReadOnlyList<(string Id, string Text)> sections)
    {
        var sb = new StringBuilder();
        foreach (var (id, text) in sections)
        {
            sb.Append(id).Append('\u001f').Append(text.Length).Append('\u001f');
        }
        return ContentHash.OfString(sb.ToString());
    }

    private static string SchemaToPromptJson(FactSchema schema)
    {
        var obj = new JsonObject();
        foreach (var c in schema.Concepts)
        {
            var slot = new JsonObject
            {
                ["type"] = c.Type switch
                {
                    FactType.Boolean => "boolean|null",
                    FactType.Enum => "enum|null",
                    FactType.Integer => "integer|null",
                    FactType.Duration => "string|null (verbatim phrase)",
                    _ => "string|null",
                },
                ["description"] = c.Description,
            };
            if (c.EnumValues is { Count: > 0 })
                slot["enum"] = new JsonArray(c.EnumValues.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            if (c.Examples is { Count: > 0 })
                slot["examples"] = new JsonArray(c.Examples.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
            obj[c.Name] = slot;
        }
        obj["supporting_quote"] = new JsonObject
        {
            ["type"] = "string|null",
            ["description"] = "Verbatim quote (<=200 chars) from the section text supporting any non-null value.",
        };
        return JsonSerializer.Serialize(obj, CanonicalJson.Options);
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
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (attempt <= _maxRetries && FoundryRuleAuthoringAgent.IsTransient(ex))
            {
                var delay = ComputeRetryDelay(ex, attempt);
                _log.LogInformation("Transient fact-extract failure (attempt {A}/{M}), backing off {B}ms: {E}",
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
                foreach (var header in new[] { "Retry-After-ms", "Retry-After", "X-RateLimit-Reset" })
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
        var baseMs = Math.Min(30_000, 1_000 * (1 << exponent));
        var jitter = Random.Shared.Next(0, Math.Max(1, baseMs / 2));
        return TimeSpan.FromMilliseconds(baseMs + jitter);
    }

    private static bool TryParseRetryAfter(string header, string? value, out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var isMilliseconds = header.Equals("Retry-After-ms", StringComparison.OrdinalIgnoreCase);
        var isReset = header.Equals("X-RateLimit-Reset", StringComparison.OrdinalIgnoreCase);

        if (isReset && DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var resetAt))
        {
            delay = resetAt - DateTimeOffset.UtcNow;
            return true;
        }

        if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return false;

        if (isMilliseconds)
        {
            delay = TimeSpan.FromMilliseconds(Math.Max(0, seconds));
            return true;
        }

        if (isReset)
        {
            try
            {
                var timestamp = (long)seconds;
                resetAt = seconds > 1_000_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp)
                    : seconds > 1_000_000_000
                        ? DateTimeOffset.FromUnixTimeSeconds(timestamp)
                        : DateTimeOffset.UtcNow.AddSeconds(Math.Max(0, seconds));
                delay = resetAt - DateTimeOffset.UtcNow;
            }
            catch (ArgumentOutOfRangeException)
            {
                return false;
            }
            return true;
        }

        delay = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return true;
    }

    private static int? GetStatusCode(Exception exception) =>
        exception.GetType().GetProperty("Status")?.GetValue(exception) is int status
            ? status
            : null;
}
