using System.Text.Json.Nodes;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Ontology;

public enum OntologyArtifactStatus
{
    Active,
    Retired,
}

public enum OntologyDeltaChangeKind
{
    Add,
    Augment,
    Remove,
}

internal static class OntologyRelationshipTypes
{
    private static readonly HashSet<string> Supported = new(StringComparer.Ordinal)
    {
        "alias-of", "child-of", "parent-of", "prohibits", "related-to", "requires",
    };

    public static bool IsSupported(string value) => Supported.Contains(value);
}

public sealed record AcceptedConcept(
    string ConceptId,
    string CanonicalSemanticKey,
    string CanonicalLabel,
    string Description,
    string ConceptType,
    IReadOnlyList<string> Aliases,
    OntologyArtifactStatus Status,
    IReadOnlyList<SourceEvidence> Provenance,
    string CreatedInSnapshot,
    string LastChangedInSnapshot,
    string ContentFingerprint)
{
    public static AcceptedConcept Create(
        string conceptId,
        string canonicalSemanticKey,
        string canonicalLabel,
        string description,
        string conceptType,
        IReadOnlyList<string> aliases,
        OntologyArtifactStatus status,
        IReadOnlyList<SourceEvidence> provenance,
        string createdInSnapshot,
        string lastChangedInSnapshot)
    {
        var concept = new AcceptedConcept(
            conceptId,
            canonicalSemanticKey,
            canonicalLabel,
            description,
            conceptType,
            Array.AsReadOnly(aliases.ToArray()),
            status,
            Array.AsReadOnly(provenance.ToArray()),
            createdInSnapshot,
            lastChangedInSnapshot,
            string.Empty);
        return concept with { ContentFingerprint = OntologyArtifactFingerprint.Compute(concept.ToFingerprintJson()) };
    }

    public JsonObject ToCanonicalJson()
    {
        OntologyModelValidator.ValidateConcept(this, expectedDocumentFingerprint: null);
        var aliases = OntologyArtifactSerialization.StringArray(Aliases);
        var provenance = OntologyArtifactSerialization.EvidenceArray(Provenance);
        return new JsonObject
        {
            ["aliases"] = aliases,
            ["canonicalLabel"] = OntologyCanonical.Normalize(CanonicalLabel),
            ["canonicalSemanticKey"] = OntologyCanonical.Normalize(CanonicalSemanticKey),
            ["conceptId"] = OntologyCanonical.Normalize(ConceptId),
            ["conceptType"] = OntologyCanonical.Normalize(ConceptType),
            ["contentFingerprint"] = ContentFingerprint,
            ["createdInSnapshot"] = OntologyCanonical.Normalize(CreatedInSnapshot),
            ["description"] = OntologyCanonical.Normalize(Description),
            ["lastChangedInSnapshot"] = OntologyCanonical.Normalize(LastChangedInSnapshot),
            ["provenance"] = provenance,
            ["status"] = OntologyArtifactSerialization.Status(Status),
        };
    }

    internal JsonObject ToFingerprintJson() => new()
    {
        ["aliases"] = OntologyArtifactSerialization.StringArray(Aliases),
        ["canonicalLabel"] = OntologyCanonical.Normalize(CanonicalLabel),
        ["canonicalSemanticKey"] = OntologyCanonical.Normalize(CanonicalSemanticKey),
        ["conceptId"] = OntologyCanonical.Normalize(ConceptId),
        ["conceptType"] = OntologyCanonical.Normalize(ConceptType),
        ["description"] = OntologyCanonical.Normalize(Description),
        ["provenance"] = OntologyArtifactSerialization.EvidenceArray(Provenance),
        ["status"] = OntologyArtifactSerialization.Status(Status),
    };
}

