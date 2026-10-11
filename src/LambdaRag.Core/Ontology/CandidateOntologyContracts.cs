using System.Text.Json;
using System.Text.Json.Nodes;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Ontology;

public sealed class OntologyArtifactValidationException : Exception
{
    public OntologyArtifactValidationException(string message) : base(message) { }
}

public sealed record CandidateOntologyDocument(
    string SchemaVersion,
    string SourceDocumentFingerprint,
    string ExtractionContractVersion,
    string GeneratorIdentity,
    IReadOnlyList<CandidateConcept> Concepts,
    IReadOnlyList<CandidateRelationship> Relationships,
    IReadOnlyList<string> Diagnostics)
{
    public const string CurrentSchemaVersion = "lr-candidate-ontology/1.0.0";

    public static CandidateOntologyDocument ParseStrict(
        string json,
        IReadOnlySet<string>? acceptedSemanticKeys = null,
        bool allowExternalEndpoints = false)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        OntologyContractJson.RejectDuplicateProperties(document.RootElement, "$");
        OntologyContractJson.RequireExactProperties(
            document.RootElement,
            "$",
            "schemaVersion", "sourceDocumentFingerprint", "extractionContractVersion",
            "generatorIdentity", "concepts", "relationships", "diagnostics");

        foreach (var (index, concept) in document.RootElement.GetProperty("concepts").EnumerateArray().Select((value, index) => (index, value)))
        {
            var path = $"$.concepts[{index}]";
            OntologyContractJson.RequireExactProperties(
                concept, path,
                "proposedLabel", "proposedDescription", "conceptType", "aliases",
                "sourceEvidence", "proposedSemanticKey", "diagnosticConfidence");
            foreach (var (evidenceIndex, evidence) in concept.GetProperty("sourceEvidence").EnumerateArray().Select((value, index) => (index, value)))
            {
                OntologyContractJson.RequireExactProperties(
                    evidence, $"{path}.sourceEvidence[{evidenceIndex}]",
                    "documentFingerprint", "sectionId", "spanId", "startOffset", "endOffset", "exactTextFingerprint");
            }
        }

        foreach (var (index, relationship) in document.RootElement.GetProperty("relationships").EnumerateArray().Select((value, index) => (index, value)))
        {
            var path = $"$.relationships[{index}]";
            OntologyContractJson.RequireExactProperties(
                relationship, path,
                "proposedRelationshipType", "sourceSemanticKey", "targetSemanticKey", "sourceEvidence");
            foreach (var (evidenceIndex, evidence) in relationship.GetProperty("sourceEvidence").EnumerateArray().Select((value, index) => (index, value)))
            {
                OntologyContractJson.RequireExactProperties(
                    evidence, $"{path}.sourceEvidence[{evidenceIndex}]",
                    "documentFingerprint", "sectionId", "spanId", "startOffset", "endOffset", "exactTextFingerprint");
            }
        }

        var candidate = JsonSerializer.Deserialize<CandidateOntologyDocument>(json, CanonicalJson.Options)
            ?? throw new OntologyArtifactValidationException("Candidate ontology document was null.");
        OntologyArtifactValidator.Validate(candidate, acceptedSemanticKeys, allowExternalEndpoints);
        return candidate;
    }

    public JsonObject ToCanonicalJson(bool allowExternalEndpoints = false)
    {
        OntologyArtifactValidator.Validate(this, allowExternalEndpoints: allowExternalEndpoints);
        var concepts = new JsonArray();
        foreach (var concept in Concepts.OrderBy(c => OntologyCanonical.Normalize(c.ProposedSemanticKey), StringComparer.Ordinal))
            concepts.Add(concept.ToCanonicalJson());

        var relationships = new JsonArray();
        foreach (var relationship in Relationships
                     .OrderBy(r => OntologyCanonical.Normalize(r.ProposedRelationshipType), StringComparer.Ordinal)
                     .ThenBy(r => OntologyCanonical.Normalize(r.SourceSemanticKey), StringComparer.Ordinal)
                     .ThenBy(r => OntologyCanonical.Normalize(r.TargetSemanticKey), StringComparer.Ordinal))
            relationships.Add(relationship.ToCanonicalJson());

        var diagnostics = new JsonArray();
        foreach (var diagnostic in Diagnostics.OrderBy(d => OntologyCanonical.Normalize(d), StringComparer.Ordinal))
            diagnostics.Add(OntologyCanonical.Normalize(diagnostic));

        return new JsonObject
        {
            ["concepts"] = concepts,
            ["diagnostics"] = diagnostics,
            ["extractionContractVersion"] = OntologyCanonical.Normalize(ExtractionContractVersion),
            ["generatorIdentity"] = OntologyCanonical.Normalize(GeneratorIdentity),
            ["relationships"] = relationships,
            ["schemaVersion"] = SchemaVersion,
            ["sourceDocumentFingerprint"] = SourceDocumentFingerprint,
        };
    }

    public byte[] ToCanonicalBytes(bool allowExternalEndpoints = false)
        => OntologyCanonicalJson.SerializeToUtf8(ToCanonicalJson(allowExternalEndpoints));
}

