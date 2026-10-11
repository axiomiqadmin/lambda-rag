using LambdaRag.Core.Domain;

namespace LambdaRag.Core.Ontology;

public sealed record ParsedDocumentSpanInput(
    string ParserId,
    string ParserVersion,
    IReadOnlyList<RawSpan> Spans)
{
    public SpanVersionIdentity CreateVersionIdentity(string familyId, string versionLabel)
        => SpanVersionIdentity.Create(familyId, versionLabel, ParserId, ParserVersion);
}

public static class ParsedDocumentSpanAdapter
{
    public static ParsedDocumentSpanInput Adapt(ParsedDocument parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var parserId = RequiredMetadata(parsed, "parser_id");
        var parserVersion = RequiredMetadata(parsed, "parser_version");
        var spans = new RawSpan[parsed.Blocks.Count];

        for (var i = 0; i < parsed.Blocks.Count; i++)
        {
            var block = parsed.Blocks[i];
            var kind = block.Kind switch
            {
                ContentBlockKind.Heading => SpanKind.Heading,
                ContentBlockKind.Paragraph or ContentBlockKind.ListItem
                    or ContentBlockKind.TableCell or ContentBlockKind.CodeBlock
                    or ContentBlockKind.Caption or ContentBlockKind.Other => SpanKind.Body,
                _ => throw new InvalidOperationException(
                    $"Unsupported content block kind '{block.Kind}'."),
            };

            spans[i] = new RawSpan(
                kind,
                block.HeadingLevel,
                block.Text,
                block.Span.CharStart,
                block.Span.CharLength,
                block.Span.PageNumber);
        }

        return new ParsedDocumentSpanInput(
            parserId,
            parserVersion,
            Array.AsReadOnly(spans));
    }

    private static string RequiredMetadata(ParsedDocument parsed, string key)
    {
        if (!parsed.Metadata.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException(
                $"Parsed document metadata must contain a non-empty '{key}'.");

        return OntologyCanonical.NormalizeRequired(value, $"metadata.{key}");
    }
}