public sealed record OntologyRelationship(
    string RelationshipId,
    string RelationshipType,
    string SourceConceptId,
    string TargetConceptId,
    IReadOnlyList<SourceEvidence> Provenance,
    OntologyArtifactStatus Status,
    string ContentFingerprint)
{
    public static OntologyRelationship Create(
        string relationshipId,
        string relationshipType,
        string sourceConceptId,
        string targetConceptId,
        IReadOnlyList<SourceEvidence> provenance,
        OntologyArtifactStatus status)
    {
        var relationship = new OntologyRelationship(
            relationshipId,
            relationshipType,
            sourceConceptId,
            targetConceptId,
            Array.AsReadOnly(provenance.ToArray()),
            status,
            string.Empty);
        return relationship with
        {
            ContentFingerprint = OntologyArtifactFingerprint.Compute(relationship.ToFingerprintJson()),
        };
    }

    public JsonObject ToCanonicalJson()
    {
        OntologyModelValidator.ValidateRelationship(this, expectedDocumentFingerprint: null);
        return new JsonObject
        {
            ["contentFingerprint"] = ContentFingerprint,
            ["provenance"] = OntologyArtifactSerialization.EvidenceArray(Provenance),
            ["relationshipId"] = OntologyCanonical.Normalize(RelationshipId),
            ["relationshipType"] = OntologyCanonical.Normalize(RelationshipType),
            ["sourceConceptId"] = OntologyCanonical.Normalize(SourceConceptId),
            ["status"] = OntologyArtifactSerialization.Status(Status),
            ["targetConceptId"] = OntologyCanonical.Normalize(TargetConceptId),
        };
    }

    internal JsonObject ToFingerprintJson() => new()
    {
        ["provenance"] = OntologyArtifactSerialization.EvidenceArray(Provenance),
        ["relationshipId"] = OntologyCanonical.Normalize(RelationshipId),
        ["relationshipType"] = OntologyCanonical.Normalize(RelationshipType),
        ["sourceConceptId"] = OntologyCanonical.Normalize(SourceConceptId),
        ["status"] = OntologyArtifactSerialization.Status(Status),
        ["targetConceptId"] = OntologyCanonical.Normalize(TargetConceptId),
    };
}

public sealed record ConceptAugmentation(
    string ConceptId,
    string? CanonicalLabel,
    string? Description,
    string? ConceptType,
    IReadOnlyList<SourceEvidence> AddedProvenance,
    string ContentFingerprint)
{
    public static ConceptAugmentation Create(
        string conceptId,
        string? canonicalLabel,
        string? description,
        string? conceptType,
        IReadOnlyList<SourceEvidence> addedProvenance)
    {
        var augmentation = new ConceptAugmentation(
            conceptId, canonicalLabel, description, conceptType,
            Array.AsReadOnly(addedProvenance.ToArray()), string.Empty);
        OntologyModelValidator.ValidateAugmentation(augmentation, expectedDocumentFingerprint: null, verifyFingerprint: false);
        return augmentation with
        {
            ContentFingerprint = OntologyArtifactFingerprint.Compute(augmentation.ToFingerprintJson()),
        };
    }

    public JsonObject ToCanonicalJson()
    {
        OntologyModelValidator.ValidateAugmentation(this, expectedDocumentFingerprint: null);
        var json = ToFingerprintJson();
        json["contentFingerprint"] = ContentFingerprint;
        return json;
    }

    internal JsonObject ToFingerprintJson() => new()
    {
        ["addedProvenance"] = OntologyArtifactSerialization.EvidenceArray(AddedProvenance),
        ["canonicalLabel"] = CanonicalLabel is null ? null : OntologyCanonical.Normalize(CanonicalLabel),
        ["conceptId"] = OntologyCanonical.Normalize(ConceptId),
        ["conceptType"] = ConceptType is null ? null : OntologyCanonical.Normalize(ConceptType),
        ["description"] = Description is null ? null : OntologyCanonical.Normalize(Description),
    };
}

public sealed record AliasChange(
    string ConceptId,
    string Alias,
    OntologyDeltaChangeKind ChangeKind,
    IReadOnlyList<SourceEvidence> Provenance)
{
    public JsonObject ToCanonicalJson() => new()
    {
        ["alias"] = OntologyCanonical.Normalize(Alias),
        ["changeKind"] = OntologyArtifactSerialization.ChangeKind(ChangeKind),
        ["conceptId"] = OntologyCanonical.Normalize(ConceptId),
        ["provenance"] = OntologyArtifactSerialization.EvidenceArray(Provenance),
    };
}

public sealed record RelationshipChange(
    OntologyDeltaChangeKind ChangeKind,
    OntologyRelationship Relationship)
{
    public JsonObject ToCanonicalJson() => new()
    {
        ["changeKind"] = OntologyArtifactSerialization.ChangeKind(ChangeKind),
        ["relationship"] = Relationship.ToCanonicalJson(),
    };
}

