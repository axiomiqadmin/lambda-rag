using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Ontology;

public sealed record OntologyGenerationSpan(
    int Ordinal,
    string SpanId,
    string SectionId,
    SpanKind Kind,
    int HeadingLevel,
    string Text,
    string TextFingerprint,
    int StartOffset,
    int EndOffset,
    int? PageNumber)
{
    public static OntologyGenerationSpan FromRecord(SpanVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new OntologyGenerationSpan(
            record.Ordinal,
            record.InstanceId.Value,
            record.Observation.AncestryPath,
            record.Kind,
            record.Observation.HeadingLevel,
            record.Text,
            record.ContentFingerprint.Value,
            record.Observation.CharStart,
            checked(record.Observation.CharStart + record.Observation.CharLength),
            record.Observation.PageNumber);
    }

    internal JsonObject ToCanonicalJson() => new()
    {
        ["endOffset"] = EndOffset,
        ["headingLevel"] = HeadingLevel,
        ["kind"] = Kind.ToString(),
        ["ordinal"] = Ordinal,
        ["pageNumber"] = PageNumber,
        ["sectionId"] = OntologyCanonical.Normalize(SectionId),
        ["spanId"] = OntologyCanonical.Normalize(SpanId),
        ["startOffset"] = StartOffset,
        ["text"] = OntologyCanonical.Normalize(Text),
        ["textFingerprint"] = OntologyArtifactValidator.RequiredHash(TextFingerprint, "span.textFingerprint"),
    };
}

public sealed record OntologyGenerationRequest(
    string SourceDocumentFingerprint,
    IReadOnlyList<OntologyGenerationSpan> ChangedSpans,
    IReadOnlyList<AcceptedConcept> AcceptedConceptContext,
    string SchemaVersion,
    string ExtractionContractVersion,
    string GeneratorIdentity,
    IReadOnlyDictionary<string, string> DeterministicSettings,
    ContentHash RequestFingerprint)
{
    public CandidateOntologyDocument? CachedGenerationResult { get; init; }

    public static OntologyGenerationRequest Create(
        string sourceDocumentFingerprint,
        IReadOnlyList<OntologyGenerationSpan> changedSpans,
        IReadOnlyList<AcceptedConcept> acceptedConceptContext,
        string extractionContractVersion,
        string generatorIdentity,
        IReadOnlyDictionary<string, string>? deterministicSettings = null)
    {
        var documentFingerprint = OntologyArtifactValidator.RequiredHash(
            sourceDocumentFingerprint, "sourceDocumentFingerprint");
        OntologyArtifactValidator.Required(extractionContractVersion, "extractionContractVersion");
        OntologyArtifactValidator.Required(generatorIdentity, "generatorIdentity");
        ArgumentNullException.ThrowIfNull(changedSpans);
        ArgumentNullException.ThrowIfNull(acceptedConceptContext);

        var spans = changedSpans.OrderBy(span => span.Ordinal).ToArray();
        var spanIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var span in spans)
        {
            if (span.Ordinal < 0 || span.StartOffset < 0 || span.EndOffset <= span.StartOffset)
                throw new OntologyArtifactValidationException("Generation span has invalid ordinal or offsets.");
            OntologyArtifactValidator.Required(span.SpanId, "generationSpan.spanId");
            OntologyArtifactValidator.Required(span.SectionId, "generationSpan.sectionId");
            var text = OntologyCanonical.Normalize(span.Text, "generationSpan.text");
            var textFingerprint = OntologyArtifactValidator.RequiredHash(
                span.TextFingerprint, "generationSpan.textFingerprint");
            if (!string.Equals(SpanContentFingerprint.FromText(text).Value, textFingerprint, StringComparison.Ordinal))
                throw new OntologyArtifactValidationException("Generation span text does not match its canonical fingerprint.");
            if (!spanIds.Add(span.SpanId))
                throw new OntologyArtifactValidationException("Generation request contains duplicate span IDs.");
        }

        var context = acceptedConceptContext
            .OrderBy(concept => OntologyCanonical.Normalize(concept.ConceptId), StringComparer.Ordinal)
            .ToArray();
        foreach (var concept in context)
            OntologyModelValidator.ValidateConcept(concept, expectedDocumentFingerprint: null);

        var settings = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in deterministicSettings ?? new Dictionary<string, string>())
        {
            var normalizedKey = OntologyCanonical.NormalizeRequired(key, "deterministicSettings.key");
            if (!settings.TryAdd(normalizedKey, OntologyCanonical.Normalize(value, $"deterministicSettings.{key}")))
                throw new OntologyArtifactValidationException("Generation settings contain duplicate normalized keys.");
        }

        var spansJson = new JsonArray(spans.Select(span => (JsonNode?)span.ToCanonicalJson()).ToArray());
        var contextJson = new JsonArray(context.Select(concept => (JsonNode?)concept.ToCanonicalJson()).ToArray());
        var settingsJson = new JsonObject();
        foreach (var (key, value) in settings)
            settingsJson[key] = value;

        var material = new JsonObject
        {
            ["acceptedConceptContext"] = contextJson,
            ["changedSpans"] = spansJson,
            ["extractionContractVersion"] = OntologyCanonical.Normalize(extractionContractVersion),
            ["generatorIdentity"] = OntologyCanonical.Normalize(generatorIdentity),
            ["schemaVersion"] = CandidateOntologyDocument.CurrentSchemaVersion,
            ["sourceDocumentFingerprint"] = documentFingerprint,
            ["settings"] = settingsJson,
        };
        var fingerprint = ContentHash.OfBytes(OntologyCanonicalJson.SerializeToUtf8(material));

        return new OntologyGenerationRequest(
            documentFingerprint,
            Array.AsReadOnly(spans),
            Array.AsReadOnly(context),
            CandidateOntologyDocument.CurrentSchemaVersion,
            OntologyCanonical.Normalize(extractionContractVersion),
            OntologyCanonical.Normalize(generatorIdentity),
            settings,
            fingerprint);
    }
}

