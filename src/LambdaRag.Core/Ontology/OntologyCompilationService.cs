using LambdaRag.Core.Domain;

namespace LambdaRag.Core.Ontology;

public sealed record OntologyCompilationResult(
    OntologySnapshot? Snapshot,
    CandidateOntologyDocument? Candidate,
    OntologyDelta? Delta,
    IReadOnlyList<SpanVersionRecord> CurrentSpanRecords,
    SpanReconciliationResultSet? SpanReconciliation,
    bool ReusedExistingSnapshot)
{
    public bool CanPublish => Snapshot is not null;
}

public sealed class OntologyCompilationService
{
    private const string EmptyGenerationIdentity = "deterministic-no-changed-spans/1.0.0";
    private readonly ICandidateOntologyGenerator _generator;
    private readonly DeterministicSpanReconciler _spanReconciler;
    private readonly OntologyConceptReconciler _conceptReconciler;

    public OntologyCompilationService(
        ICandidateOntologyGenerator generator,
        DeterministicSpanReconciler? spanReconciler = null,
        OntologyConceptReconciler? conceptReconciler = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _spanReconciler = spanReconciler ?? new DeterministicSpanReconciler();
        _conceptReconciler = conceptReconciler ?? new OntologyConceptReconciler();
    }

    public async Task<OntologyCompilationResult> CompileAsync(
        ParsedDocument parsed,
        DocumentFamilyManifest documentFamily,
        string ontologyId,
        string versionLabel,
        string targetSnapshotId,
        string pipelineVersion,
        string extractionContractVersion,
        OntologySnapshot? previousSnapshot = null,
        IReadOnlyList<SpanVersionRecord>? previousSpanRecords = null,
        IReadOnlyDictionary<string, string>? deterministicSettings = null,
        IReadOnlyDictionary<string, string>? explicitMappings = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(documentFamily);
        OntologyArtifactValidator.Required(ontologyId, "ontologyId");
        OntologyArtifactValidator.Required(versionLabel, "versionLabel");
        OntologyArtifactValidator.Required(targetSnapshotId, "targetSnapshotId");
        OntologyArtifactValidator.Required(pipelineVersion, "pipelineVersion");
        OntologyArtifactValidator.Required(extractionContractVersion, "extractionContractVersion");
        if (!documentFamily.IsIntegrityValid)
            throw new OntologyArtifactValidationException("Document family manifest failed integrity validation.");

        var currentDocumentFingerprint = OntologyArtifactValidator.RequiredHash(
            parsed.Source.Id.Value, "sourceDocumentFingerprint");
        if (previousSnapshot is not null)
        {
            OntologyArtifactValidator.ValidateSnapshot(previousSnapshot);
            if (previousSnapshot.DocumentFamilyId != documentFamily.FamilyId
                || previousSnapshot.OntologyId != ontologyId)
                throw new OntologyArtifactValidationException("Parent snapshot belongs to a different ontology or document family.");

            if (previousSnapshot.SourceDocumentFingerprint == currentDocumentFingerprint)
            {
                if (previousSpanRecords is not null)
                {
                    var adaptedForRerun = ParsedDocumentSpanAdapter.Adapt(parsed);
                    if (previousSpanRecords.Count > 0)
                    {
                        var previousIdentity = previousSpanRecords[0].VersionIdentity;
                        if (previousIdentity.FamilyId != documentFamily.FamilyId
                            || previousIdentity.ParserId != adaptedForRerun.ParserId
                            || previousIdentity.ParserVersion != adaptedForRerun.ParserVersion
                            || previousIdentity.CanonicalisationVersion != OntologyCanonical.CanonicalisationVersion)
                            throw new ParserVersionMismatchException("Exact document bytes were produced by an incompatible parser or canonicalisation version.");
                        _ = CanonicalSpanEvidenceIndex.FromRecords(parsed, previousIdentity, previousSpanRecords);
                    }
                }
                return new OntologyCompilationResult(
                    previousSnapshot,
                    null,
                    null,
                    previousSpanRecords ?? Array.Empty<SpanVersionRecord>(),
                    null,
                    ReusedExistingSnapshot: true);
            }

            if (previousSpanRecords is null)
                throw new OntologyArtifactValidationException("Incremental compilation requires the parent snapshot's span records.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var adapted = ParsedDocumentSpanAdapter.Adapt(parsed);
        var currentIdentity = adapted.CreateVersionIdentity(documentFamily.FamilyId, versionLabel);
        var currentObservations = SpanObservationBuilder.Build(adapted.Spans);
        SpanReconciliationResultSet? spanReconciliation = null;
        IReadOnlyList<SpanVersionRecord> currentRecords;
        IReadOnlySet<int> changedOrdinals;

        if (previousSpanRecords is null)
        {
            currentRecords = _spanReconciler.Seed(currentObservations, currentIdentity);
            changedOrdinals = currentRecords.Select(record => record.Ordinal).ToHashSet();
        }
        else
        {
            spanReconciliation = _spanReconciler.Reconcile(
                previousSpanRecords,
                currentObservations,
                currentIdentity,
                SpanReconciliationPolicy.Strict);
            currentRecords = spanReconciliation.CurrentRecords;
            if (spanReconciliation.HasUnresolved)
            {
                return new OntologyCompilationResult(null, null, null, currentRecords,
                    spanReconciliation, ReusedExistingSnapshot: false);
            }

            changedOrdinals = spanReconciliation.Results
                .Where(result => result.CurrentOrdinal is not null
                                 && result.Classification is SpanClassification.Added or SpanClassification.SplitFrom)
                .Select(result => result.CurrentOrdinal!.Value)
                .ToHashSet();
        }

        var evidenceIndex = CanonicalSpanEvidenceIndex.FromRecords(parsed, currentIdentity, currentRecords);
        CandidateOntologyDocument candidate;
        if (changedOrdinals.Count == 0)
        {
            candidate = EmptyCandidate(currentDocumentFingerprint, extractionContractVersion);
        }
        else
        {
            var generationSpans = currentRecords
                .Where(record => changedOrdinals.Contains(record.Ordinal))
                .OrderBy(record => record.Ordinal)
                .Select(OntologyGenerationSpan.FromRecord)
                .ToArray();
            var settings = new Dictionary<string, string>(deterministicSettings ?? new Dictionary<string, string>(), StringComparer.Ordinal)
            {
                ["canonicalisationVersion"] = currentIdentity.CanonicalisationVersion,
                ["documentFamilyId"] = documentFamily.FamilyId,
                ["parserId"] = currentIdentity.ParserId,
                ["parserVersion"] = currentIdentity.ParserVersion,
                ["versionLabel"] = currentIdentity.VersionLabel,
            };
            var generationRequest = OntologyGenerationRequest.Create(
                currentDocumentFingerprint,
                generationSpans,
                previousSnapshot?.Concepts ?? Array.Empty<AcceptedConcept>(),
                extractionContractVersion,
                generatorIdentity: deterministicSettings?.GetValueOrDefault("generatorIdentity") ?? "candidate-generator/1",
                settings);
            candidate = await _generator.GenerateAsync(generationRequest, cancellationToken).ConfigureAwait(false);
        }

        var reconciliation = _conceptReconciler.Reconcile(
            candidate,
            evidenceIndex,
            documentFamily,
            ontologyId,
            targetSnapshotId,
            pipelineVersion,
            previousSnapshot,
            explicitMappings);

        return new OntologyCompilationResult(
            reconciliation.Snapshot,
            candidate,
            reconciliation.Delta,
            currentRecords,
            spanReconciliation,
            ReusedExistingSnapshot: false);
    }

    private static CandidateOntologyDocument EmptyCandidate(
        string documentFingerprint,
        string extractionContractVersion)
        => new(
            CandidateOntologyDocument.CurrentSchemaVersion,
            documentFingerprint,
            OntologyCanonical.NormalizeRequired(extractionContractVersion, "extractionContractVersion"),
            EmptyGenerationIdentity,
            Array.Empty<CandidateConcept>(),
            Array.Empty<CandidateRelationship>(),
            Array.Empty<string>());
}