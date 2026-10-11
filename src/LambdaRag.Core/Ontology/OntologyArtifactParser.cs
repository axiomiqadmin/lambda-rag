using System.Text.Json;
using LambdaRag.Core;

namespace LambdaRag.Core.Ontology;

internal static class OntologyArtifactParser
{
    public static OntologySnapshot ParseSnapshot(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        OntologyContractJson.RejectDuplicateProperties(root, "$" );
        OntologyContractJson.RequireExactProperties(
            root,
            "$",
            "schemaVersion", "ontologyId", "documentFamilyId", "snapshotId",
            "sourceDocumentFingerprint", "parentSnapshotId", "concepts", "relationships",
            "unresolvedItems", "generatorContractVersion", "canonicalisationVersion",
            "createdByPipelineVersion", "snapshotFingerprint");

        foreach (var (index, concept) in Array(root.GetProperty("concepts"), "$.concepts").Select((item, index) => (index, item)))
            ValidateAcceptedConceptShape(concept, $"$.concepts[{index}]");
        foreach (var (index, relationship) in Array(root.GetProperty("relationships"), "$.relationships").Select((item, index) => (index, item)))
            ValidateRelationshipShape(relationship, $"$.relationships[{index}]");
        ValidateUnresolvedArray(root.GetProperty("unresolvedItems"), "$.unresolvedItems");

        try
        {
            var snapshot = JsonSerializer.Deserialize<OntologySnapshot>(json, CanonicalJson.Options)
                ?? throw new OntologyArtifactValidationException("Ontology snapshot was null.");
            OntologyArtifactValidator.ValidateSnapshot(snapshot);
            return snapshot;
        }
        catch (JsonException ex)
        {
            throw new OntologyArtifactValidationException($"Invalid ontology snapshot JSON: {ex.Message}");
        }
    }

    public static OntologyDelta ParseDelta(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        OntologyContractJson.RejectDuplicateProperties(root, "$" );
        OntologyContractJson.RequireExactProperties(
            root,
            "$",
            "schemaVersion", "baseSnapshotId", "targetDocumentFingerprint", "addedConcepts",
            "augmentedConcepts", "retiredConcepts", "aliasChanges", "relationshipChanges",
            "unresolvedItems", "deltaFingerprint");

        foreach (var (index, concept) in Array(root.GetProperty("addedConcepts"), "$.addedConcepts").Select((item, index) => (index, item)))
            ValidateAcceptedConceptShape(concept, $"$.addedConcepts[{index}]");
        foreach (var (index, augmentation) in Array(root.GetProperty("augmentedConcepts"), "$.augmentedConcepts").Select((item, index) => (index, item)))
        {
            var path = $"$.augmentedConcepts[{index}]";
            OntologyContractJson.RequireExactProperties(
                augmentation, path,
                "conceptId", "canonicalLabel", "description", "conceptType", "addedProvenance", "contentFingerprint");
            ValidateEvidenceArray(augmentation.GetProperty("addedProvenance"), path + ".addedProvenance");
        }
        _ = Array(root.GetProperty("retiredConcepts"), "$.retiredConcepts")
            .Select(item => String(item, "$.retiredConcepts[]"))
            .ToArray();
        foreach (var (index, alias) in Array(root.GetProperty("aliasChanges"), "$.aliasChanges").Select((item, index) => (index, item)))
        {
            var path = $"$.aliasChanges[{index}]";
            OntologyContractJson.RequireExactProperties(alias, path, "conceptId", "alias", "changeKind", "provenance");
            ValidateEvidenceArray(alias.GetProperty("provenance"), path + ".provenance");
        }
        foreach (var (index, change) in Array(root.GetProperty("relationshipChanges"), "$.relationshipChanges").Select((item, index) => (index, item)))
        {
            var path = $"$.relationshipChanges[{index}]";
            OntologyContractJson.RequireExactProperties(change, path, "changeKind", "relationship");
            ValidateRelationshipShape(change.GetProperty("relationship"), path + ".relationship");
        }
        ValidateUnresolvedArray(root.GetProperty("unresolvedItems"), "$.unresolvedItems");

        try
        {
            var delta = JsonSerializer.Deserialize<OntologyDelta>(json, CanonicalJson.Options)
                ?? throw new OntologyArtifactValidationException("Ontology delta was null.");
            OntologyArtifactValidator.ValidateDelta(delta);
            return delta;
        }
        catch (JsonException ex)
        {
            throw new OntologyArtifactValidationException($"Invalid ontology delta JSON: {ex.Message}");
        }
    }

    private static void ValidateAcceptedConceptShape(JsonElement element, string path)
    {
        OntologyContractJson.RequireExactProperties(
            element, path,
            "conceptId", "canonicalSemanticKey", "canonicalLabel", "description", "conceptType",
            "aliases", "status", "provenance", "createdInSnapshot", "lastChangedInSnapshot", "contentFingerprint");
        _ = Array(element.GetProperty("aliases"), path + ".aliases")
            .Select(item => String(item, path + ".aliases[]"))
            .ToArray();
        ValidateEvidenceArray(element.GetProperty("provenance"), path + ".provenance");
    }

    private static void ValidateRelationshipShape(JsonElement element, string path)
    {
        OntologyContractJson.RequireExactProperties(
            element, path,
            "relationshipId", "relationshipType", "sourceConceptId", "targetConceptId",
            "provenance", "status", "contentFingerprint");
        ValidateEvidenceArray(element.GetProperty("provenance"), path + ".provenance");
    }

    private static void ValidateEvidenceArray(JsonElement element, string path)
    {
        foreach (var (index, evidence) in Array(element, path).Select((item, index) => (index, item)))
        {
            var evidencePath = $"{path}[{index}]";
            OntologyContractJson.RequireExactProperties(
                evidence, evidencePath,
                "documentFingerprint", "sectionId", "spanId", "startOffset", "endOffset", "exactTextFingerprint");
        }
    }

    private static void ValidateUnresolvedArray(JsonElement element, string path)
    {
        foreach (var (index, item) in Array(element, path).Select((value, index) => (index, value)))
        {
            var itemPath = $"{path}[{index}]";
            OntologyContractJson.RequireExactProperties(
                item, itemPath, "itemType", "itemKey", "reasonCode", "evidence", "diagnostics");
            ValidateEvidenceArray(item.GetProperty("evidence"), itemPath + ".evidence");
            _ = Array(item.GetProperty("diagnostics"), itemPath + ".diagnostics")
                .Select(diagnostic => String(diagnostic, itemPath + ".diagnostics[]"))
                .ToArray();
        }
    }

    private static IEnumerable<JsonElement> Array(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new OntologyArtifactValidationException($"Expected an array at {path}.");
        return element.EnumerateArray().ToArray();
    }

    private static string String(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new OntologyArtifactValidationException($"Expected a string at {path}.");
        return element.GetString() ?? string.Empty;
    }
}