public sealed record OntologyUnresolvedItem(
    string ItemType,
    string ItemKey,
    string ReasonCode,
    IReadOnlyList<SourceEvidence> Evidence,
    IReadOnlyList<string> Diagnostics)
{
    public JsonObject ToCanonicalJson() => new()
    {
        ["diagnostics"] = OntologyArtifactSerialization.StringArray(Diagnostics),
        ["evidence"] = OntologyArtifactSerialization.EvidenceArray(Evidence),
        ["itemKey"] = OntologyCanonical.Normalize(ItemKey),
        ["itemType"] = OntologyCanonical.Normalize(ItemType),
        ["reasonCode"] = OntologyCanonical.Normalize(ReasonCode),
    };
}

public sealed record OntologySnapshot(
    string SchemaVersion,
    string OntologyId,
    string DocumentFamilyId,
    string SnapshotId,
    string SourceDocumentFingerprint,
    string? ParentSnapshotId,
    IReadOnlyList<AcceptedConcept> Concepts,
    IReadOnlyList<OntologyRelationship> Relationships,
    IReadOnlyList<OntologyUnresolvedItem> UnresolvedItems,
    string GeneratorContractVersion,
    string CanonicalisationVersion,
    string CreatedByPipelineVersion,
    string SnapshotFingerprint)
{
    public const string CurrentSchemaVersion = "lr-ontology-snapshot/1.0.0";

    public static OntologySnapshot ParseStrict(string json)
        => OntologyArtifactParser.ParseSnapshot(json);

    public static OntologySnapshot Create(
        string ontologyId,
        string documentFamilyId,
        string snapshotId,
        string sourceDocumentFingerprint,
        string? parentSnapshotId,
        IReadOnlyList<AcceptedConcept> concepts,
        IReadOnlyList<OntologyRelationship> relationships,
        IReadOnlyList<OntologyUnresolvedItem> unresolvedItems,
        string generatorContractVersion,
        string createdByPipelineVersion)
    {
        var snapshot = new OntologySnapshot(
            CurrentSchemaVersion,
            ontologyId,
            documentFamilyId,
            snapshotId,
            sourceDocumentFingerprint,
            parentSnapshotId,
            Array.AsReadOnly(concepts.ToArray()),
            Array.AsReadOnly(relationships.ToArray()),
            Array.AsReadOnly(unresolvedItems.ToArray()),
            generatorContractVersion,
            OntologyCanonical.CanonicalisationVersion,
            createdByPipelineVersion,
            string.Empty);
        OntologyModelValidator.ValidateSnapshot(snapshot, verifyFingerprint: false);
        return snapshot with { SnapshotFingerprint = snapshot.RecomputeFingerprint() };
    }

    public string RecomputeFingerprint()
        => OntologyArtifactFingerprint.Compute(ToCanonicalJsonCore(includeFingerprint: false));

    public JsonObject ToCanonicalJson()
    {
        OntologyModelValidator.ValidateSnapshot(this);
        return ToCanonicalJsonCore(includeFingerprint: true);
    }

    public byte[] ToCanonicalBytes() => OntologyCanonicalJson.SerializeToUtf8(ToCanonicalJson());

    private JsonObject ToCanonicalJsonCore(bool includeFingerprint)
    {
        var concepts = new JsonArray();
        foreach (var concept in Concepts.OrderBy(c => OntologyCanonical.Normalize(c.ConceptId), StringComparer.Ordinal))
            concepts.Add(concept.ToCanonicalJson());

        var relationships = new JsonArray();
        foreach (var relationship in Relationships.OrderBy(r => OntologyCanonical.Normalize(r.RelationshipId), StringComparer.Ordinal))
            relationships.Add(relationship.ToCanonicalJson());

        var unresolved = OntologyArtifactSerialization.UnresolvedArray(UnresolvedItems);
        var json = new JsonObject
        {
            ["canonicalisationVersion"] = CanonicalisationVersion,
            ["concepts"] = concepts,
            ["createdByPipelineVersion"] = OntologyCanonical.Normalize(CreatedByPipelineVersion),
            ["documentFamilyId"] = OntologyCanonical.Normalize(DocumentFamilyId),
            ["generatorContractVersion"] = OntologyCanonical.Normalize(GeneratorContractVersion),
            ["ontologyId"] = OntologyCanonical.Normalize(OntologyId),
            ["parentSnapshotId"] = ParentSnapshotId is null ? null : OntologyCanonical.Normalize(ParentSnapshotId),
            ["relationships"] = relationships,
            ["schemaVersion"] = SchemaVersion,
            ["snapshotId"] = OntologyCanonical.Normalize(SnapshotId),
            ["sourceDocumentFingerprint"] = SourceDocumentFingerprint,
            ["unresolvedItems"] = unresolved,
        };
        if (includeFingerprint)
            json["snapshotFingerprint"] = SnapshotFingerprint;
        return json;
    }
}