public sealed record CandidateConcept(
    string ProposedLabel,
    string ProposedDescription,
    string ConceptType,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<SourceEvidence> SourceEvidence,
    string ProposedSemanticKey,
    decimal DiagnosticConfidence)
{
    public JsonObject ToCanonicalJson()
    {
        var aliases = new JsonArray();
        foreach (var alias in Aliases.OrderBy(a => OntologyCanonical.Normalize(a), StringComparer.Ordinal))
            aliases.Add(OntologyCanonical.Normalize(alias));

        var evidence = new JsonArray();
        foreach (var item in SourceEvidence.OrderBy(OntologyArtifactSerialization.EvidenceSortKey, StringComparer.Ordinal))
            evidence.Add(item.ToCanonicalJson());

        return new JsonObject
        {
            ["aliases"] = aliases,
            ["conceptType"] = OntologyCanonical.Normalize(ConceptType),
            ["diagnosticConfidence"] = JsonValue.Create(DiagnosticConfidence),
            ["proposedDescription"] = OntologyCanonical.Normalize(ProposedDescription),
            ["proposedLabel"] = OntologyCanonical.Normalize(ProposedLabel),
            ["proposedSemanticKey"] = OntologyCanonical.Normalize(ProposedSemanticKey),
            ["sourceEvidence"] = evidence,
        };
    }

}

public sealed record CandidateRelationship(
    string ProposedRelationshipType,
    string SourceSemanticKey,
    string TargetSemanticKey,
    IReadOnlyList<SourceEvidence> SourceEvidence)
{
    public JsonObject ToCanonicalJson()
    {
        var evidence = new JsonArray();
        foreach (var item in SourceEvidence.OrderBy(OntologyArtifactSerialization.EvidenceSortKey, StringComparer.Ordinal))
            evidence.Add(item.ToCanonicalJson());

        return new JsonObject
        {
            ["proposedRelationshipType"] = OntologyCanonical.Normalize(ProposedRelationshipType),
            ["sourceEvidence"] = evidence,
            ["sourceSemanticKey"] = OntologyCanonical.Normalize(SourceSemanticKey),
            ["targetSemanticKey"] = OntologyCanonical.Normalize(TargetSemanticKey),
        };
    }
}

public sealed record SourceEvidence(
    string DocumentFingerprint,
    string SectionId,
    string SpanId,
    int StartOffset,
    int EndOffset,
    string ExactTextFingerprint)
{
    public JsonObject ToCanonicalJson() => new()
    {
        ["documentFingerprint"] = DocumentFingerprint,
        ["endOffset"] = EndOffset,
        ["exactTextFingerprint"] = ExactTextFingerprint,
        ["sectionId"] = OntologyCanonical.Normalize(SectionId),
        ["spanId"] = OntologyCanonical.Normalize(SpanId),
        ["startOffset"] = StartOffset,
    };
}