public interface ICandidateOntologyGenerator
{
    Task<CandidateOntologyDocument> GenerateAsync(
        OntologyGenerationRequest request,
        CancellationToken cancellationToken = default);
}

public interface ICandidateOntologyCache
{
    Task<CandidateOntologyDocument?> GetAsync(
        ContentHash requestFingerprint,
        CancellationToken cancellationToken = default);

    Task PutAsync(
        ContentHash requestFingerprint,
        CandidateOntologyDocument candidate,
        CancellationToken cancellationToken = default);
}

public sealed class OntologyGeneratorUnavailableException : InvalidOperationException
{
    public OntologyGeneratorUnavailableException(string requestFingerprint)
        : base($"No cached candidate ontology exists for request '{requestFingerprint}', and no generator is available.") { }
}

public sealed class OntologyCachedArtifactMismatchException : InvalidOperationException
{
    public OntologyCachedArtifactMismatchException(string requestFingerprint, string reason)
        : base($"Cached candidate artifact for request '{requestFingerprint}' is invalid: {reason}") { }
}

public sealed class InMemoryCandidateOntologyCache : ICandidateOntologyCache
{
    private readonly ConcurrentDictionary<string, byte[]> _entries = new(StringComparer.Ordinal);

    public Task<CandidateOntologyDocument?> GetAsync(
        ContentHash requestFingerprint,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(requestFingerprint.Value, out var bytes))
            return Task.FromResult<CandidateOntologyDocument?>(null);
        return Task.FromResult<CandidateOntologyDocument?>(
            CandidateOntologyDocument.ParseStrict(
                System.Text.Encoding.UTF8.GetString(bytes), allowExternalEndpoints: true));
    }

    public Task PutAsync(
        ContentHash requestFingerprint,
        CandidateOntologyDocument candidate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = candidate.ToCanonicalBytes(allowExternalEndpoints: true);
        _entries.AddOrUpdate(requestFingerprint.Value, bytes, (_, existing) =>
        {
            if (!existing.AsSpan().SequenceEqual(bytes))
                throw new OntologyArtifactValidationException("A cache request key cannot be overwritten with different candidate bytes.");
            return existing;
        });
        return Task.CompletedTask;
    }
}

public sealed class FileCandidateOntologyCache : ICandidateOntologyCache
{
    private readonly string _cacheDirectory;

    public FileCandidateOntologyCache(string cacheDirectory)
    {
        _cacheDirectory = Path.GetFullPath(OntologyCanonical.NormalizeRequired(cacheDirectory, nameof(cacheDirectory)));
    }

    public async Task<CandidateOntologyDocument?> GetAsync(
        ContentHash requestFingerprint,
        CancellationToken cancellationToken = default)
    {
        var path = CachePath(requestFingerprint);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return CandidateOntologyDocument.ParseStrict(json, allowExternalEndpoints: true);
    }

    public async Task PutAsync(
        ContentHash requestFingerprint,
        CandidateOntologyDocument candidate,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_cacheDirectory);
        var path = CachePath(requestFingerprint);
        var bytes = candidate.ToCanonicalBytes(allowExternalEndpoints: true);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(tempPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                var existing = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!existing.AsSpan().SequenceEqual(bytes))
                    throw new OntologyArtifactValidationException(
                        "An immutable candidate cache entry already exists with different bytes.");
            }
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    private string CachePath(ContentHash requestFingerprint)
    {
        OntologyArtifactValidator.RequiredHash(requestFingerprint.Value, "requestFingerprint");
        return Path.Combine(_cacheDirectory, requestFingerprint.Value[ContentHash.Prefix.Length..] + ".candidate.json");
    }
}