public sealed record OntologyDelta(
    string SchemaVersion,
    string BaseSnapshotId,
    string TargetDocumentFingerprint,
    IReadOnlyList<AcceptedConcept> AddedConcepts,
    IReadOnlyList<ConceptAugmentation> AugmentedConcepts,
    IReadOnlyList<string> RetiredConcepts,
    IReadOnlyList<AliasChange> AliasChanges,
    IReadOnlyList<RelationshipChange> RelationshipChanges,
    IReadOnlyList<OntologyUnresolvedItem> UnresolvedItems,
    string DeltaFingerprint)
{
    public const string CurrentSchemaVersion = "lr-ontology-delta/1.0.0";

    public static OntologyDelta ParseStrict(string json)
        => OntologyArtifactParser.ParseDelta(json);

    public static OntologyDelta Create(
        string baseSnapshotId,
        string targetDocumentFingerprint,
        IReadOnlyList<AcceptedConcept> addedConcepts,
        IReadOnlyList<ConceptAugmentation> augmentedConcepts,
        IReadOnlyList<string> retiredConcepts,
        IReadOnlyList<AliasChange> aliasChanges,
        IReadOnlyList<RelationshipChange> relationshipChanges,
        IReadOnlyList<OntologyUnresolvedItem> unresolvedItems)
    {
        var delta = new OntologyDelta(
            CurrentSchemaVersion,
            baseSnapshotId,
            targetDocumentFingerprint,
            Array.AsReadOnly(addedConcepts.ToArray()),
            Array.AsReadOnly(augmentedConcepts.ToArray()),
            Array.AsReadOnly(retiredConcepts.ToArray()),
            Array.AsReadOnly(aliasChanges.ToArray()),
            Array.AsReadOnly(relationshipChanges.ToArray()),
            Array.AsReadOnly(unresolvedItems.ToArray()),
            string.Empty);
        OntologyModelValidator.ValidateDelta(delta, verifyFingerprint: false);
        return delta with { DeltaFingerprint = delta.RecomputeFingerprint() };
    }

    public string RecomputeFingerprint()
        => OntologyArtifactFingerprint.Compute(ToCanonicalJsonCore(includeFingerprint: false));

    public JsonObject ToCanonicalJson()
    {
        OntologyModelValidator.ValidateDelta(this);
        return ToCanonicalJsonCore(includeFingerprint: true);
    }

    public byte[] ToCanonicalBytes() => OntologyCanonicalJson.SerializeToUtf8(ToCanonicalJson());

    private JsonObject ToCanonicalJsonCore(bool includeFingerprint)
    {
        var added = new JsonArray();
        foreach (var concept in AddedConcepts.OrderBy(c => OntologyCanonical.Normalize(c.ConceptId), StringComparer.Ordinal))
            added.Add(concept.ToCanonicalJson());

        var augmented = new JsonArray();
        foreach (var concept in AugmentedConcepts.OrderBy(c => OntologyCanonical.Normalize(c.ConceptId), StringComparer.Ordinal))
            augmented.Add(concept.ToCanonicalJson());

        var retired = OntologyArtifactSerialization.StringArray(RetiredConcepts);
        var aliases = new JsonArray();
        foreach (var change in AliasChanges
                     .OrderBy(c => OntologyCanonical.Normalize(c.ConceptId), StringComparer.Ordinal)
                     .ThenBy(c => OntologyCanonical.Normalize(c.Alias), StringComparer.Ordinal)
                     .ThenBy(c => c.ChangeKind))
            aliases.Add(change.ToCanonicalJson());

        var relationships = new JsonArray();
        foreach (var change in RelationshipChanges
                     .OrderBy(c => OntologyCanonical.Normalize(c.Relationship.RelationshipId), StringComparer.Ordinal)
                     .ThenBy(c => c.ChangeKind))
            relationships.Add(change.ToCanonicalJson());

        var json = new JsonObject
        {
            ["addedConcepts"] = added,
            ["aliasChanges"] = aliases,
            ["augmentedConcepts"] = augmented,
            ["baseSnapshotId"] = OntologyCanonical.Normalize(BaseSnapshotId),
            ["retiredConcepts"] = retired,
            ["relationshipChanges"] = relationships,
            ["schemaVersion"] = SchemaVersion,
            ["targetDocumentFingerprint"] = TargetDocumentFingerprint,
            ["unresolvedItems"] = OntologyArtifactSerialization.UnresolvedArray(UnresolvedItems),
        };
        if (includeFingerprint)
            json["deltaFingerprint"] = DeltaFingerprint;
        return json;
    }
}

