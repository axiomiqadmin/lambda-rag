using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class FileOntologyArtifactStoreTests
{
    private static readonly DocumentFamilyManifest Family =
        DocumentFamilyManifest.Register("artifact-store-tests", "registrant", "family-1");

    [Fact]
    public void Family_manifest_strict_roundtrip_recomputes_its_identity()
    {
        var json = System.Text.Encoding.UTF8.GetString(Family.ToCanonicalBytes());

        var replayed = DocumentFamilyManifest.ParseStrict(json);

        replayed.Should().Be(Family);
        var tampered = json.Replace("\"salt\":\"family-1\"", "\"salt\":\"other\"", StringComparison.Ordinal);
        Action act = () => DocumentFamilyManifest.ParseStrict(tampered);
        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*fingerprint does not match its inputs*");
    }

    [Fact]
    public void Artifact_directory_paths_remain_within_windows_path_budget()
    {
        var store = new FileOntologyArtifactStore(Path.Combine(Path.GetTempPath(), "lambda-rag", "artifacts", "sample-run"));
        var paths = store.GetPaths(
            Family.FamilyId,
            ContentHash.OfString("doc").Value,
            "snapshot-cloud-service-architecture-v1");

        paths.VersionDirectory.Length.Should().BeLessThan(240);
    }

    [Fact]
    public async Task Save_and_load_roundtrips_snapshot_candidate_delta_and_span_records()
    {
        var root = TemporaryDirectory();
        try
        {
            var fixture = BuildCompilation();
            var store = new FileOntologyArtifactStore(root);
            var paths = await store.SaveCompilationAsync(
                Family, fixture.Parsed, fixture.Identity, "snapshot-1", fixture.Compilation);
            await store.SaveFamilyManifestAsync(Family);

            (await store.LoadFamilyManifestAsync(Family.FamilyId)).Should().Be(Family);
            (await store.FindSnapshotByIdAsync(Family.FamilyId, "snapshot-1"))!
                .ToCanonicalBytes().Should().Equal(fixture.Snapshot.ToCanonicalBytes());

            (await store.LoadSnapshotAsync(Family.FamilyId, fixture.Parsed.Source.Id.Value, "snapshot-1"))!
                .ToCanonicalBytes().Should().Equal(fixture.Snapshot.ToCanonicalBytes());
            (await store.LoadCandidateAsync(Family.FamilyId, fixture.Parsed.Source.Id.Value, "snapshot-1"))!
                .ToCanonicalBytes().Should().Equal(fixture.Candidate.ToCanonicalBytes());
            (await store.LoadDeltaAsync(Family.FamilyId, fixture.Parsed.Source.Id.Value, "snapshot-1"))!
                .ToCanonicalBytes().Should().Equal(fixture.Delta.ToCanonicalBytes());
            (await store.LoadSpanRecordsAsync(Family.FamilyId, fixture.Parsed.Source.Id.Value, "snapshot-1"))!
                .Select(record => record.InstanceId).Should()
                .Equal(fixture.Records.Select(record => record.InstanceId));
            File.Exists(paths.CanonicalDocumentPath).Should().BeTrue();
            File.Exists(paths.AcceptedOntologyPath).Should().BeTrue();
            File.Exists(paths.ExtractedOntologyPath).Should().BeTrue();
            File.Exists(paths.ExtractedEntitiesPath).Should().BeTrue();
            File.Exists(paths.ExtractedRelationshipsPath).Should().BeTrue();
            File.Exists(paths.ConfidenceScoresPath).Should().BeTrue();
            File.Exists(paths.ValidationReportPath).Should().BeTrue();
            File.Exists(paths.ExecutionLogPath).Should().BeTrue();
            OntologyAuditArtifact.ParseStrict(await File.ReadAllTextAsync(paths.AuditPath))
                .Status.Should().Be("accepted");

            await store.SaveCompilationAsync(Family, fixture.Parsed, fixture.Identity, "snapshot-1", fixture.Compilation);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Conflicting_bytes_cannot_overwrite_an_accepted_snapshot_or_audit()
    {
        var root = TemporaryDirectory();
        try
        {
            var fixture = BuildCompilation();
            var store = new FileOntologyArtifactStore(root);
            await store.SaveCompilationAsync(Family, fixture.Parsed, fixture.Identity, "snapshot-1",
                fixture.Compilation, OntologyAuditArtifact.FromCompilation(Family, fixture.Parsed, fixture.Compilation)
                    with { Diagnostics = new[] { "first" } });

            Func<Task> act = () => store.SaveCompilationAsync(Family, fixture.Parsed, fixture.Identity, "snapshot-1",
                fixture.Compilation, OntologyAuditArtifact.FromCompilation(Family, fixture.Parsed, fixture.Compilation)
                    with { Diagnostics = new[] { "second" } });

            await act.Should().ThrowAsync<OntologyArtifactValidationException>()
                .WithMessage("*Immutable artifact bundle already exists with different bytes*");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Blocked_compilation_never_writes_an_accepted_snapshot()
    {
        var root = TemporaryDirectory();
        try
        {
            var fixture = BuildCompilation();
            var blocked = fixture.Compilation with { Snapshot = null };
            var store = new FileOntologyArtifactStore(root);
            var paths = await store.SaveCompilationAsync(
                Family, fixture.Parsed, fixture.Identity, "blocked-snapshot", blocked);

            File.Exists(paths.AcceptedOntologyPath).Should().BeFalse();
            File.Exists(paths.CandidateOntologyPath).Should().BeTrue();
            File.Exists(paths.OntologyDeltaPath).Should().BeTrue();
            File.Exists(paths.AuditPath).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_audit_leaves_no_partially_published_version_directory()
    {
        var root = TemporaryDirectory();
        try
        {
            var fixture = BuildCompilation();
            var invalidAudit = OntologyAuditArtifact.FromCompilation(Family, fixture.Parsed, fixture.Compilation) with
            {
                DocumentFingerprint = ContentHash.OfString("different-document").Value,
            };
            var store = new FileOntologyArtifactStore(root);
            var paths = store.GetPaths(Family.FamilyId, fixture.Parsed.Source.Id.Value, fixture.Snapshot.SnapshotId);

            Func<Task> act = () => store.SaveCompilationAsync(
                Family, fixture.Parsed, fixture.Identity, "snapshot-1", fixture.Compilation, invalidAudit);

            await act.Should().ThrowAsync<OntologyArtifactValidationException>();
            Directory.Exists(paths.VersionDirectory).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static (ParsedDocument Parsed, SpanVersionIdentity Identity,
        IReadOnlyList<SpanVersionRecord> Records, AcceptedConcept Concept,
        OntologySnapshot Snapshot, CandidateOntologyDocument Candidate, OntologyDelta Delta,
        OntologyCompilationResult Compilation) BuildCompilation()
    {
        const string heading = "Terms";
        const string body = "Security controls apply.";
        var canonical = heading + "\n" + body;
        var source = new SourceDocument(ContentHash.OfString(canonical), "test.md", SourceDocumentKind.Markdown,
            canonical.Length, DateTimeOffset.UnixEpoch);
        var parsed = new ParsedDocument(
            source,
            canonical,
            new[]
            {
                new ContentBlock("h", ContentBlockKind.Heading, heading,
                    new SourceSpan(source.Id.Value, 0, heading.Length, null, "/Terms"), 1, "/Terms"),
                new ContentBlock("p", ContentBlockKind.Paragraph, body,
                    new SourceSpan(source.Id.Value, heading.Length + 1, body.Length, null, "/Terms"), 0, "/Terms"),
            },
            new Dictionary<string, string> { ["parser_id"] = "artifact-test", ["parser_version"] = "1.0.0" });
        var identity = SpanVersionIdentity.Create(Family.FamilyId, "v1", "artifact-test", "1.0.0");
        var records = new DeterministicSpanReconciler().Seed(
            SpanObservationBuilder.Build(ParsedDocumentSpanAdapter.Adapt(parsed).Spans), identity);
        var index = CanonicalSpanEvidenceIndex.Create(parsed, Family.FamilyId, "v1");
        var evidence = index.CreateEvidence(index.Records.Single(record => record.Kind == SpanKind.Body));
        var concept = AcceptedConcept.Create(
            "concept-security", "security-controls", "Security controls", "Controls apply.", "requirement",
            Array.Empty<string>(), OntologyArtifactStatus.Active, new[] { evidence }, "snapshot-1", "snapshot-1");
        var snapshot = OntologySnapshot.Create(
            "ontology-1", Family.FamilyId, "snapshot-1", source.Id.Value, null,
            new[] { concept }, Array.Empty<OntologyRelationship>(), Array.Empty<OntologyUnresolvedItem>(),
            "extract-v1", "pipeline-v1");
        var candidate = new CandidateOntologyDocument(
            CandidateOntologyDocument.CurrentSchemaVersion, source.Id.Value, "extract-v1", "fake-v1",
            Array.Empty<CandidateConcept>(), Array.Empty<CandidateRelationship>(), Array.Empty<string>());
        var delta = OntologyDelta.Create(
            "none", source.Id.Value, new[] { concept }, Array.Empty<ConceptAugmentation>(), Array.Empty<string>(),
            Array.Empty<AliasChange>(), Array.Empty<RelationshipChange>(), Array.Empty<OntologyUnresolvedItem>());
        var compilation = new OntologyCompilationResult(snapshot, candidate, delta, records, null, false);
        return (parsed, identity, records, concept, snapshot, candidate, delta, compilation);
    }

    private static string TemporaryDirectory()
        => Path.Combine(Path.GetTempPath(), $"lambda-rag-artifacts-{Guid.NewGuid():N}");
}