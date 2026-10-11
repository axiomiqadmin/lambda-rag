using FluentAssertions;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class OntologyGenerationTests
{
    [Fact]
    public void Request_fingerprint_is_independent_of_settings_insertion_order()
    {
        var spans = new[] { GenerationSpan(1, "second"), GenerationSpan(0, "first") };
        var firstSettings = new Dictionary<string, string> { ["temperature"] = "0", ["format"] = "json" };
        var secondSettings = new Dictionary<string, string> { ["format"] = "json", ["temperature"] = "0" };

        var first = Request(spans, firstSettings);
        var second = Request(spans.Reverse().ToArray(), secondSettings);

        first.RequestFingerprint.Should().Be(second.RequestFingerprint);
    }

    [Fact]
    public void Request_fingerprint_changes_when_generation_settings_change()
    {
        var first = Request(new[] { GenerationSpan(0, "span") }, new Dictionary<string, string> { ["promptVersion"] = "1" });
        var second = Request(new[] { GenerationSpan(0, "span") }, new Dictionary<string, string> { ["promptVersion"] = "2" });

        first.RequestFingerprint.Should().NotBe(second.RequestFingerprint);
    }

    [Fact]
    public void Request_creation_rejects_text_and_fingerprint_mismatch()
    {
        var invalid = GenerationSpan(0, "span") with { TextFingerprint = ContentHash.OfString("wrong").Value };

        Action act = () => Request(new[] { invalid });

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*does not match its canonical fingerprint*");
    }

    [Fact]
    public async Task Caching_generator_calls_inner_once_for_identical_complete_requests()
    {
        var request = Request(new[] { GenerationSpan(0, "span") });
        var fake = new CountingGenerator();
        var cache = new InMemoryCandidateOntologyCache();
        var generator = new CachingCandidateOntologyGenerator(fake, cache);

        var first = await generator.GenerateAsync(request);
        var second = await generator.GenerateAsync(request);

        fake.Calls.Should().Be(1);
        first.ToCanonicalBytes().Should().Equal(second.ToCanonicalBytes());
    }

    [Fact]
    public async Task Replay_uses_cache_without_a_generator_and_fails_on_cache_miss()
    {
        var request = Request(new[] { GenerationSpan(0, "span") });
        var cache = new InMemoryCandidateOntologyCache();
        var candidate = Candidate(request);
        await cache.PutAsync(request.RequestFingerprint, candidate);
        var replay = new ReplayCandidateOntologyGenerator(cache);

        (await replay.GenerateAsync(request)).ToCanonicalBytes().Should().Equal(candidate.ToCanonicalBytes());

        var missing = Request(new[] { GenerationSpan(0, "different-span") });
        Func<Task> act = () => replay.GenerateAsync(missing);
        await act.Should().ThrowAsync<OntologyGeneratorUnavailableException>();
    }

    [Fact]
    public async Task Cache_hit_with_wrong_request_metadata_fails_closed()
    {
        var request = Request(new[] { GenerationSpan(0, "span") });
        var cache = new InMemoryCandidateOntologyCache();
        var wrong = Candidate(request) with
        {
            SourceDocumentFingerprint = ContentHash.OfString("other-document").Value,
        };
        await cache.PutAsync(request.RequestFingerprint, wrong);
        var generator = new CachingCandidateOntologyGenerator(new CountingGenerator(), cache);

        Func<Task> act = () => generator.GenerateAsync(request);

        await act.Should().ThrowAsync<OntologyCachedArtifactMismatchException>();
    }

    [Fact]
    public async Task File_cache_roundtrips_canonical_candidate_and_refuses_overwrite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lambda-rag-ontology-cache-{Guid.NewGuid():N}");
        var request = Request(new[] { GenerationSpan(0, "span") });
        var candidate = Candidate(request);
        var cache = new FileCandidateOntologyCache(path);

        try
        {
            await cache.PutAsync(request.RequestFingerprint, candidate);
            var loaded = await cache.GetAsync(request.RequestFingerprint);
            loaded.Should().NotBeNull();
            loaded!.ToCanonicalBytes().Should().Equal(candidate.ToCanonicalBytes());
            await cache.PutAsync(request.RequestFingerprint, candidate);

            var different = candidate with { GeneratorIdentity = "different-generator" };
            Func<Task> act = () => cache.PutAsync(request.RequestFingerprint, different);
            await act.Should().ThrowAsync<OntologyArtifactValidationException>();
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    private static OntologyGenerationRequest Request(
        IReadOnlyList<OntologyGenerationSpan> spans,
        IReadOnlyDictionary<string, string>? settings = null)
        => OntologyGenerationRequest.Create(
            ContentHash.OfString("generation-document").Value,
            spans,
            Array.Empty<AcceptedConcept>(),
            "extract-v1",
            "fake-generator-v1",
            settings);

    private static OntologyGenerationSpan GenerationSpan(int ordinal, string text)
        => new(
            ordinal,
            $"si:{ordinal}",
            "/0",
            SpanKind.Body,
            0,
            text,
            SpanContentFingerprint.FromText(text).Value,
            ordinal * 20,
            ordinal * 20 + text.Length,
            null);

    private static CandidateOntologyDocument Candidate(OntologyGenerationRequest request)
        => new(
            CandidateOntologyDocument.CurrentSchemaVersion,
            request.SourceDocumentFingerprint,
            request.ExtractionContractVersion,
            request.GeneratorIdentity,
            Array.Empty<CandidateConcept>(),
            Array.Empty<CandidateRelationship>(),
            Array.Empty<string>());

    private sealed class CountingGenerator : ICandidateOntologyGenerator
    {
        public int Calls { get; private set; }

        public Task<CandidateOntologyDocument> GenerateAsync(
            OntologyGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Candidate(request));
        }
    }
}