internal static class OntologyModelValidator
{
    public static void ValidateConcept(AcceptedConcept concept, string? expectedDocumentFingerprint)
    {
        ArgumentNullException.ThrowIfNull(concept);
        OntologyArtifactValidator.Required(concept.ConceptId, "conceptId");
        OntologyArtifactValidator.Required(concept.CanonicalSemanticKey, "canonicalSemanticKey");
        OntologyArtifactValidator.Required(concept.CanonicalLabel, "canonicalLabel");
        OntologyCanonical.Normalize(concept.Description, "description");
        OntologyArtifactValidator.Required(concept.ConceptType, "conceptType");
        OntologyArtifactValidator.Required(concept.CreatedInSnapshot, "createdInSnapshot");
        OntologyArtifactValidator.Required(concept.LastChangedInSnapshot, "lastChangedInSnapshot");
        ValidateStatus(concept.Status);
        OntologyArtifactValidator.ValidateCandidateStrings(concept.Aliases, "aliases", allowEmpty: true);
        ValidateEvidence(concept.Provenance, expectedDocumentFingerprint: null, "provenance", required: true);
        var expected = OntologyArtifactFingerprint.Compute(concept.ToFingerprintJson());
        if (!string.Equals(concept.ContentFingerprint, expected, StringComparison.Ordinal))
            throw new OntologyArtifactValidationException($"Concept '{concept.ConceptId}' content fingerprint mismatch.");
    }

    public static void ValidateRelationship(OntologyRelationship relationship, string? expectedDocumentFingerprint)
    {
        ArgumentNullException.ThrowIfNull(relationship);
        OntologyArtifactValidator.Required(relationship.RelationshipId, "relationshipId");
        var type = OntologyArtifactValidator.Required(relationship.RelationshipType, "relationshipType");
        if (!OntologyRelationshipTypes.IsSupported(type))
            throw new OntologyArtifactValidationException($"Unsupported relationship type '{type}'.");
        OntologyArtifactValidator.Required(relationship.SourceConceptId, "sourceConceptId");
        OntologyArtifactValidator.Required(relationship.TargetConceptId, "targetConceptId");
        ValidateStatus(relationship.Status);
        ValidateEvidence(relationship.Provenance, expectedDocumentFingerprint, "relationship.provenance", required: true);
        var expected = OntologyArtifactFingerprint.Compute(relationship.ToFingerprintJson());
        if (!string.Equals(relationship.ContentFingerprint, expected, StringComparison.Ordinal))
            throw new OntologyArtifactValidationException($"Relationship '{relationship.RelationshipId}' content fingerprint mismatch.");
    }