public static class OntologyArtifactValidator
{
    public static void ValidateSnapshot(OntologySnapshot snapshot)
        => OntologyModelValidator.ValidateSnapshot(snapshot);

    public static void ValidateDelta(OntologyDelta delta)
        => OntologyModelValidator.ValidateDelta(delta);

    public static void Validate(
        CandidateOntologyDocument candidate,
        IReadOnlySet<string>? acceptedSemanticKeys = null,
        bool allowExternalEndpoints = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.SchemaVersion != CandidateOntologyDocument.CurrentSchemaVersion)
            throw new OntologyArtifactValidationException("Unsupported candidate ontology schema version.");

        var sourceFingerprint = RequiredHash(candidate.SourceDocumentFingerprint, "sourceDocumentFingerprint");
        Required(candidate.ExtractionContractVersion, "extractionContractVersion");
        Required(candidate.GeneratorIdentity, "generatorIdentity");
        if (candidate.Concepts is null || candidate.Relationships is null || candidate.Diagnostics is null)
            throw new OntologyArtifactValidationException("Candidate arrays must be present.");

        var conceptsByKey = new HashSet<string>(StringComparer.Ordinal);
        foreach (var concept in candidate.Concepts)
        {
            if (concept is null)
                throw new OntologyArtifactValidationException("Candidate concepts cannot contain null entries.");
            Required(concept.ProposedLabel, "concept.proposedLabel");
            OntologyCanonical.Normalize(concept.ProposedDescription, "concept.proposedDescription");
            Required(concept.ConceptType, "concept.conceptType");
            var semanticKey = NormalizeSemanticKey(Required(concept.ProposedSemanticKey, "concept.proposedSemanticKey"));
            if (!conceptsByKey.Add(semanticKey))
                throw new OntologyArtifactValidationException($"Duplicate proposed semantic key '{semanticKey}'.");
            ValidateConfidence(concept.DiagnosticConfidence, "concept.diagnosticConfidence");
            ValidateUniqueStrings(concept.Aliases, "concept.aliases", allowEmpty: true);
            ValidateEvidenceList(concept.SourceEvidence, sourceFingerprint, "concept.sourceEvidence");
        }

