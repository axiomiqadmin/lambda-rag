using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class OntologyConceptReconcilerTests
{
    private static readonly DocumentFamilyManifest Family =
        DocumentFamilyManifest.Register("ontology-tests", "registrant", "family-1");

    private readonly OntologyConceptReconciler _reconciler = new();

    [Fact]
    public void New_concept_id_is_deterministic_but_scoped_to_document_family()
    {
        var input = Parsed("Security controls apply.");
        var index = CanonicalSpanEvidenceIndex.Create(input, Family.FamilyId, "v1");
        var candidate = Candidate(index, "security-controls", "Security controls", "Controls apply.");
        var first = Reconcile(candidate, index, Family, "snapshot-1");
        var replay = Reconcile(candidate, index, Family, "snapshot-1");

        first.Snapshot.Should().NotBeNull();
        first.Snapshot!.Concepts.Single().ConceptId.Should().Be(replay.Snapshot!.Concepts.Single().ConceptId);

        var otherFamily = DocumentFamilyManifest.Register("ontology-tests", "registrant", "family-2");
        var otherIndex = CanonicalSpanEvidenceIndex.Create(input, otherFamily.FamilyId, "v1");
        var other = Reconcile(Candidate(otherIndex, "security-controls", "Security controls", "Controls apply."),
            otherIndex, otherFamily, "snapshot-1");
        other.Snapshot!.Concepts.Single().ConceptId.Should().NotBe(first.Snapshot.Concepts.Single().ConceptId);
    }

    [Fact]
    public void Exact_semantic_key_carries_existing_concept_id_and_content()
    {
        var input = Parsed("Security controls apply.");
        var firstIndex = CanonicalSpanEvidenceIndex.Create(input, Family.FamilyId, "v1");
        var first = Reconcile(Candidate(firstIndex, "security-controls", "Security controls", "Controls apply."),
            firstIndex, Family, "snapshot-1");
        var existing = first.Snapshot!.Concepts.Single();
        var nextIndex = CanonicalSpanEvidenceIndex.Create(input, Family.FamilyId, "v2", firstIndex.Records);

        var next = Reconcile(Candidate(nextIndex, "security-controls", "Security controls", "Controls apply."),
            nextIndex, Family, "snapshot-2", first.Snapshot);

        next.Snapshot.Should().NotBeNull();
        next.Snapshot!.Concepts.Single().ConceptId.Should().Be(existing.ConceptId);
        next.Snapshot.Concepts.Single().ContentFingerprint.Should().Be(existing.ContentFingerprint);
        next.Decisions.Single().Resolution.Should().Be(CandidateConceptResolutionKind.ExactExistingConcept);
        next.Delta.AddedConcepts.Should().BeEmpty();
    }

    [Fact]
    public void New_evidence_augments_description_without_changing_concept_id()
    {
        var previousInput = Parsed("Security controls apply.");
        var previousIndex = CanonicalSpanEvidenceIndex.Create(previousInput, Family.FamilyId, "v1");
        var previous = Reconcile(Candidate(previousIndex, "security-controls", "Security controls", "Controls apply."),
            previousIndex, Family, "snapshot-1").Snapshot!;

        var currentInput = Parsed("Security controls apply quarterly.");
        var currentIndex = CanonicalSpanEvidenceIndex.Create(currentInput, Family.FamilyId, "v2", previousIndex.Records);
        var candidate = Candidate(currentIndex, "security-controls", "Security controls", "Controls apply quarterly.");
        var result = Reconcile(candidate, currentIndex, Family, "snapshot-2", previous);

        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.Concepts.Single().ConceptId.Should().Be(previous.Concepts.Single().ConceptId);
        result.Snapshot.Concepts.Single().Description.Should().Be("Controls apply quarterly.");
        result.Delta.AugmentedConcepts.Should().ContainSingle();
        result.Decisions.Single().Resolution.Should().Be(CandidateConceptResolutionKind.ExistingConceptAugmentation);
    }

    [Fact]
    public void Exact_normalized_alias_retains_id_and_records_new_alias()
    {
        var input = Parsed("Security framework applies.");
        var firstIndex = CanonicalSpanEvidenceIndex.Create(input, Family.FamilyId, "v1");
        var firstCandidate = Candidate(firstIndex, "security-framework", "Security framework", "Framework applies.",
            aliases: new[] { "security program" });
        var previous = Reconcile(firstCandidate, firstIndex, Family, "snapshot-1").Snapshot!;
        var existing = previous.Concepts.Single();
        var nextIndex = CanonicalSpanEvidenceIndex.Create(input, Family.FamilyId, "v2", firstIndex.Records);
        var aliasCandidate = Candidate(nextIndex, "cyber-framework", "SECURITY   PROGRAM", "Framework applies.",
            aliases: new[] { "cyber framework" });

        var result = Reconcile(aliasCandidate, nextIndex, Family, "snapshot-2", previous);

        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.Concepts.Single().ConceptId.Should().Be(existing.ConceptId);
        result.Snapshot.Concepts.Single().Aliases.Should().Contain("cyber framework");
        result.Delta.AliasChanges.Should().ContainSingle(change => change.Alias == "cyber framework");
        result.Decisions.Single().Resolution.Should().Be(CandidateConceptResolutionKind.ExistingConceptAlias);
    }

    [Fact]
    public void Ambiguous_alias_match_fails_closed_without_snapshot()
    {
        var input = Parsed("Security applies.");
        var index = CanonicalSpanEvidenceIndex.Create(input, Family.FamilyId, "v1");
        var first = Reconcile(Candidate(index, "access-security", "Access controls", "Security applies.", aliases: new[] { "security" }),
            index, Family, "snapshot-1").Snapshot!;
        var second = AcceptedConcept.Create(
            "concept-network", "network-security", "Network controls", "Security applies.", "requirement",
            new[] { "security" }, OntologyArtifactStatus.Active,
            new[] { index.CreateEvidence(index.Records.Single(record => record.Text == "Security applies.")) },
            "snapshot-1", "snapshot-1");
        var previous = OntologySnapshot.Create(
            "ontology-1", Family.FamilyId, "snapshot-1", index.DocumentFingerprint, null,
            first.Concepts.Append(second).ToArray(), Array.Empty<OntologyRelationship>(),
            Array.Empty<OntologyUnresolvedItem>(), "extract-v1", "pipeline-v1");
        var candidate = Candidate(index, "security-topic", "Security", "Security applies.");

        var result = Reconcile(candidate, index, Family, "snapshot-2", previous);

        result.Snapshot.Should().BeNull();
        result.Delta.UnresolvedItems.Should().ContainSingle();
        result.Decisions.Single().Resolution.Should().Be(CandidateConceptResolutionKind.Ambiguous);
    }

    [Fact]
    public void Same_semantic_key_with_different_type_fails_closed()
    {
        var input = Parsed("Security controls apply.");
        var index = CanonicalSpanEvidenceIndex.Create(input, Family.FamilyId, "v1");
        var previous = Reconcile(Candidate(index, "security-controls", "Security controls", "Controls apply."),
            index, Family, "snapshot-1").Snapshot!;
        var wrongType = Candidate(index, "security-controls", "Security controls", "Controls apply.", type: "actor");

        var result = Reconcile(wrongType, index, Family, "snapshot-2", previous);

        result.Snapshot.Should().BeNull();
        result.Decisions.Single().Resolution.Should().Be(CandidateConceptResolutionKind.PotentialConflict);
    }

    [Fact]
    public void Unsupported_description_change_without_new_evidence_fails_closed()
    {
        var input = Parsed("Security controls apply.");
        var index = CanonicalSpanEvidenceIndex.Create(input, Family.FamilyId, "v1");
        var previous = Reconcile(Candidate(index, "security-controls", "Security controls", "Controls apply."),
            index, Family, "snapshot-1").Snapshot!;
        var candidate = Candidate(index, "security-controls", "Security controls", "New unsupported meaning.");

        var result = Reconcile(candidate, index, Family, "snapshot-2", previous);

        result.Snapshot.Should().BeNull();
        result.Decisions.Single().Resolution.Should().Be(CandidateConceptResolutionKind.PotentialConflict);
    }

    [Fact]
    public void Explicit_mapping_is_applied_only_after_exact_matching_stages()
    {
        var previousInput = Parsed("Security controls apply.");
        var previousIndex = CanonicalSpanEvidenceIndex.Create(previousInput, Family.FamilyId, "v1");
        var previous = Reconcile(Candidate(previousIndex, "security-controls", "Security controls", "Controls apply."),
            previousIndex, Family, "snapshot-1").Snapshot!;
        var currentInput = Parsed("Updated security controls apply.");
        var currentIndex = CanonicalSpanEvidenceIndex.Create(currentInput, Family.FamilyId, "v2", previousIndex.Records);
        var candidate = Candidate(currentIndex, "security-controls-v2", "Security controls updated", "Controls apply.");
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["security-controls-v2"] = previous.Concepts.Single().ConceptId,
        };

        var result = Reconcile(candidate, currentIndex, Family, "snapshot-2", previous, mapping);

        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.Concepts.Single().ConceptId.Should().Be(previous.Concepts.Single().ConceptId);
        result.Decisions.Single().ReasonCode.Should().Be("explicit-deterministic-mapping");
    }

    [Fact]
    public void Concept_retires_only_when_no_prior_supporting_span_survives()
    {
        var previousInput = Parsed("Security controls apply.");
        var previousIndex = CanonicalSpanEvidenceIndex.Create(previousInput, Family.FamilyId, "v1");
        var previousCandidate = CandidateForRecords(previousIndex, "security-controls", "Security controls", "Controls apply.",
            previousIndex.Records.Where(record => record.Kind == SpanKind.Heading || record.Text == "Security controls apply."));
        var previous = Reconcile(previousCandidate, previousIndex, Family, "snapshot-1").Snapshot!;

        var currentInput = Parsed("Different clause.");
        var currentIndex = CanonicalSpanEvidenceIndex.Create(currentInput, Family.FamilyId, "v2", previousIndex.Records);
        var noCandidates = Candidate(currentIndex, Array.Empty<CandidateConcept>());
        var result = Reconcile(noCandidates, currentIndex, Family, "snapshot-2", previous);

        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.Concepts.Single(concept => concept.ConceptId == previous.Concepts.Single().ConceptId)
            .Status.Should().Be(OntologyArtifactStatus.Active, "the heading support is unchanged and its span ID is carried");

        var removedInput = Parsed("Different clause.", heading: "Other heading");
        var removedIndex = CanonicalSpanEvidenceIndex.Create(removedInput, Family.FamilyId, "v2", previousIndex.Records);
        var removed = Reconcile(Candidate(removedIndex, Array.Empty<CandidateConcept>()), removedIndex,
            Family, "snapshot-3", previous);
        removed.Snapshot!.Concepts.Single(concept => concept.ConceptId == previous.Concepts.Single().ConceptId)
            .Status.Should().Be(OntologyArtifactStatus.Retired);
    }

    [Fact]
    public void Candidate_relationship_resolves_endpoints_and_gets_a_deterministic_id()
    {
        var input = Parsed("Security controls require monitoring.");
        var index = CanonicalSpanEvidenceIndex.Create(input, Family.FamilyId, "v1");
        var evidence = index.CreateEvidence(index.Records.Single(record => record.Kind == SpanKind.Body));
        var concepts = new[]
        {
            new CandidateConcept("Security controls", "Controls exist.", "requirement", Array.Empty<string>(),
                new[] { evidence }, "security-controls", 0.9m),
            new CandidateConcept("Monitoring", "Monitoring exists.", "requirement", Array.Empty<string>(),
                new[] { evidence }, "monitoring", 0.9m),
        };
        var relationships = new[]
        {
            new CandidateRelationship("requires", "security-controls", "monitoring", new[] { evidence }),
        };
        var candidate = Candidate(index, concepts, relationships);

        var first = Reconcile(candidate, index, Family, "snapshot-1");
        var replay = Reconcile(candidate, index, Family, "snapshot-1");

        first.Snapshot.Should().NotBeNull();
        first.Snapshot!.Relationships.Should().ContainSingle();
        first.Snapshot.Relationships.Single().Status.Should().Be(OntologyArtifactStatus.Active);
        first.Snapshot.Relationships.Single().RelationshipId
            .Should().Be(replay.Snapshot!.Relationships.Single().RelationshipId);
        first.Delta.RelationshipChanges.Should().ContainSingle(change => change.ChangeKind == OntologyDeltaChangeKind.Add);
    }

    [Fact]
    public void Existing_relationship_adds_new_evidence_without_changing_relationship_id()
    {
        var previousInput = Parsed("Security controls require monitoring.");
        var previousIndex = CanonicalSpanEvidenceIndex.Create(previousInput, Family.FamilyId, "v1");
        var previousCandidate = TwoConceptsAndRelation(previousIndex);
        var previous = Reconcile(previousCandidate, previousIndex, Family, "snapshot-1").Snapshot!;
        var previousRelationshipId = previous.Relationships.Single().RelationshipId;

        var currentInput = Parsed("Updated security controls require monitoring.");
        var currentIndex = CanonicalSpanEvidenceIndex.Create(currentInput, Family.FamilyId, "v2", previousIndex.Records);
        var currentCandidate = TwoConceptsAndRelation(currentIndex);
        var result = Reconcile(currentCandidate, currentIndex, Family, "snapshot-2", previous);

        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.Relationships.Single().RelationshipId.Should().Be(previousRelationshipId);
        result.Delta.RelationshipChanges.Should().ContainSingle(change => change.ChangeKind == OntologyDeltaChangeKind.Augment);
    }

    [Fact]
    public void Incremental_relationship_can_reference_an_unchanged_parent_concept()
    {
        var previousInput = ParsedSections(("Terms", "Security controls apply."));
        var previousIndex = CanonicalSpanEvidenceIndex.Create(previousInput, Family.FamilyId, "v1");
        var previousCandidate = Candidate(previousIndex, "security-controls", "Security controls", "Controls apply.");
        var previous = Reconcile(previousCandidate, previousIndex, Family, "snapshot-1").Snapshot!;

        var currentInput = ParsedSections(
            ("Terms", "Security controls apply."),
            ("Audit", "Audit retention is required."));
        var currentIndex = CanonicalSpanEvidenceIndex.Create(currentInput, Family.FamilyId, "v2", previousIndex.Records);
        var addedSpan = currentIndex.Records.Single(record => record.Text == "Audit retention is required.");
        var evidence = currentIndex.CreateEvidence(addedSpan);
        var candidate = Candidate(currentIndex,
            new[]
            {
                new CandidateConcept("Audit retention", "Audit retention is required.", "requirement",
                    Array.Empty<string>(), new[] { evidence }, "audit-retention", 0.9m),
            },
            new[]
            {
                new CandidateRelationship("requires", "audit-retention", "security-controls", new[] { evidence }),
            });

        var result = Reconcile(candidate, currentIndex, Family, "snapshot-2", previous);

        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.Relationships.Should().ContainSingle();
        result.Snapshot.Relationships.Single().TargetConceptId
            .Should().Be(previous.Concepts.Single().ConceptId);
        result.Delta.RelationshipChanges.Should().ContainSingle(change => change.ChangeKind == OntologyDeltaChangeKind.Add);
    }

    [Fact]
    public void Relationship_retires_when_its_supporting_span_is_removed_but_endpoints_remain_supported()
    {
        var previousInput = Parsed("Security controls require monitoring.");
        var previousIndex = CanonicalSpanEvidenceIndex.Create(previousInput, Family.FamilyId, "v1");
        var headingEvidence = previousIndex.CreateEvidence(previousIndex.Records.Single(record => record.Kind == SpanKind.Heading));
        var bodyEvidence = previousIndex.CreateEvidence(previousIndex.Records.Single(record => record.Kind == SpanKind.Body));
        var previousCandidate = Candidate(index: previousIndex,
            concepts: new[]
            {
                new CandidateConcept("Security controls", "Controls exist.", "requirement", Array.Empty<string>(),
                    new[] { headingEvidence }, "security-controls", 0.9m),
                new CandidateConcept("Monitoring", "Monitoring exists.", "requirement", Array.Empty<string>(),
                    new[] { headingEvidence }, "monitoring", 0.9m),
            },
            relationships: new[]
            {
                new CandidateRelationship("requires", "security-controls", "monitoring", new[] { bodyEvidence }),
            });
        var previous = Reconcile(previousCandidate, previousIndex, Family, "snapshot-1").Snapshot!;

        var currentInput = Parsed("Different clause.");
        var currentIndex = CanonicalSpanEvidenceIndex.Create(currentInput, Family.FamilyId, "v2", previousIndex.Records);
        var currentHeadingEvidence = currentIndex.CreateEvidence(currentIndex.Records.Single(record => record.Kind == SpanKind.Heading));
        var noRelationshipCandidate = Candidate(currentIndex, new[]
        {
            new CandidateConcept("Security controls", "Controls exist.", "requirement", Array.Empty<string>(),
                new[] { currentHeadingEvidence }, "security-controls", 0.9m),
            new CandidateConcept("Monitoring", "Monitoring exists.", "requirement", Array.Empty<string>(),
                new[] { currentHeadingEvidence }, "monitoring", 0.9m),
        }, Array.Empty<CandidateRelationship>());

        var result = Reconcile(noRelationshipCandidate, currentIndex, Family, "snapshot-2", previous);

        result.Snapshot.Should().NotBeNull();
        result.Snapshot!.Relationships.Single().Status.Should().Be(OntologyArtifactStatus.Retired);
        result.Delta.RelationshipChanges.Should().ContainSingle(change => change.ChangeKind == OntologyDeltaChangeKind.Remove);
    }

    private OntologyConceptReconciliationResult Reconcile(
        CandidateOntologyDocument candidate,
        CanonicalSpanEvidenceIndex index,
        DocumentFamilyManifest family,
        string snapshotId,
        OntologySnapshot? previous = null,
        IReadOnlyDictionary<string, string>? mappings = null)
        => _reconciler.Reconcile(
            candidate, index, family, "ontology-1", snapshotId, "pipeline-v1", previous, mappings);

    private static CandidateOntologyDocument Candidate(
        CanonicalSpanEvidenceIndex index,
        string semanticKey,
        string label,
        string description,
        IReadOnlyList<string>? aliases = null,
        string type = "requirement")
    {
        var records = index.Records.Where(record => record.Kind == SpanKind.Body).ToArray();
        return CandidateForRecords(index, semanticKey, label, description, records, aliases, type);
    }

    private static CandidateOntologyDocument CandidateForRecords(
        CanonicalSpanEvidenceIndex index,
        string semanticKey,
        string label,
        string description,
        IEnumerable<SpanVersionRecord> records,
        IReadOnlyList<string>? aliases = null,
        string type = "requirement")
        => Candidate(index, new[]
        {
            new CandidateConcept(label, description, type, aliases ?? Array.Empty<string>(),
                records.Select(index.CreateEvidence).ToArray(), semanticKey, 0.9m),
        });

    private static CandidateOntologyDocument Candidate(
        CanonicalSpanEvidenceIndex index,
        IReadOnlyList<CandidateConcept> concepts)
        => Candidate(index, concepts, Array.Empty<CandidateRelationship>());

    private static CandidateOntologyDocument Candidate(
        CanonicalSpanEvidenceIndex index,
        IReadOnlyList<CandidateConcept> concepts,
        IReadOnlyList<CandidateRelationship> relationships)
        => new(
            CandidateOntologyDocument.CurrentSchemaVersion,
            index.DocumentFingerprint,
            "extract-v1",
            "test-generator",
            concepts,
            relationships,
            Array.Empty<string>());

    private static CandidateOntologyDocument TwoConceptsAndRelation(CanonicalSpanEvidenceIndex index)
    {
        var evidence = index.CreateEvidence(index.Records.Single(record => record.Kind == SpanKind.Body));
        return Candidate(index,
            new[]
            {
                new CandidateConcept("Security controls", "Controls exist.", "requirement", Array.Empty<string>(),
                    new[] { evidence }, "security-controls", 0.9m),
                new CandidateConcept("Monitoring", "Monitoring exists.", "requirement", Array.Empty<string>(),
                    new[] { evidence }, "monitoring", 0.9m),
            },
            new[] { new CandidateRelationship("requires", "security-controls", "monitoring", new[] { evidence }) });
    }

    private static ParsedDocument Parsed(string body, string heading = "Security")
    {
        var canonical = heading + "\n" + body;
        var sourceId = ContentHash.OfString(canonical);
        return new ParsedDocument(
            new SourceDocument(sourceId, "test.md", SourceDocumentKind.Markdown, canonical.Length, DateTimeOffset.UnixEpoch),
            canonical,
            new[]
            {
                new ContentBlock("heading", ContentBlockKind.Heading, heading,
                    new SourceSpan(sourceId.Value, 0, heading.Length, null, "/"), 1, "/" + heading),
                new ContentBlock("body", ContentBlockKind.Paragraph, body,
                    new SourceSpan(sourceId.Value, heading.Length + 1, body.Length, null, "/" + heading), 0, "/" + heading),
            },
            new Dictionary<string, string> { ["parser_id"] = "proof-parser", ["parser_version"] = "1.0.0" });
    }

    private static ParsedDocument ParsedSections(params (string Heading, string Body)[] sections)
    {
        var lines = new List<string>();
        var blocks = new List<ContentBlock>();
        var offset = 0;
        foreach (var (heading, body) in sections)
        {
            lines.Add(heading);
            blocks.Add(new ContentBlock($"heading-{blocks.Count}", ContentBlockKind.Heading, heading,
                new SourceSpan("", offset, heading.Length, null, "/"), 1, "/" + heading));
            offset += heading.Length + 1;

            lines.Add(body);
            blocks.Add(new ContentBlock($"body-{blocks.Count}", ContentBlockKind.Paragraph, body,
                new SourceSpan("", offset, body.Length, null, "/" + heading), 0, "/" + heading));
            offset += body.Length + 1;
        }

        var canonical = string.Join("\n", lines);
        var sourceId = ContentHash.OfString(canonical);
        var source = new SourceDocument(sourceId, "test.md", SourceDocumentKind.Markdown,
            canonical.Length, DateTimeOffset.UnixEpoch);
        var normalizedBlocks = blocks.Select(block => block with
        {
            Span = block.Span with { DocumentId = sourceId.Value },
        }).ToArray();
        return new ParsedDocument(source, canonical, normalizedBlocks,
            new Dictionary<string, string> { ["parser_id"] = "proof-parser", ["parser_version"] = "1.0.0" });
    }
}