    public static void ValidateSnapshot(OntologySnapshot snapshot, bool verifyFingerprint = true)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.SchemaVersion != OntologySnapshot.CurrentSchemaVersion)
            throw new OntologyArtifactValidationException("Unsupported ontology snapshot schema version.");
        OntologyArtifactValidator.Required(snapshot.OntologyId, "ontologyId");
        OntologyArtifactValidator.Required(snapshot.DocumentFamilyId, "documentFamilyId");
        OntologyArtifactValidator.Required(snapshot.SnapshotId, "snapshotId");
        var documentFingerprint = OntologyArtifactValidator.RequiredHash(
            snapshot.SourceDocumentFingerprint, "sourceDocumentFingerprint");
        if (snapshot.ParentSnapshotId is not null)
        {
            OntologyArtifactValidator.Required(snapshot.ParentSnapshotId, "parentSnapshotId");
            if (snapshot.ParentSnapshotId == snapshot.SnapshotId)
                throw new OntologyArtifactValidationException("Snapshot cannot be its own parent.");
        }
        OntologyArtifactValidator.Required(snapshot.GeneratorContractVersion, "generatorContractVersion");
        OntologyArtifactValidator.Required(snapshot.CreatedByPipelineVersion, "createdByPipelineVersion");
        if (snapshot.CanonicalisationVersion != OntologyCanonical.CanonicalisationVersion)
            throw new OntologyArtifactValidationException("Unsupported snapshot canonicalisation version.");
        if (snapshot.Concepts is null || snapshot.Relationships is null || snapshot.UnresolvedItems is null)
            throw new OntologyArtifactValidationException("Snapshot arrays must be present.");

        var conceptsById = new HashSet<string>(StringComparer.Ordinal);
        foreach (var concept in snapshot.Concepts)
        {
            ValidateConcept(concept, expectedDocumentFingerprint: null);
            if (!conceptsById.Add(OntologyCanonical.Normalize(concept.ConceptId)))
                throw new OntologyArtifactValidationException("Snapshot contains duplicate concept IDs.");
        }

        var relationshipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relationship in snapshot.Relationships)
        {
            ValidateRelationship(relationship, expectedDocumentFingerprint: null);
            if (!relationshipIds.Add(OntologyCanonical.Normalize(relationship.RelationshipId)))
                throw new OntologyArtifactValidationException("Snapshot contains duplicate relationship IDs.");
            if (!conceptsById.Contains(OntologyCanonical.Normalize(relationship.SourceConceptId))
                || !conceptsById.Contains(OntologyCanonical.Normalize(relationship.TargetConceptId)))
                throw new OntologyArtifactValidationException("Snapshot relationship references an unknown concept ID.");
        }

        ValidateUnresolved(snapshot.UnresolvedItems, expectedDocumentFingerprint: null);
        if (verifyFingerprint && !string.Equals(snapshot.SnapshotFingerprint, snapshot.RecomputeFingerprint(), StringComparison.Ordinal))
            throw new OntologyArtifactValidationException("Snapshot fingerprint mismatch.");
    }

    public static void ValidateDelta(OntologyDelta delta, bool verifyFingerprint = true)
    {
        ArgumentNullException.ThrowIfNull(delta);
        if (delta.SchemaVersion != OntologyDelta.CurrentSchemaVersion)
            throw new OntologyArtifactValidationException("Unsupported ontology delta schema version.");
        OntologyArtifactValidator.Required(delta.BaseSnapshotId, "baseSnapshotId");
        var documentFingerprint = OntologyArtifactValidator.RequiredHash(
            delta.TargetDocumentFingerprint, "targetDocumentFingerprint");
        if (delta.AddedConcepts is null || delta.AugmentedConcepts is null || delta.RetiredConcepts is null
            || delta.AliasChanges is null || delta.RelationshipChanges is null || delta.UnresolvedItems is null)
            throw new OntologyArtifactValidationException("Delta arrays must be present.");

        var added = new HashSet<string>(StringComparer.Ordinal);
        foreach (var concept in delta.AddedConcepts)
        {
            ValidateConcept(concept, documentFingerprint);
            var id = OntologyCanonical.Normalize(concept.ConceptId);
            if (!added.Add(id) || concept.Status != OntologyArtifactStatus.Active)
                throw new OntologyArtifactValidationException("Added concepts must be unique and active.");
        }

        var touched = new HashSet<string>(added, StringComparer.Ordinal);
        foreach (var augmentation in delta.AugmentedConcepts)
        {
            ValidateAugmentation(augmentation, documentFingerprint);
            if (!touched.Add(OntologyCanonical.Normalize(augmentation.ConceptId)))
                throw new OntologyArtifactValidationException("A concept appears more than once in the delta.");
        }

        foreach (var retiredConcept in delta.RetiredConcepts)
        {
            var id = OntologyArtifactValidator.Required(retiredConcept, "retiredConceptId");
            if (!touched.Add(id))
                throw new OntologyArtifactValidationException("A concept appears more than once in the delta.");
        }

        var aliasKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in delta.AliasChanges)
        {
            var aliasConceptId = OntologyArtifactValidator.Required(change.ConceptId, "aliasChange.conceptId");
            var aliasValue = OntologyArtifactValidator.Required(change.Alias, "aliasChange.alias");
            ValidateChangeKind(change.ChangeKind);
            if (change.ChangeKind == OntologyDeltaChangeKind.Augment)
                throw new OntologyArtifactValidationException("Aliases support only add and remove changes.");
            ValidateEvidence(
                change.Provenance,
                change.ChangeKind == OntologyDeltaChangeKind.Add ? documentFingerprint : null,
                "aliasChange.provenance",
                required: change.ChangeKind == OntologyDeltaChangeKind.Add);
            if (!aliasKeys.Add($"{aliasConceptId}\u001f{aliasValue}"))
                throw new OntologyArtifactValidationException("Duplicate alias change.");
        }

        var relationshipKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in delta.RelationshipChanges)
        {
            ArgumentNullException.ThrowIfNull(change);
            ValidateChangeKind(change.ChangeKind);
            ValidateRelationship(
                change.Relationship,
                change.ChangeKind == OntologyDeltaChangeKind.Add ? documentFingerprint : null);
            if (change.ChangeKind == OntologyDeltaChangeKind.Add && change.Relationship.Status != OntologyArtifactStatus.Active
                || change.ChangeKind == OntologyDeltaChangeKind.Augment && change.Relationship.Status != OntologyArtifactStatus.Active
                || change.ChangeKind == OntologyDeltaChangeKind.Remove && change.Relationship.Status != OntologyArtifactStatus.Retired)
                throw new OntologyArtifactValidationException("Relationship change kind and status disagree.");
            var relationshipId = OntologyArtifactValidator.Required(
                change.Relationship.RelationshipId, "relationshipChange.relationshipId");
            if (!relationshipKeys.Add(relationshipId))
                throw new OntologyArtifactValidationException("Duplicate relationship change.");
        }

        ValidateUnresolved(delta.UnresolvedItems, expectedDocumentFingerprint: null);
        if (verifyFingerprint && !string.Equals(delta.DeltaFingerprint, delta.RecomputeFingerprint(), StringComparison.Ordinal))
            throw new OntologyArtifactValidationException("Delta fingerprint mismatch.");
    }

    internal static void ValidateAugmentation(
        ConceptAugmentation augmentation,
        string? expectedDocumentFingerprint,
        bool verifyFingerprint = true)
    {
        ArgumentNullException.ThrowIfNull(augmentation);
        OntologyArtifactValidator.Required(augmentation.ConceptId, "augmentation.conceptId");
        if (augmentation.CanonicalLabel is null && augmentation.Description is null
            && augmentation.ConceptType is null && (augmentation.AddedProvenance is null || augmentation.AddedProvenance.Count == 0))
            throw new OntologyArtifactValidationException("Concept augmentation must change at least one field.");
        if (augmentation.CanonicalLabel is not null)
            OntologyArtifactValidator.Required(augmentation.CanonicalLabel, "augmentation.canonicalLabel");
        if (augmentation.Description is not null)
            OntologyCanonical.Normalize(augmentation.Description, "augmentation.description");
        if (augmentation.ConceptType is not null)
            OntologyArtifactValidator.Required(augmentation.ConceptType, "augmentation.conceptType");
        ValidateEvidence(augmentation.AddedProvenance, expectedDocumentFingerprint, "augmentation.addedProvenance", required: false);
        if (verifyFingerprint)
        {
            var expected = OntologyArtifactFingerprint.Compute(augmentation.ToFingerprintJson());
            if (!string.Equals(augmentation.ContentFingerprint, expected, StringComparison.Ordinal))
                throw new OntologyArtifactValidationException("Concept augmentation content fingerprint mismatch.");
        }
        else if (augmentation.ContentFingerprint.Length > 0)
        {
            OntologyArtifactValidator.RequiredHash(augmentation.ContentFingerprint, "augmentation.contentFingerprint");
        }
    }

    private static void ValidateUnresolved(
        IReadOnlyList<OntologyUnresolvedItem> items,
        string? expectedDocumentFingerprint)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            ArgumentNullException.ThrowIfNull(item);
            var type = OntologyArtifactValidator.Required(item.ItemType, "unresolved.itemType");
            var key = OntologyArtifactValidator.Required(item.ItemKey, "unresolved.itemKey");
            OntologyArtifactValidator.Required(item.ReasonCode, "unresolved.reasonCode");
            OntologyArtifactValidator.ValidateCandidateStrings(item.Diagnostics, "unresolved.diagnostics", allowEmpty: true);
            ValidateEvidence(item.Evidence, expectedDocumentFingerprint, "unresolved.evidence", required: false);
            if (!keys.Add($"{type}\u001f{key}"))
                throw new OntologyArtifactValidationException("Duplicate unresolved item.");
        }
    }

    private static void ValidateEvidence(
        IReadOnlyList<SourceEvidence>? evidence,
        string? expectedDocumentFingerprint,
        string field,
        bool required)
    {
        if (evidence is null || (required && evidence.Count == 0))
            throw new OntologyArtifactValidationException($"'{field}' must be present" + (required ? " and non-empty." : "."));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            ArgumentNullException.ThrowIfNull(item);
            var doc = OntologyArtifactValidator.RequiredHash(item.DocumentFingerprint, $"{field}.documentFingerprint");
            if (expectedDocumentFingerprint is not null && doc != expectedDocumentFingerprint)
                throw new OntologyArtifactValidationException($"'{field}' references a different source document.");
            var section = OntologyArtifactValidator.Required(item.SectionId, $"{field}.sectionId");
            var span = OntologyArtifactValidator.Required(item.SpanId, $"{field}.spanId");
            OntologyArtifactValidator.RequiredHash(item.ExactTextFingerprint, $"{field}.exactTextFingerprint");
            if (item.StartOffset < 0 || item.EndOffset <= item.StartOffset)
                throw new OntologyArtifactValidationException($"'{field}' has an invalid half-open offset range.");
            if (!seen.Add($"{doc}\u001f{section}\u001f{span}\u001f{item.StartOffset}\u001f{item.EndOffset}"))
                throw new OntologyArtifactValidationException($"'{field}' contains duplicate evidence.");
        }
    }

    private static void ValidateStatus(OntologyArtifactStatus status)
    {
        if (status is not OntologyArtifactStatus.Active and not OntologyArtifactStatus.Retired)
            throw new OntologyArtifactValidationException("Unsupported ontology artifact status.");
    }

    private static void ValidateChangeKind(OntologyDeltaChangeKind kind)
    {
        if (kind is not OntologyDeltaChangeKind.Add and not OntologyDeltaChangeKind.Augment and not OntologyDeltaChangeKind.Remove)
            throw new OntologyArtifactValidationException("Unsupported ontology delta change kind.");
    }
}

