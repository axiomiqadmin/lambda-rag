using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Core.Ontology;

/// <summary>
/// Thrown when a value violates the ontology canonicalisation rules
/// (prohibited control characters, empty required inputs, etc.).
/// </summary>
public sealed class OntologyCanonicalException : Exception
{
    public OntologyCanonicalException(string message) : base(message) { }
}

/// <summary>
/// Smallest span/ontology-specific canonicalisation layer for the Phase 1
/// proof slice. Intentionally does NOT replace repository-wide
/// <see cref="CanonicalJson"/>; it adds the stricter rules the span-identity
/// proof needs: NFC normalisation, control-character rejection, and a
/// length-prefixed (delimiter-injection-resistant) fingerprint builder.
/// </summary>
public static class OntologyCanonical
{
    /// <summary>Version tag folded into fingerprints so canonicalisation drift invalidates loudly.</summary>
    public const string CanonicalisationVersion = "lr-canon/1.0.0";

    /// <summary>
    /// NFC-normalise <paramref name="value"/> and reject any Unicode control
    /// character (category Cc, which includes the ASCII unit separator used by
    /// <see cref="ContentHash.Compose"/> — the delimiter-injection vector).
    /// </summary>
    public static string Normalize(string value, string fieldName = "value")
    {
        ArgumentNullException.ThrowIfNull(value);
        var nfc = value.Normalize(NormalizationForm.FormC);
        foreach (var ch in nfc)
        {
            if (char.IsControl(ch))
            {
                throw new OntologyCanonicalException(
                    $"Prohibited control character U+{(int)ch:X4} in {fieldName}.");
            }
        }
        return nfc;
    }

    /// <summary>NFC-normalise and reject controls, additionally requiring a non-empty result.</summary>
    public static string NormalizeRequired(string value, string fieldName)
    {
        var n = Normalize(value, fieldName);
        if (n.Length == 0)
            throw new OntologyCanonicalException($"Required field '{fieldName}' is empty.");
        return n;
    }

    public static string NormalizeMultiline(string value, string fieldName = "value")
    {
        ArgumentNullException.ThrowIfNull(value);
        var normalizedLines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var nfc = normalizedLines.Normalize(NormalizationForm.FormC);
        foreach (var ch in nfc)
        {
            if (char.IsControl(ch) && ch != '\n')
                throw new OntologyCanonicalException(
                    $"Prohibited control character U+{(int)ch:X4} in {fieldName}.");
        }
        return nfc;
    }
}

/// <summary>
/// Builds a deterministic fingerprint over a sequence of fields using a
/// length-prefixed binary encoding, so free text can never forge a field
/// boundary (unlike delimiter-only concatenation). Each field is written as
/// [8-byte big-endian length][UTF-8 bytes]. The final hash reuses the
/// repository <see cref="ContentHash"/> SHA-256 scheme.
/// </summary>
public sealed class CanonicalFingerprint
{
    private readonly MemoryStream _buffer = new();

    private void AppendBlock(ReadOnlySpan<byte> bytes)
    {
        Span<byte> len = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(len, (ulong)bytes.Length);
        _buffer.Write(len);
        _buffer.Write(bytes);
    }

    /// <summary>Append a structural tag (constant, control-free field label).</summary>
    public CanonicalFingerprint Tag(string tag)
    {
        AppendBlock(Encoding.UTF8.GetBytes(tag));
        return this;
    }

    /// <summary>Append NFC-normalised, control-rejected free text as its own length-prefixed block.</summary>
    public CanonicalFingerprint Text(string value)
    {
        AppendBlock(Encoding.UTF8.GetBytes(OntologyCanonical.Normalize(value)));
        return this;
    }

    /// <summary>Append an integer using an invariant representation.</summary>
    public CanonicalFingerprint Int(long value)
    {
        AppendBlock(Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture)));
        return this;
    }

    public ContentHash Build() => ContentHash.OfBytes(_buffer.ToArray());
}
