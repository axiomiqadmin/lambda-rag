using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Core.Ontology;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class OntologyCanonicalTests
{
    [Fact]
    public void Normalize_rejects_control_characters()
    {
        Action act = () => OntologyCanonical.Normalize("a\u001fb");
        act.Should().Throw<OntologyCanonicalException>();
    }

    [Fact]
    public void Normalize_rejects_bell_character()
    {
        Action act = () => OntologyCanonical.Normalize("ring\u0007");
        act.Should().Throw<OntologyCanonicalException>();
    }

    [Fact]
    public void SpanContentFingerprint_is_equal_for_NFC_and_NFD_text()
    {
        var nfc = SpanContentFingerprint.FromText("caf\u00e9");        // é precomposed
        var nfd = SpanContentFingerprint.FromText("cafe\u0301");       // e + combining acute
        nfc.Should().Be(nfd);
    }

    [Fact]
    public void CanonicalFingerprint_is_resistant_to_delimiter_injection()
    {
        // Field boundaries are length-prefixed, so ("ab","c") cannot collide with ("a","bc").
        var left = new CanonicalFingerprint().Tag("f").Text("ab").Text("c").Build();
        var right = new CanonicalFingerprint().Tag("f").Text("a").Text("bc").Build();
        left.Should().NotBe(right);
    }

    [Fact]
    public void CanonicalJson_produces_byte_identical_output_regardless_of_key_insertion_order()
    {
        var a = new JsonObject { ["b"] = 1, ["a"] = "x", ["c"] = true };
        var b = new JsonObject { ["c"] = true, ["a"] = "x", ["b"] = 1 };
        OntologyCanonicalJson.SerializeToUtf8(a).Should().Equal(OntologyCanonicalJson.SerializeToUtf8(b));
    }

    [Fact]
    public void CanonicalJson_replay_is_byte_identical()
    {
        var obj = new JsonObject { ["a"] = "x", ["n"] = 42 };
        OntologyCanonicalJson.SerializeToUtf8(obj).Should().Equal(OntologyCanonicalJson.SerializeToUtf8(obj));
    }

    [Fact]
    public void Relationship_array_order_does_not_change_canonical_bytes()
    {
        var r1 = new SpanRelationship(SpanRelationshipType.SplitInto, "si:a", "si:b");
        var r2 = new SpanRelationship(SpanRelationshipType.SplitInto, "si:a", "si:c");

        var forward = Result(new[] { r1, r2 });
        var reversed = Result(new[] { r2, r1 });

        OntologyCanonicalJson.SerializeToUtf8(forward.ToCanonicalJson())
            .Should().Equal(OntologyCanonicalJson.SerializeToUtf8(reversed.ToCanonicalJson()));

        static SpanReconciliationResult Result(IReadOnlyList<SpanRelationship> rels) => new(
            SpanClassification.Removed, new SpanInstanceId("si:a"), null, null, null,
            0, null, "retired-by-split", Array.Empty<string>(), rels);
    }
}
