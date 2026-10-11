using System.Text;
using System.Text.Json.Nodes;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Ontology;

public sealed record OntologyArtifactPaths(
    string FamilyDirectory,
    string DocumentDirectory,
    string VersionDirectory,
    string CanonicalDocumentPath,
    string CandidateOntologyPath,
    string OntologyDeltaPath,
    string AcceptedOntologyPath,
    string AuditPath,
    string ExtractedOntologyPath,
    string ExtractedEntitiesPath,
    string ExtractedRelationshipsPath,
    string ConfidenceScoresPath,
    string ValidationReportPath,
    string ExecutionLogPath);

public sealed class FileOntologyArtifactStore
{
    public const string DefaultRootDirectoryName = "ontology-artifacts";
    private readonly string _rootDirectory;

    public FileOntologyArtifactStore(string rootDirectory)
    {
        _rootDirectory = Path.GetFullPath(
            OntologyCanonical.NormalizeRequired(rootDirectory, nameof(rootDirectory)));
    }

    public OntologyArtifactPaths GetPaths(
        string familyId,
        string documentFingerprint,
        string versionId)
    {
        var familyHash = OntologyArtifactValidator.RequiredHash(familyId, "familyId");
        var documentHash = OntologyArtifactValidator.RequiredHash(documentFingerprint, "documentFingerprint");
        var normalizedVersion = OntologyCanonical.NormalizeRequired(versionId, "versionId");
        var familyDirectory = Path.Combine(_rootDirectory, Slug(familyHash));
        var documentDirectory = Path.Combine(familyDirectory, "documents", Slug(documentHash));
        var versionDirectory = Path.Combine(documentDirectory, "versions", Slug(ContentHash.OfString(normalizedVersion).Value));
        return new OntologyArtifactPaths(
            familyDirectory,
            documentDirectory,
            versionDirectory,
            Path.Combine(versionDirectory, "canonical-document.json"),
            Path.Combine(versionDirectory, "candidate-ontology.json"),
            Path.Combine(versionDirectory, "ontology-delta.json"),
            Path.Combine(versionDirectory, "accepted-ontology.json"),
            Path.Combine(versionDirectory, "audit.json"),
            Path.Combine(versionDirectory, "extracted-ontology.json"),
            Path.Combine(versionDirectory, "extracted-entities.json"),
            Path.Combine(versionDirectory, "extracted-relationships.json"),
            Path.Combine(versionDirectory, "confidence-scores.json"),
            Path.Combine(versionDirectory, "validation-report.json"),
            Path.Combine(versionDirectory, "execution.log"));
    }

    public Task SaveFamilyManifestAsync(
        DocumentFamilyManifest family,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(family);
        if (!family.IsIntegrityValid)
            throw new OntologyArtifactValidationException("Document family manifest failed integrity validation.");
        var path = FamilyManifestPath(family.FamilyId);
        return WriteImmutableAsync(path, family.ToCanonicalBytes(), cancellationToken);
    }

    public async Task<DocumentFamilyManifest?> LoadFamilyManifestAsync(
        string familyId,
        CancellationToken cancellationToken = default)
    {
        var expectedId = OntologyArtifactValidator.RequiredHash(familyId, "familyId");
        var path = FamilyManifestPath(expectedId);
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var manifest = DocumentFamilyManifest.ParseStrict(json);
        if (manifest.FamilyId != expectedId)
            throw new OntologyArtifactValidationException("Family manifest path does not match its declared ID.");
        return manifest;
    }

