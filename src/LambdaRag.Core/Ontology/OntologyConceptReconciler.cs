namespace LambdaRag.Core.Ontology;

public enum CandidateConceptResolutionKind
{
    ExactExistingConcept,
    ExistingConceptAlias,
    ExistingConceptAugmentation,
    NewConcept,
    PotentialConflict,
    Ambiguous,
    Invalid,
    Unsupported,
}

public sealed record CandidateConceptDecision(
    string CandidateSemanticKey,
    CandidateConceptResolutionKind Resolution,
    string ReasonCode,
    string? ConceptId,
    IReadOnlyList<string> Diagnostics);

public sealed record OntologyConceptReconciliationResult(
    OntologySnapshot? Snapshot,
    OntologyDelta Delta,
    IReadOnlyList<CandidateConceptDecision> Decisions)
{
    public bool CanPublish => Snapshot is not null;
}

public sealed class OntologyConceptReconciler
{
    public OntologyConceptReconciliationResult Reconcile(
        CandidateOntologyDocument candidate,
        CanonicalSpanEvidenceIndex evidenceIndex,
        DocumentFamilyManifest documentFamily,
        string ontologyId,
        string targetSnapshotId,
        string pipelineVersion,
        OntologySnapshot? previousSnapshot = null,
        IReadOnlyDictionary<string, string>? explicitMappings = null)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(evidenceIndex);
        ArgumentNullException.ThrowIfNull(documentFamily);
        OntologyArtifactValidator.Required(ontologyId, "ontologyId");
        OntologyArtifactValidator.Required(targetSnapshotId, "targetSnapshotId");
        OntologyArtifactValidator.Required(pipelineVersion, "pipelineVersion");

        if (!documentFamily.IsIntegrityValid)
            throw new OntologyArtifactValidationException("Document family manifest failed integrity validation.");
        if (evidenceIndex.VersionIdentity.FamilyId != documentFamily.FamilyId)
            throw new OntologyArtifactValidationException("Evidence index belongs to a different document family.");

        if (previousSnapshot is not null)
        {
            OntologyArtifactValidator.ValidateSnapshot(previousSnapshot);
            if (previousSnapshot.DocumentFamilyId != documentFamily.FamilyId
                || previousSnapshot.OntologyId != ontologyId)
                throw new OntologyArtifactValidationException("Parent snapshot belongs to a different ontology or document family.");
            if (previousSnapshot.SnapshotId == targetSnapshotId)
                throw new OntologyArtifactValidationException("Target snapshot ID must differ from its parent snapshot ID.");
        }

        var knownAcceptedSemanticKeys = (previousSnapshot?.Concepts ?? Array.Empty<AcceptedConcept>())
            .Where(concept => concept.Status == OntologyArtifactStatus.Active)
            .Select(concept => OntologyCanonical.Normalize(concept.CanonicalSemanticKey))
            .ToHashSet(StringComparer.Ordinal);
        evidenceIndex.ValidateCandidate(candidate, knownAcceptedSemanticKeys);

        var priorConcepts = previousSnapshot?.Concepts.ToList() ?? new List<AcceptedConcept>();
        var conceptsById = priorConcepts.ToDictionary(c => c.ConceptId, StringComparer.Ordinal);
        var priorRelationships = previousSnapshot?.Relationships.ToList() ?? new List<OntologyRelationship>();
        var decisions = new List<CandidateConceptDecision>();
        var addedConcepts = new List<AcceptedConcept>();
        var augmentations = new List<ConceptAugmentation>();
        var retiredConceptIds = new List<string>();
        var aliasChanges = new List<AliasChange>();
        var relationshipChanges = new List<RelationshipChange>();
        var unresolved = new List<OntologyUnresolvedItem>();
        var claimedConcepts = new HashSet<string>(StringComparer.Ordinal);
        var acceptedConceptIdsBySemanticKey = priorConcepts
            .Where(concept => concept.Status == OntologyArtifactStatus.Active)
            .ToDictionary(concept => IdentityKey(concept.CanonicalSemanticKey), concept => concept.ConceptId, StringComparer.Ordinal);

