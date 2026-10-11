using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class OntologyCompilationServiceTests
{
    private static readonly DocumentFamilyManifest Family =
        DocumentFamilyManifest.Register("compiler-tests", "registrant", "family-1");

    [Fact]
    public async Task Initial_compilation_uses_fake_generator_and_publishes_grounded_snapshot()
    {
        var parsed = Parsed("Terms", "alpha control", "beta control");
        var generator = new SpanEchoGenerator();
        var compiler = new OntologyCompilationService(generator);

        var result = await Compile(compiler, parsed, "v1", "snapshot-1");

        result.CanPublish.Should().BeTrue();
        result.Snapshot!.Concepts.Should().HaveCount(2);
        result.Snapshot.Concepts.SelectMany(concept => concept.Provenance)
            .Should().OnlyContain(evidence => evidence.DocumentFingerprint == parsed.Source.Id.Value);
        generator.Calls.Should().Be(1);
        generator.LastChangedTexts.Should().BeEquivalentTo("Terms", "alpha control", "beta control");
    }

    [Fact]
    public async Task Exact_rerun_reuses_snapshot_without_a_generator_call()
    {
        var parsed = Parsed("Terms", "alpha control");
        var generator = new SpanEchoGenerator();
        var compiler = new OntologyCompilationService(generator);
        var first = await Compile(compiler, parsed, "v1", "snapshot-1");

        var rerun = await compiler.CompileAsync(
            parsed, Family, "ontology-1", "v1", "snapshot-2", "pipeline-v1", "extract-v1",
            first.Snapshot, first.CurrentSpanRecords);

        rerun.ReusedExistingSnapshot.Should().BeTrue();
        rerun.Snapshot!.ToCanonicalBytes().Should().Equal(first.Snapshot!.ToCanonicalBytes());
        generator.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Exact_rerun_reuses_snapshot_even_if_a_new_version_label_is_supplied()
    {
        var parsed = Parsed("Terms", "alpha control");
        var generator = new SpanEchoGenerator();
        var compiler = new OntologyCompilationService(generator);
        var first = await Compile(compiler, parsed, "v1", "snapshot-1");

        var rerun = await compiler.CompileAsync(
            parsed, Family, "ontology-1", "v2", "snapshot-2", "pipeline-v1", "extract-v1",
            first.Snapshot, first.CurrentSpanRecords);

        rerun.ReusedExistingSnapshot.Should().BeTrue();
        rerun.Snapshot!.SnapshotId.Should().Be("snapshot-1");
        generator.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Local_insertion_sends_only_the_added_span_to_generator()
    {
        var previousParsed = Parsed("Terms", "alpha control", "beta control");
        var generator = new SpanEchoGenerator();
        var compiler = new OntologyCompilationService(generator);
        var previous = await Compile(compiler, previousParsed, "v1", "snapshot-1");

        var currentParsed = Parsed("Terms", "inserted control", "alpha control", "beta control");
        var current = await compiler.CompileAsync(
            currentParsed, Family, "ontology-1", "v2", "snapshot-2", "pipeline-v1", "extract-v1",
            previous.Snapshot, previous.CurrentSpanRecords);

        current.CanPublish.Should().BeTrue();
        generator.Calls.Should().Be(2);
        generator.LastChangedTexts.Should().Equal("inserted control");
        current.Delta!.AddedConcepts.Should().ContainSingle();
        foreach (var priorConcept in previous.Snapshot!.Concepts)
            current.Snapshot!.Concepts.Single(concept => concept.CanonicalSemanticKey == priorConcept.CanonicalSemanticKey)
                .ConceptId.Should().Be(priorConcept.ConceptId);
    }

    [Fact]
    public async Task Unresolved_replacement_blocks_generation_and_snapshot_publication()
    {
        var previousParsed = Parsed("Terms", "old clause");
        var generator = new SpanEchoGenerator();
        var compiler = new OntologyCompilationService(generator);
        var previous = await Compile(compiler, previousParsed, "v1", "snapshot-1");
        var callsBefore = generator.Calls;

        var current = await compiler.CompileAsync(
            Parsed("Terms", "replacement clause"), Family, "ontology-1", "v2", "snapshot-2",
            "pipeline-v1", "extract-v1", previous.Snapshot, previous.CurrentSpanRecords);

        current.CanPublish.Should().BeFalse();
        current.Snapshot.Should().BeNull();
        current.SpanReconciliation!.HasUnresolved.Should().BeTrue();
        generator.Calls.Should().Be(callsBefore);
    }

    private static Task<OntologyCompilationResult> Compile(
        OntologyCompilationService compiler,
        ParsedDocument parsed,
        string version,
        string snapshotId)
        => compiler.CompileAsync(
            parsed, Family, "ontology-1", version, snapshotId, "pipeline-v1", "extract-v1");

    private static ParsedDocument Parsed(string heading, params string[] bodies)
    {
        var blocks = new List<ContentBlock>();
        var canonical = new System.Text.StringBuilder();
        var headingStart = canonical.Length;
        canonical.Append(heading).Append('\n');
        blocks.Add(new ContentBlock(
            "heading", ContentBlockKind.Heading, heading,
            new SourceSpan("pending", headingStart, heading.Length, null, "/"), 1, "/" + heading));

        var bodyIndex = 0;
        foreach (var body in bodies)
        {
            if (bodyIndex > 0) canonical.Append('\n');
            var start = canonical.Length;
            canonical.Append(body);
            blocks.Add(new ContentBlock(
                $"body-{bodyIndex}", ContentBlockKind.Paragraph, body,
                new SourceSpan("pending", start, body.Length, null, "/" + heading), 0, "/" + heading));
            bodyIndex++;
        }

        var text = canonical.ToString();
        var sourceId = ContentHash.OfString(text);
        var source = new SourceDocument(sourceId, "compiler-test.md", SourceDocumentKind.Markdown,
            text.Length, DateTimeOffset.UnixEpoch);
        var fixedBlocks = blocks.Select(block => block with
        {
            Span = block.Span with { DocumentId = sourceId.Value },
        }).ToArray();
        return new ParsedDocument(source, text, fixedBlocks,
            new Dictionary<string, string> { ["parser_id"] = "compiler-test", ["parser_version"] = "1.0.0" });
    }

    private sealed class SpanEchoGenerator : ICandidateOntologyGenerator
    {
        public int Calls { get; private set; }
        public IReadOnlyList<string> LastChangedTexts { get; private set; } = Array.Empty<string>();

        public Task<CandidateOntologyDocument> GenerateAsync(
            OntologyGenerationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastChangedTexts = request.ChangedSpans.Select(span => span.Text).ToArray();
            var concepts = request.ChangedSpans
                .Where(span => span.Kind == SpanKind.Body)
                .Select(span => new CandidateConcept(
                    span.Text,
                    $"Evidence: {span.Text}",
                    "requirement",
                    Array.Empty<string>(),
                    new[]
                    {
                        new SourceEvidence(
                            request.SourceDocumentFingerprint,
                            span.SectionId,
                            span.SpanId,
                            span.StartOffset,
                            span.EndOffset,
                            span.TextFingerprint),
                    },
                    Slug(span.Text),
                    1.0m))
                .ToArray();

            return Task.FromResult(new CandidateOntologyDocument(
                CandidateOntologyDocument.CurrentSchemaVersion,
                request.SourceDocumentFingerprint,
                request.ExtractionContractVersion,
                request.GeneratorIdentity,
                concepts,
                Array.Empty<CandidateRelationship>(),
                Array.Empty<string>()));
        }

        private static string Slug(string text)
        {
            var words = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            return string.Join('-', words);
        }
    }
}