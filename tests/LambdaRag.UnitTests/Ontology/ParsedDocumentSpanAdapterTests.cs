using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;
using LambdaRag.Core.Ontology;
using LambdaRag.Parsing;
using Xunit;

namespace LambdaRag.UnitTests.Ontology;

public sealed class ParsedDocumentSpanAdapterTests
{
    [Fact]
    public void Adapt_maps_every_content_block_kind_and_preserves_provenance()
    {
        var kinds = Enum.GetValues<ContentBlockKind>();
        var blocks = kinds.Select((kind, index) => new ContentBlock(
            $"block-{index}",
            kind,
            $"text-{index}",
            new SourceSpan("doc", index * 20, index + 3, index + 1, "/heading"),
            index,
            "/heading")).ToArray();
        var parsed = Parsed(blocks);

        var adapted = ParsedDocumentSpanAdapter.Adapt(parsed);

        adapted.Spans.Should().HaveCount(blocks.Length);
        adapted.Spans.Select(span => span.Kind).Should().Equal(kinds.Select(kind =>
            kind == ContentBlockKind.Heading ? SpanKind.Heading : SpanKind.Body));
        for (var i = 0; i < blocks.Length; i++)
        {
            adapted.Spans[i].Text.Should().Be(blocks[i].Text);
            adapted.Spans[i].HeadingLevel.Should().Be(blocks[i].HeadingLevel);
            adapted.Spans[i].CharStart.Should().Be(blocks[i].Span.CharStart);
            adapted.Spans[i].CharLength.Should().Be(blocks[i].Span.CharLength);
            adapted.Spans[i].PageNumber.Should().Be(blocks[i].Span.PageNumber);
        }

        var identity = adapted.CreateVersionIdentity("family-1", "version-1");
        identity.ParserId.Should().Be("test-parser");
        identity.ParserVersion.Should().Be("1.2.3");
        identity.FamilyId.Should().Be("family-1");
        identity.VersionLabel.Should().Be("version-1");
    }

    [Theory]
    [InlineData("parser_id")]
    [InlineData("parser_version")]
    public void Adapt_fails_when_parser_metadata_is_missing(string missingKey)
    {
        var metadata = new Dictionary<string, string>
        {
            [missingKey == "parser_id" ? "parser_version" : "parser_id"] = "1.0.0",
        };
        var parsed = Parsed(Array.Empty<ContentBlock>(), metadata);

        Action act = () => ParsedDocumentSpanAdapter.Adapt(parsed);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{missingKey}'*");
    }

    [Fact]
    public void Adapt_fails_on_an_unrecognized_content_block_kind()
    {
        var parsed = Parsed(new[]
        {
            new ContentBlock(
                "future-block", (ContentBlockKind)999, "text",
                new SourceSpan("doc", 0, 4, null, "/"), 0, "/"),
        });

        Action act = () => ParsedDocumentSpanAdapter.Adapt(parsed);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Unsupported content block kind*");
    }

    [Fact]
    public async Task Adapt_preserves_spans_from_a_real_markdown_parse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lambda-rag-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(path, "# Heading\n\nBody paragraph.");

        try
        {
            var parsed = await new MarkdownParser().ParseAsync(path);
            var adapted = ParsedDocumentSpanAdapter.Adapt(parsed);

            adapted.ParserId.Should().Be("md-parser");
            adapted.ParserVersion.Should().Be("1.0.0");
            adapted.Spans.Select(span => span.Kind)
                .Should().Equal(SpanKind.Heading, SpanKind.Body);
            for (var i = 0; i < parsed.Blocks.Count; i++)
            {
                adapted.Spans[i].Text.Should().Be(parsed.Blocks[i].Text);
                adapted.Spans[i].CharStart.Should().Be(parsed.Blocks[i].Span.CharStart);
                adapted.Spans[i].CharLength.Should().Be(parsed.Blocks[i].Span.CharLength);
                adapted.Spans[i].PageNumber.Should().Be(parsed.Blocks[i].Span.PageNumber);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Adapt_preserves_spans_from_a_real_docx_parse()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "samples", "contracts", "contoso-sample-contract.docx"));
        File.Exists(path).Should().BeTrue();

