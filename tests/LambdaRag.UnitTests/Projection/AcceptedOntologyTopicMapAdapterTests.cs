using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using LambdaRag.Projection;
using LambdaRag.Projection.Projectors;
using Xunit;

namespace LambdaRag.UnitTests.Projection;

public sealed class AcceptedOntologyTopicMapAdapterTests
{
    [Fact]
    public void Static_mode_returns_the_existing_static_map_unchanged()
    {
        var map = TopicMapRegistry.Load("contract.v1");

        AcceptedOntologyTopicMapAdapter.Create(Snapshot(Concept("security-controls", "Security controls", "cybersecurity")),
            OntologyTopicMode.Static, map).Should().BeSameAs(map);
    }

    [Fact]
    public void Dynamic_mode_uses_only_active_accepted_concepts_and_aliases()
    {
        var snapshot = Snapshot(
            Concept("security-controls", "Security controls", "cybersecurity"),
            Concept("retired", "Retired topic", "old alias", OntologyArtifactStatus.Retired));

        var map = AcceptedOntologyTopicMapAdapter.Create(snapshot, OntologyTopicMode.Dynamic);

        map.Domain.Should().Be(snapshot.OntologyId);
        map.Version.Should().Be(snapshot.SnapshotId);
        map.Topics.Should().ContainSingle();
        map.Topics.Single().Id.Should().Be("security-controls");
        map.Topics.Single().Keywords.Should().Contain("security controls").And.Contain("cybersecurity");
        map.Axes.Should().BeEmpty();
    }

    [Fact]
    public void Hybrid_mode_retains_static_topics_and_adds_dynamic_topics()
    {
        var staticMap = TopicMapRegistry.Load("contract.v1");
        var snapshot = Snapshot(Concept("security-controls", "Security controls", "cybersecurity"));

        var hybrid = AcceptedOntologyTopicMapAdapter.Create(snapshot, OntologyTopicMode.Hybrid, staticMap);

        hybrid.Topics.Select(topic => topic.Id).Should().Contain("security-controls");
        hybrid.Topics.Select(topic => topic.Id).Should().Contain(staticMap.Topics.Select(topic => topic.Id));
        hybrid.Axes.Should().BeSameAs(staticMap.Axes);
    }

    [Fact]
    public void Dynamic_mode_rejects_ambiguous_normalized_aliases()
    {
        var snapshot = Snapshot(
            Concept("concept-a", "Access controls", "shared phrase"),
            Concept("concept-b", "Network controls", "Shared  Phrase"));

        Action act = () => AcceptedOntologyTopicMapAdapter.Create(snapshot, OntologyTopicMode.Dynamic);

        act.Should().Throw<OntologyArtifactValidationException>()
            .WithMessage("*ambiguously maps to multiple concepts*");
    }

    [Fact]
    public async Task Accepted_dynamic_snapshot_drives_projected_topics_without_generator_access()
    {
        var snapshot = Snapshot(Concept("security-controls", "Security controls", "cybersecurity"));
        var map = AcceptedOntologyTopicMapAdapter.Create(snapshot, OntologyTopicMode.Dynamic);
        var projector = new DeterministicContractProjector(map);
        var heading = "Security controls";
        var body = "Cybersecurity controls are required.";
        var sourceHash = ContentHash.OfString(heading + "\n" + body);
        var parsed = new ParsedDocument(
            new SourceDocument(sourceHash, "dynamic.md", SourceDocumentKind.Markdown, heading.Length + body.Length + 1,
                DateTimeOffset.UnixEpoch),
            heading + "\n" + body,
            new[]
            {
                new ContentBlock("h", ContentBlockKind.Heading, heading,
                    new SourceSpan(sourceHash.Value, 0, heading.Length, null, "/Security controls"), 1, "/Security controls"),
                new ContentBlock("p", ContentBlockKind.Paragraph, body,
                    new SourceSpan(sourceHash.Value, heading.Length + 1, body.Length, null, "/Security controls"), 0, "/Security controls"),
            },
            new Dictionary<string, string> { ["parser_id"] = "test-parser", ["parser_version"] = "1.0.0" });

        var projected = await projector.ProjectAsync(parsed);

        projected.Graph["topic_map"]!.GetValue<string>().Should().Be("dynamic-ontology@snapshot-1");
        projected.Graph["sections"]!.AsArray()[0]!.AsObject()["primary_topic"]!.GetValue<string>()
            .Should().Be("security-controls");
    }

    private static OntologySnapshot Snapshot(params AcceptedConcept[] concepts)
    {
        var docHash = ContentHash.OfString("dynamic-topic-map-document").Value;
        var provenance = new SourceEvidence(docHash, "/0", "span-1", 0, 10, ContentHash.OfString("evidence").Value);
        var normalized = concepts.Select(concept => AcceptedConcept.Create(
            concept.ConceptId,
            concept.CanonicalSemanticKey,
            concept.CanonicalLabel,
            concept.Description,
            concept.ConceptType,
            concept.Aliases,
            concept.Status,
            new[] { provenance },
            "snapshot-1",
            "snapshot-1")).ToArray();
        return OntologySnapshot.Create(
            "dynamic-ontology",
            DocumentFamilyManifest.Register("topic-map-tests", "registrant", "family").FamilyId,
            "snapshot-1",
            docHash,
            null,
            normalized,
            Array.Empty<OntologyRelationship>(),
            Array.Empty<OntologyUnresolvedItem>(),
            "extract-v1",
            "pipeline-v1");
    }

    private static AcceptedConcept Concept(
        string key,
        string label,
        string alias,
        OntologyArtifactStatus status = OntologyArtifactStatus.Active)
        => AcceptedConcept.Create(
            "concept-" + key,
            key,
            label,
            "Description for " + label,
            "requirement",
            new[] { alias },
            status,
            new[] { new SourceEvidence(ContentHash.OfString("dynamic-topic-map-document").Value,
                "/0", "span-1", 0, 10, ContentHash.OfString("evidence").Value) },
            "snapshot-1",
            "snapshot-1");
}