using FluentAssertions;
using System.Text;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class OntologySnapshotDeltaTests
{
    [Fact]
    public void Accepted_concept_content_fingerprint_tracks_definition_not_snapshot_metadata()
    {
        var original = Concept("concept-a", "snapshot-1", "snapshot-1", "Access is reviewed.");
        var laterSnapshot = Concept("concept-a", "snapshot-1", "snapshot-2", "Access is reviewed.");
        var changedDefinition = Concept("concept-a", "snapshot-1", "snapshot-2", "Access is reviewed quarterly.");

        laterSnapshot.ContentFingerprint.Should().Be(original.ContentFingerprint);
        changedDefinition.ContentFingerprint.Should().NotBe(original.ContentFingerprint);
    }

    [Fact]
    public void Snapshot_fingerprint_and_bytes_are_independent_of_array_order()
    {
        var firstConcept = Concept("concept-a", "snapshot-1", "snapshot-1", "First.");
        var secondConcept = Concept("concept-b", "snapshot-1", "snapshot-1", "Second.");
        var firstRelationship = Relationship("relationship-a", "requires", "concept-a", "concept-b");
        var secondRelationship = Relationship("relationship-b", "related-to", "concept-b", "concept-a");

        var first = Snapshot(new[] { firstConcept, secondConcept }, new[] { firstRelationship, secondRelationship });
        var reversed = Snapshot(new[] { secondConcept, firstConcept }, new[] { secondRelationship, firstRelationship });

        first.SnapshotFingerprint.Should().Be(reversed.SnapshotFingerprint);
        first.ToCanonicalBytes().Should().Equal(reversed.ToCanonicalBytes());
    }

    [Fact]
    public void Snapshot_validation_rejects_relationships_to_unknown_concepts()
    {
        var concept = Concept("concept-a", "snapshot-1", "snapshot-1", "First.");
        var relationship = Relationship("relationship-a", "requires", "concept-a", "missing-concept");

        Action act = () => Snapshot(new[] { concept }, new[] { relationship });

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*unknown concept ID*");
    }

    [Fact]
    public void Snapshot_validation_detects_fingerprint_tampering()
    {
        var snapshot = Snapshot(new[] { Concept("concept-a", "snapshot-1", "snapshot-1", "First.") });
        var tampered = snapshot with { SnapshotFingerprint = ContentHash.OfString("tampered").Value };

        Action act = () => tampered.ToCanonicalBytes();

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*Snapshot fingerprint mismatch*");
    }

    [Fact]
    public void Snapshot_strict_parse_replays_identical_canonical_bytes()
    {
        var snapshot = Snapshot(new[] { Concept("concept-a", "snapshot-1", "snapshot-1", "First.") });
        var json = Encoding.UTF8.GetString(snapshot.ToCanonicalBytes());

        var replayed = OntologySnapshot.ParseStrict(json);

        replayed.ToCanonicalBytes().Should().Equal(snapshot.ToCanonicalBytes());
    }

    [Fact]
    public void Snapshot_strict_parse_rejects_duplicate_properties()
    {
        var json = Encoding.UTF8.GetString(
            Snapshot(new[] { Concept("concept-a", "snapshot-1", "snapshot-1", "First.") }).ToCanonicalBytes())
            .Replace("\"schemaVersion\":", "\"schemaVersion\":\"bad\",\"schemaVersion\":", StringComparison.Ordinal);

        Action act = () => OntologySnapshot.ParseStrict(json);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*Duplicate JSON property 'schemaVersion'*");
    }

    [Fact]
    public void Delta_fingerprint_and_bytes_are_independent_of_change_order()
    {
        var firstConcept = Concept("concept-a", "snapshot-2", "snapshot-2", "First.");
        var secondConcept = Concept("concept-b", "snapshot-2", "snapshot-2", "Second.");
        var first = Delta(new[] { firstConcept, secondConcept }, new[] { "retired-a", "retired-b" });
        var reversed = Delta(new[] { secondConcept, firstConcept }, new[] { "retired-b", "retired-a" });

        first.DeltaFingerprint.Should().Be(reversed.DeltaFingerprint);
        first.ToCanonicalBytes().Should().Equal(reversed.ToCanonicalBytes());
    }

    [Fact]
    public void Delta_validation_rejects_a_concept_with_conflicting_changes()
    {
        var concept = Concept("concept-a", "snapshot-2", "snapshot-2", "First.");

        Action act = () => Delta(new[] { concept }, new[] { "concept-a" });

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*appears more than once in the delta*");
    }

    [Fact]
    public void Delta_validation_rejects_add_and_remove_of_the_same_alias()
    {
        var evidence = Evidence(CurrentDocumentFingerprint, "span-alias", 50, 60, "same phrase");
        var changes = new[]
        {
            new AliasChange("concept-a", "same phrase", OntologyDeltaChangeKind.Add, new[] { evidence }),
            new AliasChange("concept-a", "same phrase", OntologyDeltaChangeKind.Remove, new[] { evidence }),
        };

        Action act = () => OntologyDelta.Create(
            "snapshot-1", CurrentDocumentFingerprint,
            Array.Empty<AcceptedConcept>(), Array.Empty<ConceptAugmentation>(), Array.Empty<string>(),
            changes, Array.Empty<RelationshipChange>(), Array.Empty<OntologyUnresolvedItem>());

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*Duplicate alias change*");
    }

    [Fact]
    public void Delta_removals_can_retain_provenance_from_the_parent_document()
    {
        var previousFingerprint = ContentHash.OfString("previous-doc").Value;
        var oldEvidence = Evidence(previousFingerprint, "span-old", 0, 12, "old support");
        var retiredRelationship = OntologyRelationship.Create(
            "relationship-old", "requires", "concept-a", "concept-b",
            new[] { oldEvidence }, OntologyArtifactStatus.Retired);
        var delta = OntologyDelta.Create(
            "snapshot-1", CurrentDocumentFingerprint,
            Array.Empty<AcceptedConcept>(), Array.Empty<ConceptAugmentation>(),
            Array.Empty<string>(),
            new[] { new AliasChange("concept-a", "old phrase", OntologyDeltaChangeKind.Remove, new[] { oldEvidence }) },
            new[] { new RelationshipChange(OntologyDeltaChangeKind.Remove, retiredRelationship) },
            Array.Empty<OntologyUnresolvedItem>());

        OntologyArtifactValidator.ValidateDelta(delta);
        delta.ToCanonicalBytes().Should().NotBeEmpty();
    }

    [Fact]
    public void Delta_validation_detects_fingerprint_tampering()
    {
        var delta = Delta(Array.Empty<AcceptedConcept>(), Array.Empty<string>());
        var tampered = delta with { DeltaFingerprint = ContentHash.OfString("tampered").Value };

        Action act = () => tampered.ToCanonicalBytes();

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*Delta fingerprint mismatch*");
    }

    [Fact]
    public void Delta_strict_parse_replays_identical_canonical_bytes()
    {
        var delta = Delta(Array.Empty<AcceptedConcept>(), Array.Empty<string>());
        var json = Encoding.UTF8.GetString(delta.ToCanonicalBytes());

        var replayed = OntologyDelta.ParseStrict(json);

        replayed.ToCanonicalBytes().Should().Equal(delta.ToCanonicalBytes());
    }

    private static OntologySnapshot Snapshot(
        IReadOnlyList<AcceptedConcept> concepts,
        IReadOnlyList<OntologyRelationship>? relationships = null)
        => OntologySnapshot.Create(
            "ontology-1",
            SpanFixture.FamilyId,
            "snapshot-1",
            CurrentDocumentFingerprint,
            null,
            concepts,
            relationships ?? Array.Empty<OntologyRelationship>(),
            Array.Empty<OntologyUnresolvedItem>(),
            "extract-v1",
            "pipeline-v1");

    private static OntologyDelta Delta(
        IReadOnlyList<AcceptedConcept> added,
        IReadOnlyList<string> retired)
    {
        var evidence = Evidence(CurrentDocumentFingerprint, "span-alias", 50, 60, "new phrase");
        return OntologyDelta.Create(
            "snapshot-1",
            CurrentDocumentFingerprint,
            added,
            Array.Empty<ConceptAugmentation>(),
            retired,
            new[] { new AliasChange("concept-a", "new phrase", OntologyDeltaChangeKind.Add, new[] { evidence }) },
            Array.Empty<RelationshipChange>(),
            Array.Empty<OntologyUnresolvedItem>());
    }

    private static AcceptedConcept Concept(
        string id,
        string createdInSnapshot,
        string lastChangedInSnapshot,
        string description)
        => AcceptedConcept.Create(
            id,
            $"semantic-{id}",
            id,
            description,
            "requirement",
            new[] { $"alias-{id}" },
            OntologyArtifactStatus.Active,
            new[] { Evidence(CurrentDocumentFingerprint, $"span-{id}", 0, 20, description) },
            createdInSnapshot,
            lastChangedInSnapshot);

    private static OntologyRelationship Relationship(
        string id,
        string type,
        string source,
        string target)
        => OntologyRelationship.Create(
            id,
            type,
            source,
            target,
            new[] { Evidence(CurrentDocumentFingerprint, $"span-{id}", 21, 40, id) },
            OntologyArtifactStatus.Active);

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

    private static string CurrentDocumentFingerprint { get; }
        = ContentHash.OfString("current-doc").Value;
}