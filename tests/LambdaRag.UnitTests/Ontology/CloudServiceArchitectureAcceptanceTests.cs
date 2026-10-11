using System.IO.Compression;
using System.Text.Json;
using System.Xml.Linq;
using FluentAssertions;
using LambdaRag.Core.Ontology;
using LambdaRag.Parsing;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class CloudServiceArchitectureAcceptanceTests
{
    [Fact]
    public async Task Human_reference_citations_are_grounded_in_the_primary_docx()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        using var reference = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            root, "tests", "Goldens", "cloud-service-architecture-ontology-reference.json")));
        var fixture = reference.RootElement;
        var parsed = await new DocxParser().ParseAsync(Path.Combine(root, fixture.GetProperty("sourceDocument").GetString()!));
        var canonicalText = NormalizeEvidence(parsed.CanonicalText);

        fixture.GetProperty("concepts").GetArrayLength().Should().Be(10);
        fixture.GetProperty("relationships").GetArrayLength().Should().Be(7);
        fixture.GetProperty("negativeExpectations").GetArrayLength().Should().Be(6);
        foreach (var collection in new[] { "concepts", "relationships", "negativeExpectations" })
        {
            foreach (var item in fixture.GetProperty(collection).EnumerateArray())
            {
                foreach (var fragment in item.GetProperty("evidenceFragments").EnumerateArray())
                    canonicalText.Contains(NormalizeEvidence(fragment.GetString()!), StringComparison.Ordinal)
                        .Should().BeTrue("the human reference {0} must cite the source document: {1}", collection, item.ToString());
            }
        }
    }

    private static string NormalizeEvidence(string value)
        => string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public async Task Human_reference_table_evidence_preserves_service_scope_and_replica_conflict()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        using var reference = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(
            root, "tests", "Goldens", "cloud-service-architecture-ontology-reference.json")));
        using var archive = ZipFile.OpenRead(Path.Combine(root, reference.RootElement.GetProperty("sourceDocument").GetString()!));
        using var documentStream = archive.GetEntry("word/document.xml")!.Open();
        var document = XDocument.Load(documentStream);
        XNamespace word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var tables = document.Descendants(word + "tbl")
            .Select(table => table.Elements(word + "tr")
                .Select(row => row.Elements(word + "tc")
                    .Select(cell => NormalizeEvidence(string.Join(" ", cell.Elements(word + "p")
                        .Select(paragraph => string.Concat(paragraph.Descendants(word + "t").Select(text => text.Value))))))
                    .ToArray())
                .ToArray())
            .ToArray();

        tables.SelectMany(table => table).Should().ContainSingle(row =>
            row.Contains("Compliance Service", StringComparer.Ordinal)
            && row.Contains("Regulatory", StringComparer.Ordinal)
            && row.Contains("Immutable audit log, regulatory report generation", StringComparer.Ordinal));

        var scalingTable = tables.Single(table => table.Any(row => row.Contains("Min Replicas", StringComparer.Ordinal)));
        var header = scalingTable.Single(row => row.Contains("Min Replicas", StringComparer.Ordinal));
        var serviceColumn = Array.IndexOf(header, "Service");
        var minimumColumn = Array.IndexOf(header, "Min Replicas");
        serviceColumn.Should().BeGreaterThanOrEqualTo(0);
        minimumColumn.Should().BeGreaterThanOrEqualTo(0);
        var inconsistency = reference.RootElement.GetProperty("negativeExpectations").EnumerateArray()
            .Single(item => item.GetProperty("id").GetString() == "replica-inconsistency");
        foreach (var expected in inconsistency.GetProperty("tableExpectations").EnumerateArray())
        {
            var row = scalingTable.Single(cells => cells[serviceColumn] == expected.GetProperty("service").GetString());
            row[minimumColumn].Should().Be(expected.GetProperty("minReplicas").GetString());
        }
    }

    [Fact]
    public async Task Primary_docx_compiles_offline_to_a_fully_provenanced_snapshot()
    {
        var documentPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "samples", "architecture", "Cloud-service-Architecture.docx"));
        File.Exists(documentPath).Should().BeTrue();
        var family = DocumentFamilyManifest.Register(
            "sample-architecture", "acceptance", "cloud-service-architecture-v1");
        var parsed = await new DocxParser().ParseAsync(documentPath);
        var compiler = new OntologyCompilationService(new DeterministicSpanCandidateGenerator());

        var result = await compiler.CompileAsync(
            parsed,
            family,
            "sample-architecture",
            "v1",
            "cloud-service-architecture-acceptance-test",
            "pipeline-v1",
            "extract-v1");

        result.CanPublish.Should().BeTrue();
        result.Snapshot.Should().NotBeNull();
        result.Candidate.Should().NotBeNull();
        result.CurrentSpanRecords.Should().HaveCount(parsed.Blocks.Count);
        result.Snapshot!.Concepts.Should().NotBeEmpty();
        var evidenceIndex = CanonicalSpanEvidenceIndex.Create(parsed, family.FamilyId, "v1");
        foreach (var evidence in result.Snapshot.Concepts.SelectMany(concept => concept.Provenance))
            evidenceIndex.Resolve(evidence).Should().NotBeNull();
        foreach (var evidence in result.Snapshot.Relationships.SelectMany(relationship => relationship.Provenance))
            evidenceIndex.Resolve(evidence).Should().NotBeNull();

        result.Candidate!.Diagnostics.Should().Contain("offline-fake-not-semantic-extraction");
    }
}