    public async Task<OntologySnapshot?> FindSnapshotByIdAsync(
        string familyId,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var familyHash = OntologyArtifactValidator.RequiredHash(familyId, "familyId");
        var normalizedSnapshotId = OntologyCanonical.NormalizeRequired(snapshotId, "snapshotId");
        var familyDirectory = Path.Combine(_rootDirectory, Slug(familyHash));
        if (!Directory.Exists(familyDirectory)) return null;

        OntologySnapshot? found = null;
        foreach (var path in Directory.EnumerateFiles(familyDirectory, "accepted-ontology.json", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            var snapshot = OntologySnapshot.ParseStrict(json);
            if (snapshot.SnapshotId != normalizedSnapshotId) continue;
            if (snapshot.DocumentFamilyId != familyHash)
                throw new OntologyArtifactValidationException("Snapshot stored under the wrong document family.");
            if (found is not null && found.SnapshotFingerprint != snapshot.SnapshotFingerprint)
                throw new OntologyArtifactValidationException("Snapshot ID resolves to conflicting immutable snapshots.");
            found = snapshot;
        }
        return found;
    }

    public async Task<OntologyArtifactPaths> SaveCompilationAsync(
        DocumentFamilyManifest family,
        ParsedDocument parsed,
        SpanVersionIdentity versionIdentity,
        string targetSnapshotId,
        OntologyCompilationResult compilation,
        OntologyAuditArtifact? audit = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(family);
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(versionIdentity);
        ArgumentNullException.ThrowIfNull(compilation);
        if (!family.IsIntegrityValid)
            throw new OntologyArtifactValidationException("Document family manifest failed integrity validation.");
        if (versionIdentity.FamilyId != family.FamilyId)
            throw new OntologyArtifactValidationException("Version identity belongs to a different document family.");

        var documentFingerprint = OntologyArtifactValidator.RequiredHash(parsed.Source.Id.Value, "documentFingerprint");
        var snapshotOrTargetId = compilation.Snapshot?.SnapshotId ?? targetSnapshotId;
        var paths = GetPaths(family.FamilyId, documentFingerprint, snapshotOrTargetId);
        var documentArtifact = CanonicalDocumentArtifact.Create(parsed, versionIdentity, compilation.CurrentSpanRecords);
        var auditArtifact = audit ?? OntologyAuditArtifact.FromCompilation(family, parsed, compilation);
        OntologyAuditArtifact.ParseStrict(OntologyCanonicalJson.Serialize(auditArtifact.ToCanonicalJson()));
        if (auditArtifact.FamilyId != family.FamilyId
            || auditArtifact.DocumentFingerprint != documentFingerprint
            || auditArtifact.SnapshotId != compilation.Snapshot?.SnapshotId)
            throw new OntologyArtifactValidationException("Audit identity does not match the compilation artifact bundle.");

        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["canonical-document.json"] = documentArtifact.ToCanonicalBytes(),
        };
        if (compilation.Candidate is not null)
            files["candidate-ontology.json"] = compilation.Candidate.ToCanonicalBytes(allowExternalEndpoints: true);
        if (compilation.Delta is not null)
            files["ontology-delta.json"] = compilation.Delta.ToCanonicalBytes();
        if (compilation.Snapshot is not null)
        {
            files["accepted-ontology.json"] = compilation.Snapshot.ToCanonicalBytes();
            files["extracted-ontology.json"] = compilation.Snapshot.ToCanonicalBytes();
        }

        var concepts = compilation.Snapshot?.Concepts ?? Array.Empty<AcceptedConcept>();
        var conceptArray = new JsonArray(concepts
            .OrderBy(concept => concept.ConceptId, StringComparer.Ordinal)
            .Select(concept => (JsonNode?)concept.ToCanonicalJson())
            .ToArray());
        var relationships = compilation.Snapshot?.Relationships ?? Array.Empty<OntologyRelationship>();
        var relationshipArray = new JsonArray(relationships
            .OrderBy(relationship => relationship.RelationshipId, StringComparer.Ordinal)
            .Select(relationship => (JsonNode?)relationship.ToCanonicalJson())
            .ToArray());
        files["extracted-entities.json"] = OntologyCanonicalJson.SerializeToUtf8(new JsonObject
        {
            ["concepts"] = conceptArray,
            ["documentFingerprint"] = documentFingerprint,
            ["schemaVersion"] = "lr-ontology-entities/1.0.0",
            ["snapshotId"] = compilation.Snapshot?.SnapshotId,
        });
        files["extracted-relationships.json"] = OntologyCanonicalJson.SerializeToUtf8(new JsonObject
        {
            ["documentFingerprint"] = documentFingerprint,
            ["relationships"] = relationshipArray,
            ["schemaVersion"] = "lr-ontology-relationships/1.0.0",
            ["snapshotId"] = compilation.Snapshot?.SnapshotId,
        });
        var confidenceScores = new JsonArray();
        foreach (var concept in compilation.Candidate?.Concepts
                     .OrderBy(concept => concept.ProposedSemanticKey, StringComparer.Ordinal)
                 ?? Enumerable.Empty<CandidateConcept>())
        {
            confidenceScores.Add(new JsonObject
            {
                ["diagnosticConfidence"] = JsonValue.Create(concept.DiagnosticConfidence),
                ["proposedSemanticKey"] = OntologyCanonical.Normalize(concept.ProposedSemanticKey),
            });
        }
        files["confidence-scores.json"] = OntologyCanonicalJson.SerializeToUtf8(new JsonObject
        {
            ["generatorIdentity"] = compilation.Candidate?.GeneratorIdentity,
            ["scores"] = confidenceScores,
            ["schemaVersion"] = "lr-ontology-confidence-diagnostics/1.0.0",
            ["semanticAccuracyClaimed"] = false,
        });
        files["audit.json"] = auditArtifact.ToCanonicalBytes();
        files["validation-report.json"] = auditArtifact.ToCanonicalBytes();
        var executionLog = string.Join('\n',
            $"status={auditArtifact.Status}",
            $"familyId={family.FamilyId}",
            $"documentFingerprint={documentFingerprint}",
            $"snapshotId={compilation.Snapshot?.SnapshotId ?? "(not-published)"}",
            $"spanRecords={compilation.CurrentSpanRecords.Count}",
            $"candidateConcepts={compilation.Candidate?.Concepts.Count ?? 0}",
            $"unresolvedSpanCount={auditArtifact.UnresolvedSpanCount}",
            $"unresolvedOntologyItemCount={auditArtifact.UnresolvedOntologyItemCount}",
            string.Empty);
        files["execution.log"] = Encoding.UTF8.GetBytes(executionLog);
        await WriteImmutableBundleAsync(paths.VersionDirectory, files, cancellationToken).ConfigureAwait(false);
        return paths;
    }