public sealed class CachingCandidateOntologyGenerator : ICandidateOntologyGenerator
{
    private readonly ICandidateOntologyGenerator _inner;
    private readonly ICandidateOntologyCache _cache;

    public CachingCandidateOntologyGenerator(
        ICandidateOntologyGenerator inner,
        ICandidateOntologyCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<CandidateOntologyDocument> GenerateAsync(
        OntologyGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cached = await _cache.GetAsync(request.RequestFingerprint, cancellationToken).ConfigureAwait(false);
        if (cached is not null)
            return ValidateForRequest(cached, request, fromCache: true);

        var candidate = request.CachedGenerationResult
                        ?? await _inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false);
        candidate = ValidateForRequest(candidate, request, fromCache: false);
        await _cache.PutAsync(request.RequestFingerprint, candidate, cancellationToken).ConfigureAwait(false);
        return candidate;
    }

    private static CandidateOntologyDocument ValidateForRequest(
        CandidateOntologyDocument candidate,
        OntologyGenerationRequest request,
        bool fromCache)
    {
        try
        {
            var acceptedKeys = request.AcceptedConceptContext
                .Select(concept => OntologyCanonical.Normalize(concept.CanonicalSemanticKey))
                .ToHashSet(StringComparer.Ordinal);
            OntologyArtifactValidator.Validate(candidate, acceptedKeys);
            if (candidate.SchemaVersion != request.SchemaVersion
                || candidate.SourceDocumentFingerprint != request.SourceDocumentFingerprint
                || candidate.ExtractionContractVersion != request.ExtractionContractVersion
                || candidate.GeneratorIdentity != request.GeneratorIdentity)
                throw new OntologyArtifactValidationException("Candidate metadata does not match the complete generation request.");
            return candidate;
        }
        catch (Exception ex) when (fromCache && ex is not OntologyCachedArtifactMismatchException)
        {
            throw new OntologyCachedArtifactMismatchException(request.RequestFingerprint.Value, ex.Message);
        }
    }

    internal static CandidateOntologyDocument ValidateForReplay(
        CandidateOntologyDocument candidate,
        OntologyGenerationRequest request)
        => ValidateForRequest(candidate, request, fromCache: true);
}

public sealed class ReplayCandidateOntologyGenerator : ICandidateOntologyGenerator
{
    private readonly ICandidateOntologyCache _cache;

    public ReplayCandidateOntologyGenerator(ICandidateOntologyCache cache)
        => _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    public async Task<CandidateOntologyDocument> GenerateAsync(
        OntologyGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cached = await _cache.GetAsync(request.RequestFingerprint, cancellationToken).ConfigureAwait(false);
        if (cached is null)
            throw new OntologyGeneratorUnavailableException(request.RequestFingerprint.Value);

        return CachingCandidateOntologyGenerator.ValidateForReplay(cached, request);
    }
}

public sealed class DeterministicSpanCandidateGenerator : ICandidateOntologyGenerator
{
    public const string GeneratorId = "deterministic-document-span-fake/1.0.0";

    public Task<CandidateOntologyDocument> GenerateAsync(
        OntologyGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var concepts = new List<CandidateConcept>();
        foreach (var spanGroup in request.ChangedSpans
                     .Where(span => span.Kind == SpanKind.Body)
                     .GroupBy(span => span.TextFingerprint, StringComparer.Ordinal)
                     .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var spans = spanGroup.OrderBy(span => span.Ordinal).ToArray();
            var text = OntologyCanonical.Normalize(spans[0].Text);
            var semanticKey = "span-" + spans[0].TextFingerprint[ContentHash.Prefix.Length..];
            concepts.Add(new CandidateConcept(
                text,
                text,
                "document-span",
                Array.Empty<string>(),
                spans.Select(span => new SourceEvidence(
                    request.SourceDocumentFingerprint,
                    span.SectionId,
                    span.SpanId,
                    span.StartOffset,
                    span.EndOffset,
                    span.TextFingerprint)).ToArray(),
                semanticKey,
                1.0m));
        }

        return Task.FromResult(new CandidateOntologyDocument(
            CandidateOntologyDocument.CurrentSchemaVersion,
            request.SourceDocumentFingerprint,
            request.ExtractionContractVersion,
            request.GeneratorIdentity,
            concepts,
            Array.Empty<CandidateRelationship>(),
            new[] { "offline-fake-not-semantic-extraction" }));
    }
}