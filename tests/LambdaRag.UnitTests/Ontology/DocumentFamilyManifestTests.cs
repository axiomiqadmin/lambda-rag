using FluentAssertions;
using LambdaRag.Core.Ontology;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class DocumentFamilyManifestTests
{
    [Fact]
    public void Same_inputs_yield_identical_family_id_and_bytes()
    {
        var a = DocumentFamilyManifest.Register("ns", "reg", "salt");
        var b = DocumentFamilyManifest.Register("ns", "reg", "salt");
        a.FamilyId.Should().Be(b.FamilyId);
        a.ToCanonicalBytes().Should().Equal(b.ToCanonicalBytes());
    }

    [Fact]
    public void Family_id_changes_only_with_its_declared_inputs()
    {
        var baseline = DocumentFamilyManifest.Register("ns", "reg", "salt");
        DocumentFamilyManifest.Register("ns2", "reg", "salt").FamilyId.Should().NotBe(baseline.FamilyId);
        DocumentFamilyManifest.Register("ns", "reg2", "salt").FamilyId.Should().NotBe(baseline.FamilyId);
        DocumentFamilyManifest.Register("ns", "reg", "salt2").FamilyId.Should().NotBe(baseline.FamilyId);
    }

    [Fact]
    public void Family_id_is_delimiter_injection_resistant()
    {
        var left = DocumentFamilyManifest.Register("ab", "c", "d");
        var right = DocumentFamilyManifest.Register("a", "bc", "d");
        left.FamilyId.Should().NotBe(right.FamilyId);
    }

    [Fact]
    public void Register_rejects_control_characters_in_inputs()
    {
        Action act = () => DocumentFamilyManifest.Register("a\u001fb", "reg", "salt");
        act.Should().Throw<OntologyCanonicalException>();
    }

    [Fact]
    public void Registry_is_idempotent_for_identical_manifests()
    {
        var registry = new DocumentFamilyRegistry();
        var m = DocumentFamilyManifest.Register("ns", "reg", "salt");
        registry.Register(m);
        Action act = () => registry.Register(DocumentFamilyManifest.Register("ns", "reg", "salt"));
        act.Should().NotThrow();
    }

    [Fact]
    public void Registry_fails_closed_on_colliding_id_with_non_identical_manifest()
    {
        var registry = new DocumentFamilyRegistry();
        var valid = DocumentFamilyManifest.Register("ns", "reg", "salt");
        registry.Register(valid);

        // Tampered manifest reuses the id but carries different inputs.
        var tampered = DocumentFamilyManifest.CreateUnchecked(valid.FamilyId, "ns", "reg", "salt-DIFFERENT");
        Action act = () => registry.Register(tampered);
        act.Should().Throw<DocumentFamilyIntegrityException>();
    }

    [Fact]
    public void Manifest_canonical_bytes_replay_is_identical()
    {
        var m = DocumentFamilyManifest.Register("ns", "reg", "salt");
        m.ToCanonicalBytes().Should().Equal(m.ToCanonicalBytes());
    }
}