        foreach (var proposed in candidate.Concepts
                     .OrderBy(c => IdentityKey(c.ProposedSemanticKey), StringComparer.Ordinal)
                     .ThenBy(c => IdentityKey(c.ConceptType), StringComparer.Ordinal))
        {
            var match = FindMatch(proposed, priorConcepts, evidenceIndex, explicitMappings);
            if (match.Ambiguous)
            {
                AddUnresolved(proposed, "ambiguous-concept-match", match.Diagnostics);
                decisions.Add(Decision(proposed, CandidateConceptResolutionKind.Ambiguous,
                    "ambiguous-concept-match", null, match.Diagnostics));
                continue;
            }

            if (match.Conflict is not null)
            {
                AddUnresolved(proposed, "potential-concept-conflict", new[] { match.Conflict });
                decisions.Add(Decision(proposed, CandidateConceptResolutionKind.PotentialConflict,
                    "potential-concept-conflict", null, new[] { match.Conflict }));
                continue;
            }

            if (match.Concept is null)
            {
                var created = CreateNewConcept(proposed, documentFamily, ontologyId, targetSnapshotId);
                if (!claimedConcepts.Add(created.ConceptId))
                {
                    AddUnresolved(proposed, "concept-id-collision", new[] { "A deterministic concept ID was already claimed in this reconciliation." });
                    decisions.Add(Decision(proposed, CandidateConceptResolutionKind.Ambiguous,
                        "concept-id-collision", null, new[] { "A deterministic concept ID collision occurred." }));
                    continue;
                }
                conceptsById[created.ConceptId] = created;
                addedConcepts.Add(created);
                acceptedConceptIdsBySemanticKey[IdentityKey(proposed.ProposedSemanticKey)] = created.ConceptId;
                decisions.Add(Decision(proposed, CandidateConceptResolutionKind.NewConcept,
                    "new-concept", created.ConceptId, Array.Empty<string>()));
                continue;
            }

            var existing = match.Concept;
            if (!claimedConcepts.Add(existing.ConceptId))
            {
                AddUnresolved(proposed, "multiple-candidates-claim-concept",
                    new[] { "More than one distinct candidate resolves to the same accepted concept." });
                decisions.Add(Decision(proposed, CandidateConceptResolutionKind.Ambiguous,
                    "multiple-candidates-claim-concept", existing.ConceptId,
                    new[] { "More than one distinct candidate resolves to the same accepted concept." }));
                continue;
            }

            var update = UpdateExisting(existing, proposed, evidenceIndex, targetSnapshotId, match.AllowRename);
            if (update.Conflict is not null)
            {
                claimedConcepts.Remove(existing.ConceptId);
                AddUnresolved(proposed, "incompatible-concept-redefinition", new[] { update.Conflict });
                decisions.Add(Decision(proposed, CandidateConceptResolutionKind.PotentialConflict,
                    "incompatible-concept-redefinition", existing.ConceptId, new[] { update.Conflict }));
                continue;
            }

            conceptsById[existing.ConceptId] = update.Concept;
            acceptedConceptIdsBySemanticKey[IdentityKey(proposed.ProposedSemanticKey)] = existing.ConceptId;
            if (update.Augmentation is not null)
                augmentations.Add(update.Augmentation);
            aliasChanges.AddRange(update.AliasChanges);

            var resolution = update.Augmentation is not null
                ? CandidateConceptResolutionKind.ExistingConceptAugmentation
                : update.AliasChanges.Count > 0 || match.MatchKind == MatchKind.Alias
                    ? CandidateConceptResolutionKind.ExistingConceptAlias
                    : CandidateConceptResolutionKind.ExactExistingConcept;
            decisions.Add(Decision(proposed, resolution, match.ReasonCode,
                existing.ConceptId, Array.Empty<string>()));
        }

