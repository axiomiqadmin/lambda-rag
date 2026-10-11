using FluentAssertions;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class CandidateOntologyContractTests
{
    [Fact]
    public void Canonical_bytes_are_stable_across_candidate_array_order()
    {
        var forward = ValidCandidate(reverse: false);
        var reversed = ValidCandidate(reverse: true);

        forward.ToCanonicalBytes().Should().Equal(reversed.ToCanonicalBytes());
    }

    [Fact]
    public void Strict_parse_and_replay_produce_identical_canonical_bytes()
    {
        var original = ValidCandidate();
        var json = OntologyCanonicalJson.Serialize(original.ToCanonicalJson());

        var replayed = CandidateOntologyDocument.ParseStrict(json);

        replayed.ToCanonicalBytes().Should().Equal(original.ToCanonicalBytes());
    }

    [Fact]
    public void Strict_parse_rejects_unsupported_fields()
    {
        var json = OntologyCanonicalJson.Serialize(ValidCandidate().ToCanonicalJson())
            .Replace("\"schemaVersion\":", "\"untrustedField\":true,\"schemaVersion\":", StringComparison.Ordinal);

        Action act = () => CandidateOntologyDocument.ParseStrict(json);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*unsupported [untrustedField]*");
    }

    [Fact]
    public void Strict_parse_rejects_duplicate_json_properties()
    {
        var json = OntologyCanonicalJson.Serialize(ValidCandidate().ToCanonicalJson())
            .Replace("\"schemaVersion\":", "\"schemaVersion\":\"attacker-value\",\"schemaVersion\":", StringComparison.Ordinal);

        Action act = () => CandidateOntologyDocument.ParseStrict(json);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*Duplicate JSON property 'schemaVersion'*");
    }

    [Fact]
    public void Strict_parse_requires_relationship_endpoints_unless_parent_context_supplies_them()
    {
        var complete = ValidCandidate();
        var incremental = complete with { Concepts = new[] { complete.Concepts[0] } };
        var json = OntologyCanonicalJson.Serialize(incremental.ToCanonicalJson(allowExternalEndpoints: true));

        Action standalone = () => CandidateOntologyDocument.ParseStrict(json);
        standalone.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*unknown semantic key*");

        var parsed = CandidateOntologyDocument.ParseStrict(
            json,
            new HashSet<string>(StringComparer.Ordinal) { "availability-commitments" });
        parsed.Relationships.Should().ContainSingle();
    }

    [Fact]
    public void Validation_rejects_duplicate_semantic_keys()
    {
        var candidate = ValidCandidate();
        var duplicate = candidate.Concepts[0] with { ProposedLabel = "renamed" };
        var invalid = candidate with { Concepts = candidate.Concepts.Append(duplicate).ToArray() };

        Action act = () => OntologyArtifactValidator.Validate(invalid);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*Duplicate proposed semantic key*");
    }

    [Fact]
    public void Validation_allows_a_present_but_empty_alias_list()
    {
        var candidate = ValidCandidate();
        var withoutAliases = candidate.Concepts[0] with { Aliases = Array.Empty<string>() };
        var valid = candidate with { Concepts = new[] { withoutAliases, candidate.Concepts[1] } };

        Action act = () => OntologyArtifactValidator.Validate(valid);

        act.Should().NotThrow();
    }

    [Fact]
    public void Validation_rejects_duplicate_evidence_locations_even_with_different_text_hashes()
    {
        var candidate = ValidCandidate();
        var evidence = candidate.Concepts[0].SourceEvidence[0];
        var conflicting = evidence with { ExactTextFingerprint = ContentHash.OfString("different text").Value };
        var concept = candidate.Concepts[0] with { SourceEvidence = new[] { evidence, conflicting } };
        var invalid = candidate with { Concepts = new[] { concept, candidate.Concepts[1] } };

        Action act = () => OntologyArtifactValidator.Validate(invalid);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*duplicate evidence*");
    }

    [Fact]
    public void Validation_rejects_unsupported_relationship_types()
    {
        var candidate = ValidCandidate();
        var invalid = candidate with
        {
            Relationships = new[]
            {
                candidate.Relationships[0] with { ProposedRelationshipType = "similar-to" },
            },
        };

        Action act = () => OntologyArtifactValidator.Validate(invalid);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*Unsupported relationship type*");
    }

    [Fact]
    public void Validation_rejects_relationships_to_unknown_semantic_keys()
    {
        var candidate = ValidCandidate();
        var invalid = candidate with
        {
            Relationships = new[]
            {
                candidate.Relationships[0] with { TargetSemanticKey = "missing-concept" },
            },
        };

        Action act = () => OntologyArtifactValidator.Validate(invalid);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*unknown semantic key*");
    }

    [Fact]
    public void Validation_rejects_evidence_for_a_different_document()
    {
        var candidate = ValidCandidate();
        var invalidEvidence = candidate.Concepts[0].SourceEvidence[0] with
        {
            DocumentFingerprint = ContentHash.OfString("other-document").Value,
        };
        var invalidConcept = candidate.Concepts[0] with
        {
            SourceEvidence = new[] { invalidEvidence },
        };
        var invalid = candidate with
        {
            Concepts = new[] { invalidConcept, candidate.Concepts[1] },
        };

        Action act = () => OntologyArtifactValidator.Validate(invalid);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*different source document*");
    }

    [Fact]
    public void Validation_rejects_invalid_confidence()
    {
        var candidate = ValidCandidate();
        var invalidConcept = candidate.Concepts[0] with { DiagnosticConfidence = 1.01m };
        var invalid = candidate with { Concepts = new[] { invalidConcept, candidate.Concepts[1] } };

        Action act = () => OntologyArtifactValidator.Validate(invalid);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*diagnosticConfidence*");
    }

    [Fact]
    public void Validation_rejects_empty_evidence_ranges()
    {
        var candidate = ValidCandidate();
        var invalidEvidence = candidate.Concepts[0].SourceEvidence[0] with { EndOffset = 0 };
        var invalidConcept = candidate.Concepts[0] with { SourceEvidence = new[] { invalidEvidence } };
        var invalid = candidate with { Concepts = new[] { invalidConcept, candidate.Concepts[1] } };

        Action act = () => OntologyArtifactValidator.Validate(invalid);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*invalid half-open offset range*");
    }

    private static CandidateOntologyDocument ValidCandidate(bool reverse = false)
    {
        var documentFingerprint = ContentHash.OfString("candidate-source").Value;
        var security = new CandidateConcept(
            "Security", "Security controls", "requirement",
            new[] { "security control", "cybersecurity" },
            new[] { Evidence(documentFingerprint, "span-1", 0, 20, "security text") },
            "security-controls", 0.9m);
        var availability = new CandidateConcept(
            "Availability", "Availability commitments", "requirement",
            new[] { "uptime" },
            new[] { Evidence(documentFingerprint, "span-2", 21, 40, "availability text") },
            "availability-commitments", 0.8m);
        var concepts = reverse
            ? new[] { availability, security }
            : new[] { security, availability };
        var relationshipEvidence = Evidence(documentFingerprint, "span-3", 41, 60, "relationship text");

        return new CandidateOntologyDocument(
            CandidateOntologyDocument.CurrentSchemaVersion,
            documentFingerprint,
            "extract-v1",
            "test-generator",
            concepts,
            new[]
            {
                new CandidateRelationship(
                    "requires", "security-controls", "availability-commitments",
                    new[] { relationshipEvidence }),
            },
            new[] { "review-required", "source-grounded" });
    }

    private static SourceEvidence Evidence(
        string documentFingerprint,
        string spanId,
        int start,
        int end,
        string text)
        => new(
            documentFingerprint,
            "section-1",
            spanId,
            start,
            end,
            ContentHash.OfString(text).Value);
}