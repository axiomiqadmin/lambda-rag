using System.Text.Json.Nodes;

namespace LambdaRag.Core.Ontology;

/// <summary>Deterministic classification of a span across two versions.</summary>
public enum SpanClassification
{
    Unchanged,
    MovedWithinHeading,
    MovedCrossHeading,
    Added,
    Removed,
    UnresolvedReplacement,
    SplitFrom,
    MergedFrom,
    Unresolved,
}

/// <summary>
/// Duplicate-handling policy. Strict is the default and fails closed on
/// indistinguishable duplicates. PositionalEquivalent is explicit, opt-in, and
/// may assign exact-content duplicates by position with a diagnostic.
/// </summary>
public enum SpanReconciliationPolicy
{
    Strict,
    PositionalEquivalent,
}

/// <summary>Relationship between span occurrences (split/merge provenance).</summary>
public enum SpanRelationshipType
{
    SplitFrom,
    SplitInto,
    MergedFrom,
    MergedInto,
}

public sealed record SpanRelationship(
    SpanRelationshipType Type,
    string From,
    string To)
{
    public JsonObject ToCanonicalJson() => new()
    {
        ["from"] = From,
        ["to"] = To,
        ["type"] = Type.ToString(),
    };
}

/// <summary>
/// One reconciliation outcome. Every previous and every current span is the
/// subject of exactly one result; no span is silently dropped.
/// </summary>
public sealed record SpanReconciliationResult(
    SpanClassification Classification,
    SpanInstanceId? PreviousInstanceId,
    SpanInstanceId? ResultingInstanceId,
    SpanVersionIdentity? PreviousVersionIdentity,
    SpanVersionIdentity? CurrentVersionIdentity,
    int? PreviousOrdinal,
    int? CurrentOrdinal,
    string ReasonCode,
    IReadOnlyList<string> Diagnostics,
    IReadOnlyList<SpanRelationship> Relationships)
{
    public JsonObject ToCanonicalJson()
    {
        var diag = new JsonArray();
        foreach (var d in Diagnostics.OrderBy(x => x, StringComparer.Ordinal))
            diag.Add(d);

        var rels = new JsonArray();
        foreach (var r in Relationships
                     .OrderBy(x => x.Type.ToString(), StringComparer.Ordinal)
                     .ThenBy(x => x.From, StringComparer.Ordinal)
                     .ThenBy(x => x.To, StringComparer.Ordinal))
            rels.Add(r.ToCanonicalJson());

        return new JsonObject
        {
            ["classification"] = Classification.ToString(),
            ["currentOrdinal"] = CurrentOrdinal,
            ["currentVersionLabel"] = CurrentVersionIdentity?.VersionLabel,
            ["diagnostics"] = diag,
            ["previousInstanceId"] = PreviousInstanceId?.Value,
            ["previousOrdinal"] = PreviousOrdinal,
            ["previousVersionLabel"] = PreviousVersionIdentity?.VersionLabel,
            ["reasonCode"] = ReasonCode,
            ["relationships"] = rels,
            ["resultingInstanceId"] = ResultingInstanceId?.Value,
        };
    }
}

/// <summary>
/// Full reconciliation output: deterministic, order-independent canonical
/// serialisation, plus the accepted current-version records with assigned ids.
/// </summary>
public sealed record SpanReconciliationResultSet(
    SpanVersionIdentity? PreviousIdentity,
    SpanVersionIdentity CurrentIdentity,
    SpanReconciliationPolicy Policy,
    IReadOnlyList<SpanReconciliationResult> Results,
    IReadOnlyList<SpanVersionRecord> CurrentRecords)
{
    public bool HasUnresolved => Results.Any(r =>
        r.Classification is SpanClassification.Unresolved or SpanClassification.UnresolvedReplacement);

    public JsonObject ToCanonicalJson()
    {
        var ordered = Results
            .OrderBy(r => r.CurrentOrdinal ?? int.MaxValue)
            .ThenBy(r => r.PreviousOrdinal ?? int.MaxValue)
            .ThenBy(r => r.Classification.ToString(), StringComparer.Ordinal)
            .ThenBy(r => r.ReasonCode, StringComparer.Ordinal);

        var arr = new JsonArray();
        foreach (var r in ordered)
            arr.Add(r.ToCanonicalJson());

        return new JsonObject
        {
            ["currentVersionLabel"] = CurrentIdentity.VersionLabel,
            ["familyId"] = CurrentIdentity.FamilyId,
            ["policy"] = Policy.ToString(),
            ["previousVersionLabel"] = PreviousIdentity?.VersionLabel,
            ["results"] = arr,
            ["schema"] = "lr-span-reconciliation/1.0.0",
        };
    }

    public byte[] ToCanonicalBytes() => OntologyCanonicalJson.SerializeToUtf8(ToCanonicalJson());
}
