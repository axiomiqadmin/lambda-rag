using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LambdaRag.Core.Ontology;

/// <summary>
/// Canonical JSON writer for ontology/span proof artefacts. Produces
/// byte-identical, compact UTF-8 for equivalent inputs by:
///   • ordering object properties ordinally,
///   • rejecting duplicate property keys,
///   • NFC-normalising + control-rejecting every string (keys and values),
///   • preserving array order as given by the caller (callers sort
///     non-semantic arrays before handing them in).
/// Numbers are written verbatim (the proof uses integers only).
/// </summary>
public static class OntologyCanonicalJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = false,
        SkipValidation = false,
    };

    public static byte[] SerializeToUtf8(JsonObject obj)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, WriterOptions))
        {
            WriteNode(writer, obj);
        }
        return ms.ToArray();
    }

    public static string Serialize(JsonObject obj) => Encoding.UTF8.GetString(SerializeToUtf8(obj));

    private static void WriteNode(Utf8JsonWriter writer, JsonNode? node)
    {
        switch (node)
        {
            case null:
                writer.WriteNullValue();
                break;

            case JsonObject obj:
                writer.WriteStartObject();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var kvp in obj.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    if (!seen.Add(kvp.Key))
                        throw new OntologyCanonicalException($"Duplicate property key '{kvp.Key}'.");
                    writer.WritePropertyName(OntologyCanonical.Normalize(kvp.Key, "key"));
                    WriteNode(writer, kvp.Value);
                }
                writer.WriteEndObject();
                break;

            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array)
                    WriteNode(writer, item);
                writer.WriteEndArray();
                break;

            case JsonValue value:
                switch (value.GetValueKind())
                {
                    case JsonValueKind.String:
                        writer.WriteStringValue(OntologyCanonical.Normalize(value.GetValue<string>(), "value"));
                        break;
                    default:
                        value.WriteTo(writer);
                        break;
                }
                break;

            default:
                node.WriteTo(writer);
                break;
        }
    }
}
