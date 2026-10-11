using System.Text;
using System.Text.Json.Nodes;
using LambdaRag.Core.Abstractions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using LambdaRag.Parsing;

namespace LambdaRag.Cli;

public static class OntologyCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            PrintHelp();
            return 64;
        }

        try
        {
            return args[0] switch
            {
                "compile" => await CompileAsync(args.Skip(1).ToArray(), replayOnly: false).ConfigureAwait(false),
                "inspect" => Inspect(args.Skip(1).ToArray()),
                "diff" => Diff(args.Skip(1).ToArray()),
                "validate" => Validate(args.Skip(1).ToArray()),
                "replay" => await CompileAsync(args.Skip(1).ToArray(), replayOnly: true).ConfigureAwait(false),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"ontology error: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> CompileAsync(string[] args, bool replayOnly)
    {
        var flags = ParseFlags(args);
        var documentPath = Required(flags, "document");
        var ns = Required(flags, "namespace");
        var registrant = Required(flags, "registrant");
        var salt = Required(flags, "salt");
        var versionLabel = flags.GetValueOrDefault("version") ?? "v1";
        var targetSnapshotId = flags.GetValueOrDefault("snapshot-id") ?? $"snapshot-{versionLabel}";
        var ontologyId = flags.GetValueOrDefault("ontology-id") ?? ns;
        var pipelineVersion = flags.GetValueOrDefault("pipeline-version") ?? "dynamic-ontology-v1/1.0.0";
        var extractionContractVersion = flags.GetValueOrDefault("extraction-contract-version") ?? "dynamic-document-ontology-v1";
        var artifactsRoot = flags.GetValueOrDefault("artifacts") ?? FileOntologyArtifactStore.DefaultRootDirectoryName;
        var family = DocumentFamilyManifest.Register(ns, registrant, salt);
        var store = new FileOntologyArtifactStore(artifactsRoot);
        await store.SaveFamilyManifestAsync(family).ConfigureAwait(false);

        var parser = new ParserRegistry(new IDocumentParser[]
        {
            new PdfParser(),
            new DocxParser(),
            new MarkdownParser(),
        });
        var parsed = await parser.ParseAsync(documentPath).ConfigureAwait(false);

        OntologySnapshot? previousSnapshot = null;
        IReadOnlyList<SpanVersionRecord>? previousSpanRecords = null;
        var parentSnapshotId = flags.GetValueOrDefault("parent");
        if (parentSnapshotId is not null)
        {
            previousSnapshot = await store.FindSnapshotByIdAsync(family.FamilyId, parentSnapshotId).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"Parent snapshot '{parentSnapshotId}' was not found in the artifact store.");
            previousSpanRecords = await store.LoadSpanRecordsAsync(
                family.FamilyId,
                previousSnapshot.SourceDocumentFingerprint,
                previousSnapshot.SnapshotId).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"Span records for parent snapshot '{parentSnapshotId}' were not found.");
        }

        var fakeRequested = args.Contains("--fake-generator", StringComparer.Ordinal);
        if (replayOnly && fakeRequested)
            throw new ArgumentException("ontology replay cannot use --fake-generator.");
        var generatorIdentity = fakeRequested
            ? DeterministicSpanCandidateGenerator.GeneratorId
            : flags.GetValueOrDefault("generator-id") ?? "candidate-generator/1.0.0";
        var candidateCache = new FileCandidateOntologyCache(
            Path.Combine(artifactsRoot, "cache", "candidate-ontology"));
        ICandidateOntologyGenerator generator = replayOnly || !fakeRequested
            ? new ReplayCandidateOntologyGenerator(candidateCache)
            : new CachingCandidateOntologyGenerator(new DeterministicSpanCandidateGenerator(), candidateCache);
        var compiler = new OntologyCompilationService(generator);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["generatorIdentity"] = generatorIdentity,
        };
        if (flags.TryGetValue("prompt-version", out var promptVersion))
            settings["promptVersion"] = promptVersion;

        var result = await compiler.CompileAsync(
            parsed,
            family,
            ontologyId,
            versionLabel,
            targetSnapshotId,
            pipelineVersion,
            extractionContractVersion,
            previousSnapshot,
            previousSpanRecords,
            settings).ConfigureAwait(false);

        var versionIdentity = result.CurrentSpanRecords.Count > 0
            ? result.CurrentSpanRecords[0].VersionIdentity
            : ParsedDocumentSpanAdapter.Adapt(parsed).CreateVersionIdentity(family.FamilyId, versionLabel);
        var paths = result.ReusedExistingSnapshot && result.Snapshot is not null
            ? store.GetPaths(family.FamilyId, parsed.Source.Id.Value, result.Snapshot.SnapshotId)
            : await store.SaveCompilationAsync(
                family, parsed, versionIdentity, targetSnapshotId, result).ConfigureAwait(false);

        Console.WriteLine($"Family:       {family.FamilyId}");
        Console.WriteLine($"Document:     {parsed.Source.Id.Value}");
        Console.WriteLine($"Snapshot:     {result.Snapshot?.SnapshotId ?? "blocked"}");
        Console.WriteLine($"Reused:       {result.ReusedExistingSnapshot}");
        Console.WriteLine($"Generator:    {(fakeRequested ? "deterministic fake (not semantic extraction)" : "cache/replay only")}");
        Console.WriteLine($"Artifacts:    {paths.VersionDirectory}");
        if (result.SpanReconciliation?.HasUnresolved == true)
            Console.WriteLine("Blocked:      span reconciliation contains unresolved identities.");
        if (result.Delta?.UnresolvedItems.Count > 0)
            Console.WriteLine($"Unresolved:   {result.Delta.UnresolvedItems.Count}");
        return result.CanPublish ? 0 : 2;
    }

    private static int Inspect(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("usage: lambda-rag ontology inspect <accepted-snapshot.json>");
        var snapshot = OntologySnapshot.ParseStrict(File.ReadAllText(args[0]));
        Console.WriteLine($"Ontology:     {snapshot.OntologyId}");
        Console.WriteLine($"Family:       {snapshot.DocumentFamilyId}");
        Console.WriteLine($"Snapshot:     {snapshot.SnapshotId}");
        Console.WriteLine($"Document:     {snapshot.SourceDocumentFingerprint}");
        Console.WriteLine($"Fingerprint:  {snapshot.SnapshotFingerprint}");
        Console.WriteLine($"Parent:       {snapshot.ParentSnapshotId ?? "(none)"}");
        Console.WriteLine($"Concepts:     {snapshot.Concepts.Count}");
        foreach (var concept in snapshot.Concepts.OrderBy(c => c.ConceptId, StringComparer.Ordinal))
            Console.WriteLine($"  {concept.Status,-7} {concept.ConceptId}  {concept.CanonicalLabel} [{concept.ConceptType}] evidence={concept.Provenance.Count}");
        Console.WriteLine($"Relationships: {snapshot.Relationships.Count}");
        foreach (var relationship in snapshot.Relationships.OrderBy(r => r.RelationshipId, StringComparer.Ordinal))
            Console.WriteLine($"  {relationship.Status,-7} {relationship.RelationshipType}: {relationship.SourceConceptId} -> {relationship.TargetConceptId}");
        Console.WriteLine($"Unresolved:   {snapshot.UnresolvedItems.Count}");
        foreach (var item in snapshot.UnresolvedItems.OrderBy(i => i.ItemType, StringComparer.Ordinal).ThenBy(i => i.ItemKey, StringComparer.Ordinal))
            Console.WriteLine($"  {item.ItemType} {item.ItemKey}: {item.ReasonCode}");
        return 0;
    }

    private static int Diff(string[] args)
    {
        if (args.Length != 2)
            throw new ArgumentException("usage: lambda-rag ontology diff <old-snapshot.json> <new-snapshot.json>");
        var before = OntologySnapshot.ParseStrict(File.ReadAllText(args[0]));
        var after = OntologySnapshot.ParseStrict(File.ReadAllText(args[1]));
        if (before.OntologyId != after.OntologyId || before.DocumentFamilyId != after.DocumentFamilyId)
            throw new OntologyArtifactValidationException("Snapshots must belong to the same ontology and document family.");

        var oldConcepts = before.Concepts.ToDictionary(concept => concept.ConceptId, StringComparer.Ordinal);
        var newConcepts = after.Concepts.ToDictionary(concept => concept.ConceptId, StringComparer.Ordinal);
        var added = newConcepts.Keys.Except(oldConcepts.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var retired = newConcepts.Values
            .Where(concept => concept.Status == OntologyArtifactStatus.Retired
                              && oldConcepts.TryGetValue(concept.ConceptId, out var old)
                              && old.Status != OntologyArtifactStatus.Retired)
            .Select(concept => concept.ConceptId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var augmented = newConcepts.Values
            .Where(concept => oldConcepts.TryGetValue(concept.ConceptId, out var old)
                              && old.ContentFingerprint != concept.ContentFingerprint)
            .Select(concept => concept.ConceptId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var aliasChanges = new JsonArray();
        foreach (var conceptId in oldConcepts.Keys.Intersect(newConcepts.Keys, StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            var beforeAliases = oldConcepts[conceptId].Aliases.ToHashSet(StringComparer.Ordinal);
            var afterAliases = newConcepts[conceptId].Aliases.ToHashSet(StringComparer.Ordinal);
            foreach (var alias in afterAliases.Except(beforeAliases, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                aliasChanges.Add(new JsonObject { ["changeKind"] = "add", ["conceptId"] = conceptId, ["alias"] = alias });
            foreach (var alias in beforeAliases.Except(afterAliases, StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
                aliasChanges.Add(new JsonObject { ["changeKind"] = "remove", ["conceptId"] = conceptId, ["alias"] = alias });
        }
        var oldRelationships = before.Relationships.ToDictionary(relationship => relationship.RelationshipId, StringComparer.Ordinal);
        var newRelationships = after.Relationships.ToDictionary(relationship => relationship.RelationshipId, StringComparer.Ordinal);
        var relationshipAdded = newRelationships.Keys.Except(oldRelationships.Keys, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        var relationshipRetired = newRelationships.Values
            .Where(relationship => relationship.Status == OntologyArtifactStatus.Retired
                                   && oldRelationships.TryGetValue(relationship.RelationshipId, out var old)
                                   && old.Status != OntologyArtifactStatus.Retired)
            .Select(relationship => relationship.RelationshipId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var relationshipAugmented = newRelationships.Values
            .Where(relationship => oldRelationships.TryGetValue(relationship.RelationshipId, out var old)
                                   && old.ContentFingerprint != relationship.ContentFingerprint
                                   && relationship.Status == OntologyArtifactStatus.Active)
            .Select(relationship => relationship.RelationshipId)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        var output = new JsonObject
        {
            ["addedConcepts"] = StringArray(added),
            ["aliasChanges"] = aliasChanges,
            ["augmentedConcepts"] = StringArray(augmented),
            ["fromSnapshotId"] = before.SnapshotId,
            ["fromSnapshotFingerprint"] = before.SnapshotFingerprint,
            ["retiredConcepts"] = StringArray(retired),
            ["addedRelationships"] = StringArray(relationshipAdded),
            ["augmentedRelationships"] = StringArray(relationshipAugmented),
            ["retiredRelationships"] = StringArray(relationshipRetired),
            ["toSnapshotId"] = after.SnapshotId,
            ["toSnapshotFingerprint"] = after.SnapshotFingerprint,
        };
        Console.WriteLine(OntologyCanonicalJson.Serialize(output));
        return 0;
    }

    private static int Validate(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("usage: lambda-rag ontology validate <artifact.json>");
        var json = File.ReadAllText(args[0]);
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new OntologyArtifactValidationException("Artifact root must be a JSON object.");
        var schemaVersion = root["schemaVersion"]?.GetValue<string>();
        var type = schemaVersion switch
        {
            CandidateOntologyDocument.CurrentSchemaVersion => "candidate ontology",
            OntologySnapshot.CurrentSchemaVersion => "accepted snapshot",
            OntologyDelta.CurrentSchemaVersion => "ontology delta",
            CanonicalDocumentArtifact.CurrentSchemaVersion => "canonical document",
            OntologyAuditArtifact.CurrentSchemaVersion => "ontology audit",
            DocumentFamilyManifest.SchemaVersion => "document family manifest",
            _ => throw new OntologyArtifactValidationException($"Unknown ontology artifact schema '{schemaVersion ?? "(missing)"}'."),
        };

        switch (schemaVersion)
        {
            case CandidateOntologyDocument.CurrentSchemaVersion:
                _ = CandidateOntologyDocument.ParseStrict(json);
                break;
            case OntologySnapshot.CurrentSchemaVersion:
                _ = OntologySnapshot.ParseStrict(json);
                break;
            case OntologyDelta.CurrentSchemaVersion:
                _ = OntologyDelta.ParseStrict(json);
                break;
            case CanonicalDocumentArtifact.CurrentSchemaVersion:
                _ = CanonicalDocumentArtifact.ParseStrict(json);
                break;
            case OntologyAuditArtifact.CurrentSchemaVersion:
                _ = OntologyAuditArtifact.ParseStrict(json);
                break;
            case DocumentFamilyManifest.SchemaVersion:
                _ = DocumentFamilyManifest.ParseStrict(json);
                break;
        }
        Console.WriteLine($"Valid: {type}");
        return 0;
    }

    private static JsonArray StringArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add(value);
        return array;
    }

    private static Dictionary<string, string> ParseFlags(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal)
                && i + 1 < args.Length
                && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                result[args[i][2..]] = args[++i].Trim();
            }
        }
        return result;
    }

    private static string Required(IReadOnlyDictionary<string, string> flags, string key)
        => flags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new ArgumentException($"--{key} is required.");

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown ontology subcommand: {command}");
        PrintHelp();
        return 64;
    }

    private static void PrintHelp() => Console.WriteLine("""
        Usage:
          lambda-rag ontology compile --document <path> --namespace <id> --registrant <id> --salt <value>
                                      [--version v1] [--snapshot-id id] [--parent id] [--artifacts dir]
                                      [--fake-generator]
          lambda-rag ontology inspect <accepted-snapshot.json>
          lambda-rag ontology diff <old-snapshot.json> <new-snapshot.json>
          lambda-rag ontology validate <artifact.json>
          lambda-rag ontology replay --document <path> --namespace <id> --registrant <id> --salt <value>
                                     [--version v1] [--snapshot-id id] [--parent id] [--artifacts dir]

        `compile` uses validated cache replay by default. `--fake-generator` is a deterministic
        offline demonstration only; it does not perform semantic extraction. A live provider adapter
        is not configured by this build.
        """);

}