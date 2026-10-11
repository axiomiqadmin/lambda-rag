using System.Text.Json;
using System.Text.Json.Nodes;
using LambdaRag.Core.Domain;

namespace LambdaRag.Core.Ontology;

public sealed record CanonicalDocumentArtifact(
    string SchemaVersion,
    string DocumentFingerprint,
    string FamilyId,
    string VersionLabel,
    string ParserId,
    string ParserVersion,
    string CanonicalisationVersion,
    string CanonicalText,
    IReadOnlyList<RawSpan> Spans,
    IReadOnlyList<string> SpanInstanceIds)
{
    public const string CurrentSchemaVersion = "lr-canonical-document/1.0.0";

    public static CanonicalDocumentArtifact Create(
        ParsedDocument parsed,
        SpanVersionIdentity identity,
        IReadOnlyList<SpanVersionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var index = CanonicalSpanEvidenceIndex.FromRecords(parsed, identity, records);
        var adapted = ParsedDocumentSpanAdapter.Adapt(parsed);
        var artifact = new CanonicalDocumentArtifact(
            CurrentSchemaVersion,
            index.DocumentFingerprint,
            identity.FamilyId,
            identity.VersionLabel,
            identity.ParserId,
            identity.ParserVersion,
            identity.CanonicalisationVersion,
            OntologyCanonical.NormalizeMultiline(parsed.CanonicalText, "canonicalText"),
            System.Array.AsReadOnly(adapted.Spans.ToArray()),
            System.Array.AsReadOnly(records.Select(record => record.InstanceId.Value).ToArray()));
        Validate(artifact);
        return artifact;
    }

    public static CanonicalDocumentArtifact ParseStrict(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        OntologyContractJson.RejectDuplicateProperties(root, "$" );
        OntologyContractJson.RequireExactProperties(
            root,
            "$",
            "schemaVersion", "documentFingerprint", "familyId", "versionLabel", "parserId",
            "parserVersion", "canonicalisationVersion", "canonicalText", "spans", "spanInstanceIds");

        var spanElements = Array(root.GetProperty("spans"), "$.spans");
        foreach (var (index, span) in spanElements.Select((item, index) => (index, item)))
        {
            var path = $"$.spans[{index}]";
            OntologyContractJson.RequireExactProperties(
                span, path, "kind", "headingLevel", "text", "charStart", "charLength", "pageNumber");
        }
        var ids = Array(root.GetProperty("spanInstanceIds"), "$.spanInstanceIds");
        foreach (var (index, id) in ids.Select((item, index) => (index, item)))
        {
            if (id.ValueKind != JsonValueKind.String)
                throw new OntologyArtifactValidationException($"Expected a string at $.spanInstanceIds[{index}].");
        }

        try
        {
            var artifact = JsonSerializer.Deserialize<CanonicalDocumentArtifact>(json, CanonicalJson.Options)
                ?? throw new OntologyArtifactValidationException("Canonical document artifact was null.");
            Validate(artifact);
            return artifact;
        }
        catch (JsonException ex)
        {
            throw new OntologyArtifactValidationException($"Invalid canonical document JSON: {ex.Message}");
        }
    }

    public SpanVersionIdentity CreateVersionIdentity()
        => SpanVersionIdentity.Create(FamilyId, VersionLabel, ParserId, ParserVersion);

    public IReadOnlyList<SpanVersionRecord> CreateSpanRecords()
    {
        Validate(this);
        var observations = SpanObservationBuilder.Build(Spans);
        var identity = CreateVersionIdentity();
        var records = new SpanVersionRecord[observations.Count];
        for (var i = 0; i < observations.Count; i++)
            records[i] = new SpanVersionRecord(new SpanInstanceId(SpanInstanceIds[i]), identity, observations[i]);
        return System.Array.AsReadOnly(records);
    }

    public JsonObject ToCanonicalJson()
    {
        Validate(this);
        var spans = new JsonArray();
        foreach (var span in Spans)
            spans.Add(new JsonObject
            {
                ["charLength"] = span.CharLength,
                ["charStart"] = span.CharStart,
                ["headingLevel"] = span.HeadingLevel,
                ["kind"] = span.Kind.ToString(),
                ["pageNumber"] = span.PageNumber,
                ["text"] = OntologyCanonical.Normalize(span.Text, "span.text"),
            });

        var ids = new JsonArray();
        foreach (var id in SpanInstanceIds)
            ids.Add(OntologyCanonical.Normalize(id, "spanInstanceId"));

        return new JsonObject
        {
            ["canonicalText"] = OntologyCanonical.NormalizeMultiline(CanonicalText, "canonicalText"),
            ["canonicalisationVersion"] = CanonicalisationVersion,
            ["documentFingerprint"] = DocumentFingerprint,
            ["familyId"] = FamilyId,
            ["parserId"] = OntologyCanonical.Normalize(ParserId),
            ["parserVersion"] = OntologyCanonical.Normalize(ParserVersion),
            ["schemaVersion"] = SchemaVersion,
            ["spanInstanceIds"] = ids,
            ["spans"] = spans,
            ["versionLabel"] = OntologyCanonical.Normalize(VersionLabel),
        };
    }

    public byte[] ToCanonicalBytes() => OntologyCanonicalJson.SerializeToUtf8(ToCanonicalJson());

    private static void Validate(CanonicalDocumentArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.SchemaVersion != CurrentSchemaVersion)
            throw new OntologyArtifactValidationException("Unsupported canonical document schema version.");
        OntologyArtifactValidator.RequiredHash(artifact.DocumentFingerprint, "documentFingerprint");
        OntologyArtifactValidator.RequiredHash(artifact.FamilyId, "familyId");
        OntologyArtifactValidator.Required(artifact.VersionLabel, "versionLabel");
        OntologyArtifactValidator.Required(artifact.ParserId, "parserId");
        OntologyArtifactValidator.Required(artifact.ParserVersion, "parserVersion");
        if (artifact.CanonicalisationVersion != OntologyCanonical.CanonicalisationVersion)
            throw new OntologyArtifactValidationException("Unsupported canonical document normalization version.");
        OntologyCanonical.NormalizeMultiline(artifact.CanonicalText, "canonicalText");
        if (artifact.Spans is null || artifact.SpanInstanceIds is null
            || artifact.Spans.Count != artifact.SpanInstanceIds.Count)
            throw new OntologyArtifactValidationException("Canonical span and identity arrays must have equal lengths.");

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in artifact.SpanInstanceIds)
        {
            OntologyArtifactValidator.Required(id, "spanInstanceId");
            if (!identities.Add(id))
                throw new OntologyArtifactValidationException("Canonical document contains duplicate span instance IDs.");
        }
        foreach (var span in artifact.Spans)
        {
            ArgumentNullException.ThrowIfNull(span);
            OntologyCanonical.Normalize(span.Text, "span.text");
            if (span.CharStart < 0 || span.CharLength < 0)
                throw new OntologyArtifactValidationException("Canonical span offsets must be non-negative.");
        }

        _ = SpanObservationBuilder.Build(artifact.Spans);
    }

    private static IReadOnlyList<JsonElement> Array(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new OntologyArtifactValidationException($"Expected an array at {path}.");
        return element.EnumerateArray().ToArray();
    }
}