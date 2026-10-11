using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using Xunit;
using static LambdaRag.UnitTests.Ontology.SpanFixture;

namespace LambdaRag.UnitTests.Ontology;

public sealed class CanonicalSpanEvidenceIndexTests
{
    [Fact]
    public void Evidence_created_from_index_resolves_to_the_exact_span()
    {
        var parsed = ParsedDocumentWithSpans();
        var index = CanonicalSpanEvidenceIndex.Create(parsed, FamilyId, "v1");
        var record = index.Records.Single(span => span.Text == "Security terms.");

        var evidence = index.CreateEvidence(record);
        var resolved = index.Resolve(evidence);

        resolved.Should().Be(record);
        evidence.DocumentFingerprint.Should().Be(parsed.Source.Id.Value);
        evidence.SectionId.Should().Be(record.Observation.AncestryPath);
        evidence.StartOffset.Should().Be(record.Observation.CharStart);
        evidence.EndOffset.Should().Be(record.Observation.CharStart + record.Observation.CharLength);
        evidence.ExactTextFingerprint.Should().Be(record.ContentFingerprint.Value);
    }

    [Fact]
    public void Later_index_carries_ids_for_unchanged_spans()
    {
        var parsed = ParsedDocumentWithSpans();
        var previous = CanonicalSpanEvidenceIndex.Create(parsed, FamilyId, "v1");

        var current = CanonicalSpanEvidenceIndex.Create(parsed, FamilyId, "v2", previous.Records);

        current.Records.Single(span => span.Text == "Security terms.").InstanceId
            .Should().Be(previous.Records.Single(span => span.Text == "Security terms.").InstanceId);
    }

    [Fact]
    public void Resolve_rejects_a_nonexistent_span_id()
    {
        var index = CanonicalSpanEvidenceIndex.Create(ParsedDocumentWithSpans(), FamilyId, "v1");
        var evidence = Evidence(index, "si:nonexistent", "Security terms.");

        Action act = () => index.Resolve(evidence);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*nonexistent span*");
    }

    [Fact]
    public void Resolve_rejects_mismatched_path_offset_or_text_fingerprint()
    {
        var index = CanonicalSpanEvidenceIndex.Create(ParsedDocumentWithSpans(), FamilyId, "v1");
        var valid = index.CreateEvidence(index.Records.Single(span => span.Text == "Security terms."));

        foreach (var invalid in new[]
                 {
                     valid with { SectionId = "/wrong" },
                     valid with { StartOffset = valid.StartOffset + 1 },
                     valid with { ExactTextFingerprint = ContentHash.OfString("wrong text").Value },
                 })
        {
            Action act = () => index.Resolve(invalid);
            act.Should().Throw<OntologyArtifactValidationException>()
                .WithMessage("*does not match its canonical section*");
        }
    }

    [Fact]
    public void Resolve_rejects_evidence_from_another_document()
    {
        var index = CanonicalSpanEvidenceIndex.Create(ParsedDocumentWithSpans(), FamilyId, "v1");
        var valid = index.CreateEvidence(index.Records.Single(span => span.Text == "Security terms."));

        Action act = () => index.Resolve(valid with
        {
            DocumentFingerprint = ContentHash.OfString("other-document").Value,
        });

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*different source document*");
    }

    [Fact]
    public void ValidateCandidate_resolves_every_concept_evidence_reference()
    {
        var parsed = ParsedDocumentWithSpans();
        var index = CanonicalSpanEvidenceIndex.Create(parsed, FamilyId, "v1");
        var evidence = index.CreateEvidence(index.Records.Single(span => span.Text == "Security terms."));
        var candidate = Candidate(parsed.Source.Id.Value, evidence);

        Action valid = () => index.ValidateCandidate(candidate);
        valid.Should().NotThrow();

        var missing = evidence with { SpanId = "si:missing" };
        var invalid = candidate with
        {
            Concepts = new[] { candidate.Concepts[0] with { SourceEvidence = new[] { missing } } },
        };
        Action invalidAct = () => index.ValidateCandidate(invalid);
        invalidAct.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*nonexistent span*");
    }

    private static ParsedDocument ParsedDocumentWithSpans()
    {
        var blocks = new[]
        {
            new ContentBlock("h1", ContentBlockKind.Heading, "Security", new SourceSpan("doc", 0, 8, null, "/Security"), 1, "/Security"),
            new ContentBlock("p1", ContentBlockKind.Paragraph, "Security terms.", new SourceSpan("doc", 9, 15, null, "/Security"), 0, "/Security"),
        };
        return new ParsedDocument(
            new SourceDocument(ContentHash.OfString("canonical-doc"), "test.md", SourceDocumentKind.Markdown, 24, DateTimeOffset.UnixEpoch),
            "Security\nSecurity terms.",
            blocks,
            new Dictionary<string, string> { ["parser_id"] = ParserId, ["parser_version"] = "1.0.0" });
    }

    private static SourceEvidence Evidence(CanonicalSpanEvidenceIndex index, string spanId, string text)
        => new(index.DocumentFingerprint, "/Security", spanId, 9, 24, SpanContentFingerprint.FromText(text).Value);

    private static CandidateOntologyDocument Candidate(string documentFingerprint, SourceEvidence evidence)
        => new(
            CandidateOntologyDocument.CurrentSchemaVersion,
            documentFingerprint,
            "extract-v1",
            "test-generator",
            new[]
            {
                new CandidateConcept("Security", "Security terms", "requirement", Array.Empty<string>(),
                    new[] { evidence }, "security-terms", 0.8m),
            },
            Array.Empty<CandidateRelationship>(),
            Array.Empty<string>());
}