internal static class OntologyArtifactSerialization
{
    public static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values.Select(v => OntologyCanonical.Normalize(v)).OrderBy(v => v, StringComparer.Ordinal))
            array.Add(value);
        return array;
    }

    public static JsonArray EvidenceArray(IEnumerable<SourceEvidence> values)
    {
        var array = new JsonArray();
        foreach (var value in values.OrderBy(EvidenceSortKey, StringComparer.Ordinal))
            array.Add(value.ToCanonicalJson());
        return array;
    }

    public static JsonArray UnresolvedArray(IEnumerable<OntologyUnresolvedItem> values)
    {
        var array = new JsonArray();
        foreach (var value in values
                     .OrderBy(v => OntologyCanonical.Normalize(v.ItemType), StringComparer.Ordinal)
                     .ThenBy(v => OntologyCanonical.Normalize(v.ItemKey), StringComparer.Ordinal)
                     .ThenBy(v => OntologyCanonical.Normalize(v.ReasonCode), StringComparer.Ordinal))
            array.Add(value.ToCanonicalJson());
        return array;
    }

    public static string Status(OntologyArtifactStatus status) => status switch
    {
        OntologyArtifactStatus.Active => "active",
        OntologyArtifactStatus.Retired => "retired",
        _ => throw new OntologyArtifactValidationException("Unsupported ontology artifact status."),
    };

    public static string ChangeKind(OntologyDeltaChangeKind kind) => kind switch
    {
        OntologyDeltaChangeKind.Add => "add",
        OntologyDeltaChangeKind.Augment => "augment",
        OntologyDeltaChangeKind.Remove => "remove",
        _ => throw new OntologyArtifactValidationException("Unsupported ontology delta change kind."),
    };

    public static string EvidenceSortKey(SourceEvidence evidence)
        => string.Join("\u001f",
            OntologyCanonical.Normalize(evidence.DocumentFingerprint),
            OntologyCanonical.Normalize(evidence.SectionId),
            OntologyCanonical.Normalize(evidence.SpanId),
            evidence.StartOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            evidence.EndOffset.ToString(System.Globalization.CultureInfo.InvariantCulture),
            OntologyCanonical.Normalize(evidence.ExactTextFingerprint));
}

internal static class OntologyArtifactFingerprint
{
    public static string Compute(JsonObject obj)
        => ContentHash.OfBytes(OntologyCanonicalJson.SerializeToUtf8(obj)).Value;
}