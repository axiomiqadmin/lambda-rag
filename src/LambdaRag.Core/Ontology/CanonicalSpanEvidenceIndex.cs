using LambdaRag.Core.Domain;

namespace LambdaRag.Core.Ontology;

public sealed class CanonicalSpanEvidenceIndex
{
    private readonly IReadOnlyDictionary<string, SpanVersionRecord> _recordsById;

    private CanonicalSpanEvidenceIndex(
        string documentFingerprint,
        SpanVersionIdentity versionIdentity,
        IReadOnlyList<SpanVersionRecord> records)
    {
        DocumentFingerprint = documentFingerprint;
        VersionIdentity = versionIdentity;
        Records = records;
        _recordsById = records.ToDictionary(record => record.InstanceId.Value, StringComparer.Ordinal);
    }

    public string DocumentFingerprint { get; }
    public SpanVersionIdentity VersionIdentity { get; }
    public IReadOnlyList<SpanVersionRecord> Records { get; }

    public static CanonicalSpanEvidenceIndex Create(
        ParsedDocument parsed,
        string familyId,
        string versionLabel,
        IReadOnlyList<SpanVersionRecord>? previousRecords = null)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        var adapterInput = ParsedDocumentSpanAdapter.Adapt(parsed);
        var identity = adapterInput.CreateVersionIdentity(familyId, versionLabel);
        var observations = SpanObservationBuilder.Build(adapterInput.Spans);
        var reconciler = new DeterministicSpanReconciler();
        var records = previousRecords is null
            ? reconciler.Seed(observations, identity)
            : reconciler.Reconcile(previousRecords, observations, identity).CurrentRecords;
        return FromRecords(parsed, identity, records);
    }

    internal static CanonicalSpanEvidenceIndex FromRecords(
        ParsedDocument parsed,
        SpanVersionIdentity identity,
        IReadOnlyList<SpanVersionRecord> records)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(records);

        var adapted = ParsedDocumentSpanAdapter.Adapt(parsed);
        if (adapted.ParserId != identity.ParserId || adapted.ParserVersion != identity.ParserVersion
            || identity.CanonicalisationVersion != OntologyCanonical.CanonicalisationVersion)
            throw new ParserVersionMismatchException("Parsed-document producer metadata does not match the span version identity.");

        var observations = SpanObservationBuilder.Build(adapted.Spans);
        if (observations.Count != records.Count)
            throw new OntologyArtifactValidationException("Span records do not cover every parsed content block.");

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            var expected = observations[i];
            if (record.Ordinal != i || record.VersionIdentity != identity
                || record.Observation.Ordinal != expected.Ordinal
                || record.Kind != expected.Kind
                || record.Observation.HeadingLevel != expected.HeadingLevel
                || record.Text != expected.Text
                || record.ContentFingerprint != expected.ContentFingerprint
                || record.AncestryPath != expected.AncestryPath
                || record.Observation.CharStart != expected.CharStart
                || record.Observation.CharLength != expected.CharLength
                || record.Observation.PageNumber != expected.PageNumber)
                throw new OntologyArtifactValidationException($"Span record at ordinal {i} does not match the canonical parsed document.");
            if (!seenIds.Add(record.InstanceId.Value))
                throw new OntologyArtifactValidationException("Span records contain duplicate instance IDs.");
        }

        return new CanonicalSpanEvidenceIndex(
            OntologyArtifactValidator.RequiredHash(parsed.Source.Id.Value, "sourceDocumentFingerprint"),
            identity,
            Array.AsReadOnly(records.ToArray()));
    }

    public SourceEvidence CreateEvidence(SpanVersionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!_recordsById.TryGetValue(record.InstanceId.Value, out var indexed)
            || indexed != record)
            throw new OntologyArtifactValidationException("Span record does not belong to this evidence index.");

        return new SourceEvidence(
            DocumentFingerprint,
            record.Observation.AncestryPath,
            record.InstanceId.Value,
            record.Observation.CharStart,
            checked(record.Observation.CharStart + record.Observation.CharLength),
            record.ContentFingerprint.Value);
    }

    public SpanVersionRecord Resolve(SourceEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        var documentFingerprint = OntologyArtifactValidator.RequiredHash(
            evidence.DocumentFingerprint, "evidence.documentFingerprint");
        if (!string.Equals(documentFingerprint, DocumentFingerprint, StringComparison.Ordinal))
            throw new OntologyArtifactValidationException("Evidence references a different source document.");

        var sectionId = OntologyArtifactValidator.Required(evidence.SectionId, "evidence.sectionId");
        var spanId = OntologyArtifactValidator.Required(evidence.SpanId, "evidence.spanId");
        var exactTextFingerprint = OntologyArtifactValidator.RequiredHash(
            evidence.ExactTextFingerprint, "evidence.exactTextFingerprint");

        if (!_recordsById.TryGetValue(spanId, out var record))
            throw new OntologyArtifactValidationException($"Evidence references nonexistent span '{spanId}'.");

        var observation = record.Observation;
        if (!string.Equals(sectionId, observation.AncestryPath, StringComparison.Ordinal)
            || evidence.StartOffset != observation.CharStart
            || evidence.EndOffset != checked(observation.CharStart + observation.CharLength)
            || !string.Equals(exactTextFingerprint, record.ContentFingerprint.Value, StringComparison.Ordinal))
        {
            throw new OntologyArtifactValidationException(
                $"Evidence for span '{spanId}' does not match its canonical section, offsets, and content fingerprint.");
        }

        return record;
    }

    public void ValidateCandidate(
        CandidateOntologyDocument candidate,
        IReadOnlySet<string>? acceptedSemanticKeys = null)
    {
        OntologyArtifactValidator.Validate(candidate, acceptedSemanticKeys);
        if (!string.Equals(candidate.SourceDocumentFingerprint, DocumentFingerprint, StringComparison.Ordinal))
            throw new OntologyArtifactValidationException("Candidate references a different source document.");

        foreach (var concept in candidate.Concepts)
            foreach (var evidence in concept.SourceEvidence)
                Resolve(evidence);

        foreach (var relationship in candidate.Relationships)
            foreach (var evidence in relationship.SourceEvidence)
                Resolve(evidence);
    }
}