        var relationshipsSeen = new HashSet<string>(StringComparer.Ordinal);
        var acceptedKeys = acceptedSemanticKeys?
            .Select(NormalizeSemanticKey)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var relationship in candidate.Relationships)
        {
            if (relationship is null)
                throw new OntologyArtifactValidationException("Candidate relationships cannot contain null entries.");
            var type = Required(relationship.ProposedRelationshipType, "relationship.proposedRelationshipType");
            if (!OntologyRelationshipTypes.IsSupported(type))
                throw new OntologyArtifactValidationException($"Unsupported relationship type '{type}'.");
            var source = NormalizeSemanticKey(Required(relationship.SourceSemanticKey, "relationship.sourceSemanticKey"));
            var target = NormalizeSemanticKey(Required(relationship.TargetSemanticKey, "relationship.targetSemanticKey"));
            if (!allowExternalEndpoints
                && ((!conceptsByKey.Contains(source) && !ContainsAccepted(source, acceptedKeys))
                    || (!conceptsByKey.Contains(target) && !ContainsAccepted(target, acceptedKeys))))
                throw new OntologyArtifactValidationException("Candidate relationship references an unknown semantic key.");
            if (!relationshipsSeen.Add($"{type}\u001f{source}\u001f{target}"))
                throw new OntologyArtifactValidationException("Duplicate candidate relationship.");
            ValidateEvidenceList(relationship.SourceEvidence, sourceFingerprint, "relationship.sourceEvidence");
        }

        ValidateUniqueStrings(candidate.Diagnostics, "diagnostics", allowEmpty: true);
    }

    private static string NormalizeSemanticKey(string value)
    {
        var normalized = OntologyCanonical.Normalize(value).Trim();
        return string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }

    private static bool ContainsAccepted(string key, IReadOnlySet<string>? acceptedSemanticKeys)
        => acceptedSemanticKeys is not null && acceptedSemanticKeys.Contains(key);

    internal static string Required(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new OntologyArtifactValidationException($"Required field '{field}' is empty.");
        return OntologyCanonical.NormalizeRequired(value, field);
    }

    internal static string RequiredHash(string? value, string field)
    {
        var normalized = Required(value, field);
        if (!IsHash(normalized))
            throw new OntologyArtifactValidationException($"Field '{field}' is not a canonical content fingerprint.");
        return normalized;
    }

    internal static bool IsHash(string value)
        => value.StartsWith(ContentHash.Prefix, StringComparison.Ordinal)
           && value.Length == ContentHash.Prefix.Length + 64
           && value.AsSpan(ContentHash.Prefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static void ValidateEvidenceList(
        IReadOnlyList<SourceEvidence>? evidence,
        string expectedDocumentFingerprint,
        string field)
    {
        if (evidence is null || evidence.Count == 0)
            throw new OntologyArtifactValidationException($"'{field}' must contain at least one evidence item.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in evidence)
        {
            if (item is null)
                throw new OntologyArtifactValidationException($"'{field}' cannot contain null entries.");
            var documentFingerprint = RequiredHash(item.DocumentFingerprint, $"{field}.documentFingerprint");
            if (documentFingerprint != expectedDocumentFingerprint)
                throw new OntologyArtifactValidationException($"'{field}' references a different source document.");
            var sectionId = Required(item.SectionId, $"{field}.sectionId");
            var spanId = Required(item.SpanId, $"{field}.spanId");
            RequiredHash(item.ExactTextFingerprint, $"{field}.exactTextFingerprint");
            if (item.StartOffset < 0 || item.EndOffset <= item.StartOffset)
                throw new OntologyArtifactValidationException($"'{field}' has an invalid half-open offset range.");
            if (!seen.Add($"{documentFingerprint}\u001f{sectionId}\u001f{spanId}\u001f{item.StartOffset}\u001f{item.EndOffset}"))
                throw new OntologyArtifactValidationException($"'{field}' contains duplicate evidence.");
        }
    }

    private static void ValidateUniqueStrings(
        IReadOnlyList<string>? values,
        string field,
        bool allowEmpty = false)
    {
        if (values is null || (!allowEmpty && values.Count == 0))
            throw new OntologyArtifactValidationException($"'{field}' must be present" + (allowEmpty ? "." : " and non-empty."));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var normalized = allowEmpty && value is not null
                ? OntologyCanonical.Normalize(value, field)
                : Required(value, field);
            if (!seen.Add(normalized))
                throw new OntologyArtifactValidationException($"'{field}' contains a duplicate value.");
        }
    }

    internal static void ValidateCandidateStrings(
        IReadOnlyList<string>? values,
        string field,
        bool allowEmpty = false)
        => ValidateUniqueStrings(values, field, allowEmpty);

    private static void ValidateConfidence(decimal confidence, string field)
    {
        if (confidence < 0m || confidence > 1m)
            throw new OntologyArtifactValidationException($"'{field}' must be between 0 and 1.");
    }
}

internal static class OntologyContractJson
{
    public static void RejectDuplicateProperties(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new OntologyArtifactValidationException($"Duplicate JSON property '{property.Name}' at {path}.");
                RejectDuplicateProperties(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item, $"{path}[{index++}]");
        }
    }

    public static void RequireExactProperties(JsonElement element, string path, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new OntologyArtifactValidationException($"Expected an object at {path}.");

        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        var actual = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var missing = expectedSet.Except(actual, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var extra = actual.Except(expectedSet, StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0 || extra.Length > 0)
            throw new OntologyArtifactValidationException(
                $"Invalid fields at {path}; missing [{string.Join(", ", missing)}], unsupported [{string.Join(", ", extra)}].");
    }
}