    public async Task<CanonicalDocumentArtifact?> LoadCanonicalDocumentAsync(
        string familyId,
        string documentFingerprint,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var paths = GetPaths(familyId, documentFingerprint, versionId);
        if (!File.Exists(paths.CanonicalDocumentPath)) return null;
        var json = await File.ReadAllTextAsync(paths.CanonicalDocumentPath, cancellationToken).ConfigureAwait(false);
        var artifact = CanonicalDocumentArtifact.ParseStrict(json);
        if (artifact.FamilyId != familyId || artifact.DocumentFingerprint != documentFingerprint)
            throw new OntologyArtifactValidationException("Canonical document artifact path does not match its declared identity.");
        return artifact;
    }

    public async Task<IReadOnlyList<SpanVersionRecord>?> LoadSpanRecordsAsync(
        string familyId,
        string documentFingerprint,
        string versionId,
        CancellationToken cancellationToken = default)
        => (await LoadCanonicalDocumentAsync(familyId, documentFingerprint, versionId, cancellationToken)
                .ConfigureAwait(false))?.CreateSpanRecords();

    public async Task<OntologySnapshot?> LoadSnapshotAsync(
        string familyId,
        string documentFingerprint,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var paths = GetPaths(familyId, documentFingerprint, snapshotId);
        if (!File.Exists(paths.AcceptedOntologyPath)) return null;
        var json = await File.ReadAllTextAsync(paths.AcceptedOntologyPath, cancellationToken).ConfigureAwait(false);
        var snapshot = OntologySnapshot.ParseStrict(json);
        if (snapshot.DocumentFamilyId != familyId || snapshot.SourceDocumentFingerprint != documentFingerprint
            || snapshot.SnapshotId != snapshotId)
            throw new OntologyArtifactValidationException("Accepted snapshot path does not match its declared identity.");
        return snapshot;
    }

