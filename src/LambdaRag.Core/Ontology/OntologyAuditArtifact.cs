using System.Text.Json;
using System.Text.Json.Nodes;
using LambdaRag.Core.Domain;

namespace LambdaRag.Core.Ontology;

public sealed record OntologyAuditArtifact(
    string SchemaVersion,
    string FamilyId,
    string DocumentFingerprint,
    string ParserId,
    string ParserVersion,
    string Status,
    bool ReusedExistingSnapshot,
    string? SnapshotId,
    int SpanRecordCount,
    int ChangedSpanCount,
    int CandidateConceptCount,
    int AcceptedConceptCount,
    int RelationshipCount,
    int UnresolvedSpanCount,
    int UnresolvedOntologyItemCount,
    IReadOnlyList<string> Diagnostics)
{
    public const string CurrentSchemaVersion = "lr-ontology-audit/1.0.0";

    public static OntologyAuditArtifact FromCompilation(
        DocumentFamilyManifest family,
        ParsedDocument parsed,
        OntologyCompilationResult result)
    {
        var parserId = parsed.Metadata.TryGetValue("parser_id", out var id) ? id : string.Empty;
        var parserVersion = parsed.Metadata.TryGetValue("parser_version", out var version) ? version : string.Empty;
        var unresolvedSpans = result.SpanReconciliation?.Results.Count(item =>
            item.Classification is SpanClassification.Unresolved or SpanClassification.UnresolvedReplacement) ?? 0;
        var unresolvedOntology = result.Delta?.UnresolvedItems.Count ?? 0;
        var diagnostics = new List<string>();
        if (unresolvedSpans > 0) diagnostics.Add("span-reconciliation-unresolved");
        if (unresolvedOntology > 0) diagnostics.Add("ontology-reconciliation-unresolved");

        return new OntologyAuditArtifact(
            CurrentSchemaVersion,
            family.FamilyId,
            parsed.Source.Id.Value,
            OntologyCanonical.Normalize(parserId, "parserId"),
            OntologyCanonical.Normalize(parserVersion, "parserVersion"),
            result.ReusedExistingSnapshot ? "reused" : result.CanPublish ? "accepted" : "blocked",
            result.ReusedExistingSnapshot,
            result.Snapshot?.SnapshotId,
            result.CurrentSpanRecords.Count,
            result.SpanReconciliation is null
                ? result.CurrentSpanRecords.Count
                : result.SpanReconciliation.Results.Count(item =>
                    item.Classification is SpanClassification.Added or SpanClassification.SplitFrom),
            result.Candidate?.Concepts.Count ?? 0,
            result.Snapshot?.Concepts.Count ?? 0,
            result.Snapshot?.Relationships.Count ?? 0,
            unresolvedSpans,
            unresolvedOntology,
            diagnostics);
    }

    public static OntologyAuditArtifact ParseStrict(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        OntologyContractJson.RejectDuplicateProperties(root, "$" );
        OntologyContractJson.RequireExactProperties(
            root, "$", "schemaVersion", "familyId", "documentFingerprint", "parserId", "parserVersion",
            "status", "reusedExistingSnapshot", "snapshotId", "spanRecordCount", "changedSpanCount",
            "candidateConceptCount", "acceptedConceptCount", "relationshipCount", "unresolvedSpanCount",
            "unresolvedOntologyItemCount", "diagnostics");

        var diagnostics = root.GetProperty("diagnostics");
        if (diagnostics.ValueKind != JsonValueKind.Array)
            throw new OntologyArtifactValidationException("Audit diagnostics must be an array.");
        var artifact = new OntologyAuditArtifact(
            String(root, "schemaVersion"),
            String(root, "familyId"),
            String(root, "documentFingerprint"),
            String(root, "parserId"),
            String(root, "parserVersion"),
            String(root, "status"),
            root.GetProperty("reusedExistingSnapshot").GetBoolean(),
            root.GetProperty("snapshotId").ValueKind == JsonValueKind.Null
                ? null
                : String(root, "snapshotId"),
            Integer(root, "spanRecordCount"),
            Integer(root, "changedSpanCount"),
            Integer(root, "candidateConceptCount"),
            Integer(root, "acceptedConceptCount"),
            Integer(root, "relationshipCount"),
            Integer(root, "unresolvedSpanCount"),
            Integer(root, "unresolvedOntologyItemCount"),
            diagnostics.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString() ?? string.Empty
                : throw new OntologyArtifactValidationException("Audit diagnostics must be strings.")).ToArray());
        Validate(artifact);
        return artifact;
    }

    public JsonObject ToCanonicalJson()
    {
        Validate(this);
        var diagnostics = new JsonArray();
        foreach (var diagnostic in Diagnostics.OrderBy(value => value, StringComparer.Ordinal))
            diagnostics.Add(OntologyCanonical.Normalize(diagnostic, "diagnostic"));
        return new JsonObject
        {
            ["acceptedConceptCount"] = AcceptedConceptCount,
            ["candidateConceptCount"] = CandidateConceptCount,
            ["changedSpanCount"] = ChangedSpanCount,
            ["diagnostics"] = diagnostics,
            ["documentFingerprint"] = DocumentFingerprint,
            ["familyId"] = FamilyId,
            ["parserId"] = OntologyCanonical.Normalize(ParserId),
            ["parserVersion"] = OntologyCanonical.Normalize(ParserVersion),
            ["relationshipCount"] = RelationshipCount,
            ["reusedExistingSnapshot"] = ReusedExistingSnapshot,
            ["schemaVersion"] = SchemaVersion,
            ["snapshotId"] = SnapshotId is null ? null : OntologyCanonical.Normalize(SnapshotId),
            ["spanRecordCount"] = SpanRecordCount,
            ["status"] = Status,
            ["unresolvedOntologyItemCount"] = UnresolvedOntologyItemCount,
            ["unresolvedSpanCount"] = UnresolvedSpanCount,
        };
    }

    public byte[] ToCanonicalBytes() => OntologyCanonicalJson.SerializeToUtf8(ToCanonicalJson());

    private static void Validate(OntologyAuditArtifact artifact)
    {
        if (artifact.SchemaVersion != CurrentSchemaVersion)
            throw new OntologyArtifactValidationException("Unsupported ontology audit schema version.");
        OntologyArtifactValidator.RequiredHash(artifact.FamilyId, "familyId");
        OntologyArtifactValidator.RequiredHash(artifact.DocumentFingerprint, "documentFingerprint");
        OntologyArtifactValidator.Required(artifact.ParserId, "parserId");
        OntologyArtifactValidator.Required(artifact.ParserVersion, "parserVersion");
        if (artifact.Status is not "accepted" and not "blocked" and not "reused")
            throw new OntologyArtifactValidationException("Unsupported ontology audit status.");
        if (artifact.ReusedExistingSnapshot != (artifact.Status == "reused"))
            throw new OntologyArtifactValidationException("Audit reuse flag and status disagree.");
        if (artifact.Status is "accepted" or "reused" && string.IsNullOrWhiteSpace(artifact.SnapshotId))
            throw new OntologyArtifactValidationException("Published audit must identify its snapshot.");
        if (artifact.Status == "blocked" && artifact.SnapshotId is not null)
            throw new OntologyArtifactValidationException("Blocked audit cannot identify a published snapshot.");
        if (artifact.SpanRecordCount < 0 || artifact.ChangedSpanCount < 0 || artifact.CandidateConceptCount < 0
            || artifact.AcceptedConceptCount < 0 || artifact.RelationshipCount < 0
            || artifact.UnresolvedSpanCount < 0 || artifact.UnresolvedOntologyItemCount < 0)
            throw new OntologyArtifactValidationException("Audit counts must be non-negative.");
        OntologyArtifactValidator.ValidateCandidateStrings(artifact.Diagnostics, "diagnostics", allowEmpty: true);
    }

    private static string String(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String)
            throw new OntologyArtifactValidationException($"Audit field '{property}' must be a string.");
        return value.GetString() ?? string.Empty;
    }

    private static int Integer(JsonElement element, string property)
    {
        if (!element.GetProperty(property).TryGetInt32(out var value))
            throw new OntologyArtifactValidationException($"Audit field '{property}' must be an integer.");
        return value;
    }
}