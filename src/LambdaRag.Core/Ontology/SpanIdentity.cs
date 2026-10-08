namespace LambdaRag.Core.Ontology;

/// <summary>
/// Fingerprint of a span's canonical text ONLY. Offsets, page numbers,
/// heading ancestry and neighbours are deliberately excluded: two spans with
/// identical text intentionally collide here. NFC/NFD variants collide too.
/// </summary>
public readonly record struct SpanContentFingerprint(string Value)
{
    public static SpanContentFingerprint FromText(string text)
    {
        var fp = new CanonicalFingerprint()
            .Tag("lr-span-content/1")
            .Tag("text").Text(text)
            .Build();
        return new SpanContentFingerprint(fp.Value);
    }

    public override string ToString() => Value;
}

/// <summary>
/// Durable identity of a span OCCURRENCE across document versions. Minted
/// deterministically but carried forward only when the reconciler proves
/// continuity. The value is an opaque token; its derivation must never be
/// used as cross-version identity evidence.
/// </summary>
public readonly record struct SpanInstanceId(string Value)
{
    public static SpanInstanceId Mint(SpanVersionIdentity identity, int ordinal)
    {
        var fp = new CanonicalFingerprint()
            .Tag("lr-span-instance/1")
            .Tag("family").Text(identity.FamilyId)
            .Tag("version").Text(identity.VersionLabel)
            .Tag("parserId").Text(identity.ParserId)
            .Tag("parserVersion").Text(identity.ParserVersion)
            .Tag("canon").Text(identity.CanonicalisationVersion)
            .Tag("ordinal").Int(ordinal)
            .Build();
        return new SpanInstanceId("si:" + fp.Value);
    }

    public override string ToString() => Value;
}

/// <summary>
/// Identity of the version a set of spans belongs to. Parser and
/// canonicalisation versions participate so that incompatible producers are
/// rejected rather than silently reconciled.
/// </summary>
public sealed record SpanVersionIdentity(
    string FamilyId,
    string VersionLabel,
    string ParserId,
    string ParserVersion,
    string CanonicalisationVersion)
{
    public static SpanVersionIdentity Create(
        string familyId,
        string versionLabel,
        string parserId,
        string parserVersion)
        => new(
            OntologyCanonical.NormalizeRequired(familyId, nameof(familyId)),
            OntologyCanonical.NormalizeRequired(versionLabel, nameof(versionLabel)),
            OntologyCanonical.NormalizeRequired(parserId, nameof(parserId)),
            OntologyCanonical.NormalizeRequired(parserVersion, nameof(parserVersion)),
            OntologyCanonical.CanonicalisationVersion);
}

/// <summary>
/// Within-version structural context of a span. Uses heading-ordinal ancestry
/// plus neighbour CONTENT fingerprints — never absolute slot index — so that
/// indistinguishable duplicates genuinely collide (and must fail closed under
/// the Strict policy rather than being told apart by position).
/// </summary>
public sealed record SpanStructuralContextKey(
    SpanKind Kind,
    string AncestryPath,
    string? PrevSiblingContentFingerprint,
    string? NextSiblingContentFingerprint)
{
    public string Fingerprint()
        => new CanonicalFingerprint()
            .Tag("lr-span-struct/1")
            .Tag("kind").Text(Kind.ToString())
            .Tag("ancestry").Text(AncestryPath)
            .Tag("prevPresent").Int(PrevSiblingContentFingerprint is null ? 0 : 1)
            .Tag("prev").Text(PrevSiblingContentFingerprint ?? string.Empty)
            .Tag("nextPresent").Int(NextSiblingContentFingerprint is null ? 0 : 1)
            .Tag("next").Text(NextSiblingContentFingerprint ?? string.Empty)
            .Build().Value;
}