        var liveSpanIds = evidenceIndex.Records
            .Select(record => record.InstanceId.Value)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var prior in priorConcepts.Where(c => c.Status == OntologyArtifactStatus.Active))
        {
            if (claimedConcepts.Contains(prior.ConceptId))
                continue;

            if (prior.Provenance.Any(evidence => liveSpanIds.Contains(evidence.SpanId)))
                continue;

            var retired = AcceptedConcept.Create(
                prior.ConceptId,
                prior.CanonicalSemanticKey,
                prior.CanonicalLabel,
                prior.Description,
                prior.ConceptType,
                prior.Aliases,
                OntologyArtifactStatus.Retired,
                prior.Provenance,
                prior.CreatedInSnapshot,
                targetSnapshotId);
            conceptsById[prior.ConceptId] = retired;
            retiredConceptIds.Add(prior.ConceptId);
        }

        var claimedRelationshipIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var proposed in candidate.Relationships
                     .OrderBy(r => IdentityKey(r.ProposedRelationshipType), StringComparer.Ordinal)
                     .ThenBy(r => IdentityKey(r.SourceSemanticKey), StringComparer.Ordinal)
                     .ThenBy(r => IdentityKey(r.TargetSemanticKey), StringComparer.Ordinal))
        {
            var sourceKey = IdentityKey(proposed.SourceSemanticKey);
            var targetKey = IdentityKey(proposed.TargetSemanticKey);
            if (!acceptedConceptIdsBySemanticKey.TryGetValue(sourceKey, out var sourceId)
                || !acceptedConceptIdsBySemanticKey.TryGetValue(targetKey, out var targetId))
            {
                unresolved.Add(new OntologyUnresolvedItem(
                    "relationship",
                    $"{IdentityKey(proposed.ProposedRelationshipType)}:{sourceKey}:{targetKey}",
                    "unresolved-relationship-endpoint",
                    proposed.SourceEvidence,
                    new[] { "A relationship endpoint did not resolve to an accepted concept." }));
                continue;
            }

            if (!conceptsById.TryGetValue(sourceId, out var sourceConcept)
                || !conceptsById.TryGetValue(targetId, out var targetConcept)
                || sourceConcept.Status != OntologyArtifactStatus.Active
                || targetConcept.Status != OntologyArtifactStatus.Active)
            {
                unresolved.Add(new OntologyUnresolvedItem(
                    "relationship",
                    $"{IdentityKey(proposed.ProposedRelationshipType)}:{sourceKey}:{targetKey}",
                    "relationship-endpoint-retired",
                    proposed.SourceEvidence,
                    new[] { "A relationship endpoint is not active in the target snapshot." }));
                continue;
            }

            var relationshipType = OntologyCanonical.NormalizeRequired(
                proposed.ProposedRelationshipType, "relationshipType");
            var previousRelationship = priorRelationships.SingleOrDefault(relationship =>
                IdentityKey(relationship.RelationshipType) == IdentityKey(relationshipType)
                && relationship.SourceConceptId == sourceId
                && relationship.TargetConceptId == targetId);

            if (previousRelationship is null)
            {
                var created = CreateRelationship(
                    proposed, relationshipType, sourceId, targetId, documentFamily, ontologyId);
                priorRelationships.Add(created);
                claimedRelationshipIds.Add(created.RelationshipId);
                relationshipChanges.Add(new RelationshipChange(OntologyDeltaChangeKind.Add, created));
                continue;
            }

            claimedRelationshipIds.Add(previousRelationship.RelationshipId);
            var newEvidence = proposed.SourceEvidence
                .Where(evidence => !previousRelationship.Provenance.Any(old => SameEvidenceIdentity(old, evidence)))
                .ToArray();
            if (previousRelationship.Status == OntologyArtifactStatus.Retired && newEvidence.Length == 0)
            {
                unresolved.Add(new OntologyUnresolvedItem(
                    "relationship",
                    previousRelationship.RelationshipId,
                    "relationship-reactivation-without-current-evidence",
                    proposed.SourceEvidence,
                    new[] { "A retired relationship requires current source evidence to reactivate." }));
                continue;
            }
            if (previousRelationship.Status == OntologyArtifactStatus.Active && newEvidence.Length == 0)
                continue;

            var updatedRelationship = OntologyRelationship.Create(
                previousRelationship.RelationshipId,
                previousRelationship.RelationshipType,
                previousRelationship.SourceConceptId,
                previousRelationship.TargetConceptId,
                previousRelationship.Provenance.Concat(newEvidence).ToArray(),
                OntologyArtifactStatus.Active);
            ReplaceRelationship(priorRelationships, previousRelationship, updatedRelationship);
            relationshipChanges.Add(new RelationshipChange(
                previousRelationship.Status == OntologyArtifactStatus.Retired
                    ? OntologyDeltaChangeKind.Add
                    : OntologyDeltaChangeKind.Augment,
                updatedRelationship));
        }

        foreach (var relationship in priorRelationships
                     .Where(r => r.Status == OntologyArtifactStatus.Active)
                     .ToArray())
        {
            if (claimedRelationshipIds.Contains(relationship.RelationshipId))
                continue;

            var sourceRetired = retiredConceptIds.Contains(relationship.SourceConceptId, StringComparer.Ordinal);
            var targetRetired = retiredConceptIds.Contains(relationship.TargetConceptId, StringComparer.Ordinal);
            var supportRemoved = !relationship.Provenance.Any(evidence => liveSpanIds.Contains(evidence.SpanId));
            if (!sourceRetired && !targetRetired && !supportRemoved)
                continue;

            var retired = OntologyRelationship.Create(
                relationship.RelationshipId,
                relationship.RelationshipType,
                relationship.SourceConceptId,
                relationship.TargetConceptId,
                relationship.Provenance,
                OntologyArtifactStatus.Retired);
            ReplaceRelationship(priorRelationships, relationship, retired);
            relationshipChanges.Add(new RelationshipChange(OntologyDeltaChangeKind.Remove, retired));
        }

        var delta = OntologyDelta.Create(
            previousSnapshot?.SnapshotId ?? "none",
            evidenceIndex.DocumentFingerprint,
            addedConcepts,
            augmentations,
            retiredConceptIds,
            aliasChanges,
            relationshipChanges,
            unresolved);

        OntologySnapshot? snapshot = null;
        if (unresolved.Count == 0)
        {
            snapshot = OntologySnapshot.Create(
                ontologyId,
                documentFamily.FamilyId,
                targetSnapshotId,
                evidenceIndex.DocumentFingerprint,
                previousSnapshot?.SnapshotId,
                conceptsById.Values.ToArray(),
                priorRelationships,
                Array.Empty<OntologyUnresolvedItem>(),
                candidate.ExtractionContractVersion,
                pipelineVersion);
        }

        return new OntologyConceptReconciliationResult(
            snapshot,
            delta,
            decisions.OrderBy(d => IdentityKey(d.CandidateSemanticKey), StringComparer.Ordinal).ToArray());

        void AddUnresolved(CandidateConcept proposed, string reasonCode, IEnumerable<string> diagnostics)
        {
            var sortedDiagnostics = diagnostics
                .Select(d => OntologyCanonical.Normalize(d))
                .OrderBy(d => d, StringComparer.Ordinal)
                .ToArray();
            unresolved.Add(new OntologyUnresolvedItem(
                "concept",
                IdentityKey(proposed.ProposedSemanticKey),
                reasonCode,
                proposed.SourceEvidence,
                sortedDiagnostics));
        }
    }

    private static MatchResult FindMatch(
        CandidateConcept candidate,
        IReadOnlyList<AcceptedConcept> prior,
        CanonicalSpanEvidenceIndex evidenceIndex,
        IReadOnlyDictionary<string, string>? explicitMappings)
    {
        var key = IdentityKey(candidate.ProposedSemanticKey);
        var type = IdentityKey(candidate.ConceptType);
        var sameKey = prior.Where(c => IdentityKey(c.CanonicalSemanticKey) == key).ToList();
        var sameKeyAndType = sameKey.Where(c => IdentityKey(c.ConceptType) == type).ToList();
        if (sameKeyAndType.Count > 1)
            return MatchResult.AmbiguousResult("Multiple accepted concepts have the same semantic key and type.");
        if (sameKeyAndType.Count == 1)
            return MatchResult.Matched(sameKeyAndType[0], MatchKind.SemanticKey, "exact-semantic-key", allowRename: HasSpanContinuity(sameKeyAndType[0], candidate));
        if (sameKey.Count > 0)
            return MatchResult.ConflictResult("The semantic key already exists with a different concept type.");

        var proposedLabels = CandidatePhrases(candidate).Select(IdentityKey).ToHashSet(StringComparer.Ordinal);
        var aliasMatches = prior
            .Where(c => IdentityKey(c.ConceptType) == type && CandidateMatchesConcept(proposedLabels, c))
            .ToList();
        if (aliasMatches.Count > 1)
            return MatchResult.AmbiguousResult("Candidate phrases match aliases on multiple accepted concepts.");
        if (aliasMatches.Count == 1)
            return MatchResult.Matched(aliasMatches[0], MatchKind.Alias, "normalized-alias-match", allowRename: false);

        var candidateSpanIds = candidate.SourceEvidence.Select(e => e.SpanId).ToHashSet(StringComparer.Ordinal);
        var continuityMatches = prior
            .Where(c => IdentityKey(c.ConceptType) == type
                        && c.Provenance.Any(e => candidateSpanIds.Contains(e.SpanId)
                                                 && evidenceIndex.Records.Any(r => r.InstanceId.Value == e.SpanId)))
            .ToList();
        if (continuityMatches.Count > 1)
            return MatchResult.AmbiguousResult("Supporting spans connect the candidate to multiple accepted concepts.");
        if (continuityMatches.Count == 1)
            return MatchResult.Matched(continuityMatches[0], MatchKind.SpanContinuity,
                "exact-supporting-span-continuity", allowRename: true);

        if (explicitMappings is not null && explicitMappings.TryGetValue(candidate.ProposedSemanticKey, out var mappedId))
        {
            var mapped = prior.SingleOrDefault(c => c.ConceptId == mappedId);
            if (mapped is null)
                return MatchResult.ConflictResult("Explicit mapping points to an unknown accepted concept.");
            if (IdentityKey(mapped.ConceptType) != type)
                return MatchResult.ConflictResult("Explicit mapping points to a different concept type.");
            return MatchResult.Matched(mapped, MatchKind.ExplicitMapping, "explicit-deterministic-mapping",
                allowRename: true);
        }

        var crossTypePhraseMatches = prior
            .Where(c => IdentityKey(c.ConceptType) != type && CandidateMatchesConcept(proposedLabels, c))
            .ToList();
        if (crossTypePhraseMatches.Count > 0)
            return MatchResult.ConflictResult("Candidate phrase matches an accepted concept of a different type.");

        return MatchResult.NoMatch;
    }

    private static UpdateResult UpdateExisting(
        AcceptedConcept existing,
        CandidateConcept proposed,
        CanonicalSpanEvidenceIndex evidenceIndex,
        string targetSnapshotId,
        bool allowRename)
    {
        if (IdentityKey(existing.ConceptType) != IdentityKey(proposed.ConceptType))
            return UpdateResult.ConflictResult("Candidate changes the accepted concept type.");

        var labelMatches = IdentityKey(existing.CanonicalLabel) == IdentityKey(proposed.ProposedLabel)
                           || existing.Aliases.Any(alias => IdentityKey(alias) == IdentityKey(proposed.ProposedLabel));
        if (!labelMatches && !allowRename)
            return UpdateResult.ConflictResult("Candidate proposes an unsupported canonical label change.");

        var newEvidence = proposed.SourceEvidence
            .Where(candidateEvidence => !existing.Provenance.Any(oldEvidence => SameEvidenceIdentity(oldEvidence, candidateEvidence)))
            .ToArray();
        var descriptionChanged = !string.Equals(
            OntologyCanonical.Normalize(existing.Description),
            OntologyCanonical.Normalize(proposed.ProposedDescription),
            StringComparison.Ordinal);
        if (descriptionChanged && newEvidence.Length == 0)
            return UpdateResult.ConflictResult("Candidate changes the description without new source evidence.");

        var newLabel = allowRename && !labelMatches
            ? OntologyCanonical.Normalize(proposed.ProposedLabel)
            : existing.CanonicalLabel;
        var aliases = existing.Aliases.ToList();
        var aliasChanges = new List<AliasChange>();
        foreach (var phrase in CandidatePhrases(proposed))
        {
            var normalized = OntologyCanonical.Normalize(phrase);
            if (IdentityKey(normalized) == IdentityKey(newLabel)
                || IdentityKey(normalized) == IdentityKey(existing.CanonicalLabel)
                || aliases.Any(alias => IdentityKey(alias) == IdentityKey(normalized)))
                continue;
            aliases.Add(normalized);
            aliasChanges.Add(new AliasChange(
                existing.ConceptId,
                normalized,
                OntologyDeltaChangeKind.Add,
                proposed.SourceEvidence));
        }

        var provenance = existing.Provenance.ToList();
        provenance.AddRange(newEvidence);
        var description = descriptionChanged
            ? OntologyCanonical.Normalize(proposed.ProposedDescription)
            : existing.Description;
        var changed = descriptionChanged || newEvidence.Length > 0 || aliasChanges.Count > 0 || newLabel != existing.CanonicalLabel
                      || existing.Status != OntologyArtifactStatus.Active;
        var updated = changed
            ? AcceptedConcept.Create(
                existing.ConceptId,
                existing.CanonicalSemanticKey,
                newLabel,
                description,
                existing.ConceptType,
                aliases,
                OntologyArtifactStatus.Active,
                provenance,
                existing.CreatedInSnapshot,
                targetSnapshotId)
            : existing;

        ConceptAugmentation? augmentation = null;
        if (descriptionChanged || newEvidence.Length > 0 || newLabel != existing.CanonicalLabel)
        {
            augmentation = ConceptAugmentation.Create(
                existing.ConceptId,
                newLabel != existing.CanonicalLabel ? newLabel : null,
                descriptionChanged ? description : null,
                null,
                newEvidence);
        }

        return new UpdateResult(updated, augmentation, aliasChanges, null);
    }

    private static AcceptedConcept CreateNewConcept(
        CandidateConcept candidate,
        DocumentFamilyManifest family,
        string ontologyId,
        string snapshotId)
    {
        var origin = candidate.SourceEvidence
            .OrderBy(e => e.SpanId, StringComparer.Ordinal)
            .First().SpanId;
        var id = new CanonicalFingerprint()
            .Tag("lr-concept/1")
            .Tag("ontology").Text(ontologyId)
            .Tag("family").Text(family.FamilyId)
            .Tag("type").Text(IdentityKey(candidate.ConceptType))
            .Tag("semanticKey").Text(IdentityKey(candidate.ProposedSemanticKey))
            .Tag("originSpan").Text(origin)
            .Build();

        return AcceptedConcept.Create(
            "ci:" + id.Value,
            IdentityKey(candidate.ProposedSemanticKey),
            OntologyCanonical.NormalizeRequired(candidate.ProposedLabel, "proposedLabel"),
            OntologyCanonical.Normalize(candidate.ProposedDescription, "proposedDescription"),
            IdentityKey(candidate.ConceptType),
            candidate.Aliases,
            OntologyArtifactStatus.Active,
            candidate.SourceEvidence,
            snapshotId,
            snapshotId);
    }

    private static OntologyRelationship CreateRelationship(
        CandidateRelationship proposed,
        string relationshipType,
        string sourceConceptId,
        string targetConceptId,
        DocumentFamilyManifest family,
        string ontologyId)
    {
        var origin = proposed.SourceEvidence
            .OrderBy(evidence => evidence.SpanId, StringComparer.Ordinal)
            .First().SpanId;
        var fingerprint = new CanonicalFingerprint()
            .Tag("lr-relationship/1")
            .Tag("ontology").Text(ontologyId)
            .Tag("family").Text(family.FamilyId)
            .Tag("type").Text(relationshipType)
            .Tag("source").Text(sourceConceptId)
            .Tag("target").Text(targetConceptId)
            .Tag("originSpan").Text(origin)
            .Build();

        return OntologyRelationship.Create(
            "ri:" + fingerprint.Value,
            relationshipType,
            sourceConceptId,
            targetConceptId,
            proposed.SourceEvidence,
            OntologyArtifactStatus.Active);
    }

    private static void ReplaceRelationship(
        List<OntologyRelationship> relationships,
        OntologyRelationship previous,
        OntologyRelationship replacement)
    {
        var index = relationships.FindIndex(relationship => relationship.RelationshipId == previous.RelationshipId);
        if (index < 0)
            throw new InvalidOperationException($"Relationship '{previous.RelationshipId}' was not found in the target list.");
        relationships[index] = replacement;
    }

    private static bool CandidateMatchesConcept(IReadOnlySet<string> phrases, AcceptedConcept concept)
        => phrases.Contains(IdentityKey(concept.CanonicalLabel))
           || concept.Aliases.Any(alias => phrases.Contains(IdentityKey(alias)));

    private static bool HasSpanContinuity(AcceptedConcept concept, CandidateConcept candidate)
    {
        var ids = concept.Provenance.Select(e => e.SpanId).ToHashSet(StringComparer.Ordinal);
        return candidate.SourceEvidence.Any(evidence => ids.Contains(evidence.SpanId));
    }

    private static bool SameEvidenceIdentity(SourceEvidence left, SourceEvidence right)
        => left.DocumentFingerprint == right.DocumentFingerprint
           && left.SpanId == right.SpanId
           && left.StartOffset == right.StartOffset
           && left.EndOffset == right.EndOffset
           && left.ExactTextFingerprint == right.ExactTextFingerprint;

    private static IEnumerable<string> CandidatePhrases(CandidateConcept candidate)
        => new[] { candidate.ProposedLabel }.Concat(candidate.Aliases);

    private static string IdentityKey(string value)
    {
        var normalized = OntologyCanonical.Normalize(value).Trim();
        return string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }

    private static CandidateConceptDecision Decision(
        CandidateConcept candidate,
        CandidateConceptResolutionKind kind,
        string reason,
        string? conceptId,
        IReadOnlyList<string> diagnostics)
        => new(candidate.ProposedSemanticKey, kind, reason, conceptId, diagnostics);

    private sealed record MatchResult(
        AcceptedConcept? Concept,
        MatchKind MatchKind,
        string ReasonCode,
        bool AllowRename,
        bool Ambiguous,
        string? Conflict,
        IReadOnlyList<string> Diagnostics)
    {
        public static MatchResult NoMatch { get; } = new(null, MatchKind.None, "new-concept", false, false, null, Array.Empty<string>());
        public static MatchResult Matched(AcceptedConcept concept, MatchKind kind, string reason, bool allowRename)
            => new(concept, kind, reason, allowRename, false, null, Array.Empty<string>());
        public static MatchResult AmbiguousResult(string diagnostic)
            => new(null, MatchKind.None, "ambiguous-concept-match", false, true, null, new[] { diagnostic });
        public static MatchResult ConflictResult(string diagnostic)
            => new(null, MatchKind.None, "potential-concept-conflict", false, false, diagnostic, new[] { diagnostic });
    }

    private sealed record UpdateResult(
        AcceptedConcept Concept,
        ConceptAugmentation? Augmentation,
        IReadOnlyList<AliasChange> AliasChanges,
        string? Conflict)
    {
        public static UpdateResult ConflictResult(string conflict)
            => new(null!, null, Array.Empty<AliasChange>(), conflict);
    }

    private enum MatchKind
    {
        None,
        SemanticKey,
        Alias,
        SpanContinuity,
        ExplicitMapping,
    }
}