        var parsed = await new DocxParser().ParseAsync(path);
        var adapted = ParsedDocumentSpanAdapter.Adapt(parsed);

        adapted.ParserId.Should().Be("docx-parser");
        adapted.ParserVersion.Should().Be("1.0.0");
        adapted.Spans.Should().HaveCount(parsed.Blocks.Count);
        for (var i = 0; i < parsed.Blocks.Count; i++)
        {
            var expectedKind = parsed.Blocks[i].Kind == ContentBlockKind.Heading
                ? SpanKind.Heading
                : SpanKind.Body;
            adapted.Spans[i].Kind.Should().Be(expectedKind);
            adapted.Spans[i].Text.Should().Be(parsed.Blocks[i].Text);
            adapted.Spans[i].CharStart.Should().Be(parsed.Blocks[i].Span.CharStart);
            adapted.Spans[i].CharLength.Should().Be(parsed.Blocks[i].Span.CharLength);
            adapted.Spans[i].PageNumber.Should().Be(parsed.Blocks[i].Span.PageNumber);
        }
    }

    [Fact]
    public async Task Parsed_markdown_versions_reconcile_after_a_local_insertion()
    {
        var previousPath = Path.Combine(Path.GetTempPath(), $"lambda-rag-{Guid.NewGuid():N}.md");
        var currentPath = Path.Combine(Path.GetTempPath(), $"lambda-rag-{Guid.NewGuid():N}.md");
        await File.WriteAllTextAsync(previousPath, "# Terms\n\nAlpha.\n\nBeta.");
        await File.WriteAllTextAsync(currentPath, "# Terms\n\nInserted.\n\nAlpha.\n\nBeta.");

        try
        {
            var parser = new MarkdownParser();
            var previousInput = ParsedDocumentSpanAdapter.Adapt(await parser.ParseAsync(previousPath));
            var currentInput = ParsedDocumentSpanAdapter.Adapt(await parser.ParseAsync(currentPath));
            var reconciler = new DeterministicSpanReconciler();
            var previous = reconciler.Seed(
                SpanObservationBuilder.Build(previousInput.Spans),
                previousInput.CreateVersionIdentity("family-1", "v1"));
            var currentObservations = SpanObservationBuilder.Build(currentInput.Spans);

            var result = reconciler.Reconcile(
                previous,
                currentObservations,
                currentInput.CreateVersionIdentity("family-1", "v2"));

            result.CurrentRecords.Single(record => record.Text == "Alpha.").InstanceId
                .Should().Be(previous.Single(record => record.Text == "Alpha.").InstanceId);
            result.CurrentRecords.Single(record => record.Text == "Beta.").InstanceId
                .Should().Be(previous.Single(record => record.Text == "Beta.").InstanceId);
            result.Results.Single(record => record.CurrentOrdinal == 1).Classification
                .Should().Be(SpanClassification.Added);
            result.HasUnresolved.Should().BeFalse();
        }
        finally
        {
            File.Delete(previousPath);
            File.Delete(currentPath);
        }
    }

    private static ParsedDocument Parsed(
        IReadOnlyList<ContentBlock> blocks,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var source = new SourceDocument(
            ContentHash.OfString("parsed-document-span-adapter-test"),
            "test.md",
            SourceDocumentKind.Markdown,
            0,
            DateTimeOffset.UnixEpoch);
        return new ParsedDocument(
            source,
            string.Empty,
            blocks,
            metadata ?? new Dictionary<string, string>
            {
                ["parser_id"] = "test-parser",
                ["parser_version"] = "1.2.3",
            });
    }
}