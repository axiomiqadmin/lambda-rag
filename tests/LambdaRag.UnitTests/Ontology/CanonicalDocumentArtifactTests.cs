using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class CanonicalDocumentArtifactTests
{
    private static readonly DocumentFamilyManifest Family =
        DocumentFamilyManifest.Register("document-artifact-tests", "registrant", "family-1");

    [Fact]
    public void Artifact_roundtrip_reconstructs_identical_span_records()
    {
        var parsed = ParsedDocument();
        var identity = SpanVersionIdentity.Create(Family.FamilyId, "v1", "test-parser", "1.0.0");
        var observations = SpanObservationBuilder.Build(ParsedDocumentSpanAdapter.Adapt(parsed).Spans);
        var records = new DeterministicSpanReconciler().Seed(observations, identity);
        var artifact = CanonicalDocumentArtifact.Create(parsed, identity, records);
        var json = System.Text.Encoding.UTF8.GetString(artifact.ToCanonicalBytes());

        var replayed = CanonicalDocumentArtifact.ParseStrict(json);
        var replayedRecords = replayed.CreateSpanRecords();

        replayed.ToCanonicalBytes().Should().Equal(artifact.ToCanonicalBytes());
        replayedRecords.Select(record => record.InstanceId).Should()
            .Equal(records.Select(record => record.InstanceId));
        replayedRecords.Select(record => record.ContentFingerprint).Should()
            .Equal(records.Select(record => record.ContentFingerprint));
    }

    [Fact]
    public void Artifact_validation_rejects_duplicate_span_instance_ids()
    {
        var parsed = ParsedDocument();
        var identity = SpanVersionIdentity.Create(Family.FamilyId, "v1", "test-parser", "1.0.0");
        var records = new DeterministicSpanReconciler().Seed(
            SpanObservationBuilder.Build(ParsedDocumentSpanAdapter.Adapt(parsed).Spans), identity);
        var invalid = CanonicalDocumentArtifact.Create(parsed, identity, records) with
        {
            SpanInstanceIds = new[] { records[0].InstanceId.Value, records[0].InstanceId.Value },
        };

        Action act = () => invalid.ToCanonicalBytes();

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*duplicate span instance IDs*");
    }

    private static ParsedDocument ParsedDocument()
    {
        var text = "Terms\nSecurity controls apply.";
        var source = new SourceDocument(ContentHash.OfString(text), "test.md", SourceDocumentKind.Markdown,
            text.Length, DateTimeOffset.UnixEpoch);
        return new ParsedDocument(
            source,
            text,
            new[]
            {
                new ContentBlock("h", ContentBlockKind.Heading, "Terms",
                    new SourceSpan(source.Id.Value, 0, 5, null, "/Terms"), 1, "/Terms"),
                new ContentBlock("p", ContentBlockKind.Paragraph, "Security controls apply.",
                    new SourceSpan(source.Id.Value, 6, 24, null, "/Terms"), 0, "/Terms"),
            },
            new Dictionary<string, string> { ["parser_id"] = "test-parser", ["parser_version"] = "1.0.0" });
    }
}