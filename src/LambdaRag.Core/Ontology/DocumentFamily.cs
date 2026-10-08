using System.Text.Json.Nodes;

namespace LambdaRag.Core.Ontology;

/// <summary>Thrown when a manifest's declared id does not match its inputs.</summary>
public sealed class DocumentFamilyIntegrityException : Exception
{
    public DocumentFamilyIntegrityException(string familyId)
        : base($"Document family manifest integrity check failed for '{familyId}'.") { }
}

/// <summary>Thrown when two non-identical manifests claim the same family id.</summary>
public sealed class DocumentFamilyConflictException : Exception
{
    public DocumentFamilyConflictException(string familyId)
        : base($"Conflicting document family manifest registered for id '{familyId}'.") { }
}

/// <summary>
/// Immutable, canonically serialisable document-family identity. The
/// <see cref="FamilyId"/> depends ONLY on caller-supplied namespace,
/// registrant and salt (NFC-normalised, control-rejected, length-prefixed) —
/// never on filename, path, title, mutable content, timestamps or an
/// LLM-generated value.
/// </summary>
public sealed record DocumentFamilyManifest
{
    public const string SchemaVersion = "lr-family-manifest/1.0.0";

    public string FamilyId { get; }
    public string Namespace { get; }
    public string Registrant { get; }
    public string Salt { get; }
    public string CanonicalisationVersion { get; }
    public string ManifestFingerprint { get; }

    private DocumentFamilyManifest(
        string familyId, string ns, string registrant, string salt,
        string canonicalisationVersion, string manifestFingerprint)
    {
        FamilyId = familyId;
        Namespace = ns;
        Registrant = registrant;
        Salt = salt;
        CanonicalisationVersion = canonicalisationVersion;
        ManifestFingerprint = manifestFingerprint;
    }

    public static DocumentFamilyManifest Register(string @namespace, string registrant, string salt)
    {
        var ns = OntologyCanonical.NormalizeRequired(@namespace, nameof(@namespace));
        var reg = OntologyCanonical.NormalizeRequired(registrant, nameof(registrant));
        var slt = OntologyCanonical.NormalizeRequired(salt, nameof(salt));

        var familyId = ComputeFamilyId(ns, reg, slt);
        var fingerprint = ComputeManifestFingerprint(familyId, ns, reg, slt);
        return new DocumentFamilyManifest(
            familyId, ns, reg, slt, OntologyCanonical.CanonicalisationVersion, fingerprint);
    }

    /// <summary>Test-only constructor that bypasses recomputation, to exercise fail-closed paths.</summary>
    public static DocumentFamilyManifest CreateUnchecked(
        string familyId, string @namespace, string registrant, string salt)
        => new(
            familyId, @namespace, registrant, salt,
            OntologyCanonical.CanonicalisationVersion,
            ComputeManifestFingerprint(familyId, @namespace, registrant, salt));

    public bool IsIntegrityValid
        => FamilyId == ComputeFamilyId(Namespace, Registrant, Salt)
           && ManifestFingerprint == ComputeManifestFingerprint(FamilyId, Namespace, Registrant, Salt)
           && CanonicalisationVersion == OntologyCanonical.CanonicalisationVersion;

    private static string ComputeFamilyId(string ns, string registrant, string salt)
        => new CanonicalFingerprint()
            .Tag("lr-family/1")
            .Tag("ns").Text(ns)
            .Tag("registrant").Text(registrant)
            .Tag("salt").Text(salt)
            .Build().Value;

    private static string ComputeManifestFingerprint(string familyId, string ns, string registrant, string salt)
        => new CanonicalFingerprint()
            .Tag("lr-family-manifest/1")
            .Tag("schema").Text(SchemaVersion)
            .Tag("familyId").Text(familyId)
            .Tag("ns").Text(ns)
            .Tag("registrant").Text(registrant)
            .Tag("salt").Text(salt)
            .Tag("canon").Text(OntologyCanonical.CanonicalisationVersion)
            .Build().Value;

    public JsonObject ToCanonicalJson() => new()
    {
        ["canonicalisationVersion"] = CanonicalisationVersion,
        ["familyId"] = FamilyId,
        ["manifestFingerprint"] = ManifestFingerprint,
        ["namespace"] = Namespace,
        ["registrant"] = Registrant,
        ["salt"] = Salt,
        ["schemaVersion"] = SchemaVersion,
    };

    public byte[] ToCanonicalBytes() => OntologyCanonicalJson.SerializeToUtf8(ToCanonicalJson());
}

/// <summary>
/// In-memory registry that enforces family-id uniqueness and fails closed on
/// integrity violations or conflicting manifests sharing an id. Idempotent for
/// byte-identical re-registration.
/// </summary>
public sealed class DocumentFamilyRegistry
{
    private readonly Dictionary<string, byte[]> _byId = new(StringComparer.Ordinal);

    public DocumentFamilyManifest Register(DocumentFamilyManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.IsIntegrityValid)
            throw new DocumentFamilyIntegrityException(manifest.FamilyId);

        var bytes = manifest.ToCanonicalBytes();
        if (_byId.TryGetValue(manifest.FamilyId, out var existing))
        {
            if (!existing.AsSpan().SequenceEqual(bytes))
                throw new DocumentFamilyConflictException(manifest.FamilyId);
            return manifest;
        }

        _byId[manifest.FamilyId] = bytes;
        return manifest;
    }

    public bool Contains(string familyId) => _byId.ContainsKey(familyId);
}
