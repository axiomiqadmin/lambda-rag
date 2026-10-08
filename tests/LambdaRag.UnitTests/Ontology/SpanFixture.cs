using LambdaRag.Core.Ontology;

namespace LambdaRag.UnitTests.Ontology;

/// <summary>
/// Deterministic fixture generator for the span-identity proof. Produces raw
/// spans with realistic (but identity-irrelevant) provenance offsets, and
/// convenience helpers to seed a previous version and observe a current one.
/// </summary>
internal static class SpanFixture
{
    public const string ParserId = "proof-parser";

    public static readonly string FamilyId =
        DocumentFamilyManifest.Register("proof.ns", "proof-registrant", "salt-1").FamilyId;

    public static SpanVersionIdentity Identity(string versionLabel, string parserVersion = "1.0.0")
        => SpanVersionIdentity.Create(FamilyId, versionLabel, ParserId, parserVersion);

    public sealed record Node(SpanKind Kind, int Level, string Text);

    public static Node H(int level, string text) => new(SpanKind.Heading, level, text);

    public static Node B(string text) => new(SpanKind.Body, 0, text);

    /// <summary>Build raw spans, assigning sequential provenance offsets (which must not affect identity).</summary>
    public static IReadOnlyList<RawSpan> Doc(params Node[] nodes)
    {
        var list = new List<RawSpan>(nodes.Length);
        var offset = 0;
        foreach (var n in nodes)
        {
            var len = n.Text.Length;
            list.Add(new RawSpan(n.Kind, n.Level, n.Text, offset, len));
            offset += len + 1;
        }
        return list;
    }

    /// <summary>Like <see cref="Doc"/> but with a different provenance base so offsets differ for equal content.</summary>
    public static IReadOnlyList<RawSpan> DocShifted(params Node[] nodes)
    {
        var list = new List<RawSpan>(nodes.Length);
        var offset = 1000;
        foreach (var n in nodes)
        {
            var len = n.Text.Length;
            list.Add(new RawSpan(n.Kind, n.Level, n.Text, offset, len, PageNumber: 7));
            offset += len + 3;
        }
        return list;
    }

    public static IReadOnlyList<SpanObservation> Observe(IReadOnlyList<RawSpan> raw)
        => SpanObservationBuilder.Build(raw);

    public static IReadOnlyList<SpanVersionRecord> Seed(IReadOnlyList<RawSpan> raw, SpanVersionIdentity identity)
        => new DeterministicSpanReconciler().Seed(Observe(raw), identity);
}