    public async Task<CandidateOntologyDocument?> LoadCandidateAsync(
        string familyId,
        string documentFingerprint,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var paths = GetPaths(familyId, documentFingerprint, snapshotId);
        if (!File.Exists(paths.CandidateOntologyPath)) return null;
        var json = await File.ReadAllTextAsync(paths.CandidateOntologyPath, cancellationToken).ConfigureAwait(false);
        var candidate = CandidateOntologyDocument.ParseStrict(json);
        if (candidate.SourceDocumentFingerprint != documentFingerprint)
            throw new OntologyArtifactValidationException("Candidate artifact path does not match its source fingerprint.");
        return candidate;
    }

    public async Task<OntologyDelta?> LoadDeltaAsync(
        string familyId,
        string documentFingerprint,
        string snapshotId,
        CancellationToken cancellationToken = default)
    {
        var paths = GetPaths(familyId, documentFingerprint, snapshotId);
        if (!File.Exists(paths.OntologyDeltaPath)) return null;
        var json = await File.ReadAllTextAsync(paths.OntologyDeltaPath, cancellationToken).ConfigureAwait(false);
        var delta = OntologyDelta.ParseStrict(json);
        if (delta.TargetDocumentFingerprint != documentFingerprint)
            throw new OntologyArtifactValidationException("Delta artifact path does not match its target fingerprint.");
        return delta;
    }

    private static async Task WriteImmutableAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Artifact path has no parent directory.");
        Directory.CreateDirectory(directory);
        if (File.Exists(path))
        {
            var existingBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!existingBytes.AsSpan().SequenceEqual(bytes))
                throw new OntologyArtifactValidationException($"Immutable artifact already exists with different bytes: {path}");
            return;
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            try
            {
                File.Move(temporaryPath, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                var existingBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                if (!existingBytes.AsSpan().SequenceEqual(bytes))
                    throw new OntologyArtifactValidationException($"Immutable artifact already exists with different bytes: {path}");
            }
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static async Task WriteImmutableBundleAsync(
        string directoryPath,
        IReadOnlyDictionary<string, byte[]> files,
        CancellationToken cancellationToken)
    {
        var parentDirectory = Path.GetDirectoryName(directoryPath)
            ?? throw new InvalidOperationException("Artifact bundle path has no parent directory.");
        Directory.CreateDirectory(parentDirectory);

        if (Directory.Exists(directoryPath))
        {
            await VerifyBundleMatchesAsync(directoryPath, files, cancellationToken).ConfigureAwait(false);
            return;
        }

        var temporaryDirectory = directoryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            foreach (var (fileName, bytes) in files.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await File.WriteAllBytesAsync(Path.Combine(temporaryDirectory, fileName), bytes, cancellationToken)
                    .ConfigureAwait(false);
            }

            try
            {
                Directory.Move(temporaryDirectory, directoryPath);
            }
            catch (IOException) when (Directory.Exists(directoryPath))
            {
                await VerifyBundleMatchesAsync(directoryPath, files, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (Directory.Exists(temporaryDirectory))
                Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    private static async Task VerifyBundleMatchesAsync(
        string directoryPath,
        IReadOnlyDictionary<string, byte[]> files,
        CancellationToken cancellationToken)
    {
        foreach (var (fileName, expectedBytes) in files)
        {
            var path = Path.Combine(directoryPath, fileName);
            if (!File.Exists(path))
                throw new OntologyArtifactValidationException(
                    $"Immutable artifact bundle is incomplete; missing {path}.");
            var actualBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            if (!actualBytes.AsSpan().SequenceEqual(expectedBytes))
                throw new OntologyArtifactValidationException(
                    $"Immutable artifact bundle already exists with different bytes: {path}");
        }
    }

    private static string Slug(string hash)
    {
        var hex = hash.StartsWith(ContentHash.Prefix, StringComparison.Ordinal)
            ? hash[ContentHash.Prefix.Length..]
            : hash;
        return hex.Length > 32 ? hex[..32] : hex;
    }

    private string FamilyManifestPath(string familyId)
        => Path.Combine(_rootDirectory, Slug(OntologyArtifactValidator.RequiredHash(familyId, "familyId")), "family-manifest.json");
}