namespace LambdaRag.Core.Ontology;

/// <summary>Minimal span kind set for the proof slice.</summary>
public enum SpanKind
{
    Heading,
    Body,
}

/// <summary>
/// Raw, fixture- or parser-produced span before canonicalisation. Offsets and
/// page number are provenance only and never enter identity.
/// </summary>
public sealed record RawSpan(
    SpanKind Kind,
    int HeadingLevel,
    string Text,
    int CharStart,
    int CharLength,
    int? PageNumber = null);

/// <summary>
/// A canonicalised span observation within one version: content fingerprint
/// and structural context computed, identity not yet assigned.
/// </summary>
public sealed record SpanObservation(
    int Ordinal,
    SpanKind Kind,
    int HeadingLevel,
    string Text,
    SpanContentFingerprint ContentFingerprint,
    string AncestryPath,
    SpanStructuralContextKey StructuralKey,
    string StructuralKeyFingerprint,
    int CharStart,
    int CharLength,
    int? PageNumber);

/// <summary>A span occurrence with an assigned <see cref="SpanInstanceId"/> for a given version.</summary>
public sealed record SpanVersionRecord(
    SpanInstanceId InstanceId,
    SpanVersionIdentity VersionIdentity,
    SpanObservation Observation)
{
    public int Ordinal => Observation.Ordinal;
    public SpanKind Kind => Observation.Kind;
    public SpanContentFingerprint ContentFingerprint => Observation.ContentFingerprint;
    public string AncestryPath => Observation.AncestryPath;
    public string StructuralKeyFingerprint => Observation.StructuralKeyFingerprint;
    public string Text => Observation.Text;
}

/// <summary>
/// Builds canonical <see cref="SpanObservation"/>s from raw spans. Document
/// order is taken from list order. Heading-ordinal ancestry, neighbour
/// content fingerprints and the structural key fingerprint are all computed
/// here deterministically.
/// </summary>
public static class SpanObservationBuilder
{
    public static IReadOnlyList<SpanObservation> Build(IReadOnlyList<RawSpan> rawSpans)
    {
        ArgumentNullException.ThrowIfNull(rawSpans);

        // Pass 1: normalise text, compute content fingerprint and heading-ordinal ancestry.
        var normalizedText = new string[rawSpans.Count];
        var contentFp = new SpanContentFingerprint[rawSpans.Count];
        var ancestryPath = new string[rawSpans.Count];

        // Stack of heading frames: (level, childCountForNextDescendant).
        var stack = new List<HeadingFrame>();
        var rootChildCount = 0;

        for (var i = 0; i < rawSpans.Count; i++)
        {
            var raw = rawSpans[i];
            var text = OntologyCanonical.Normalize(raw.Text, "span.text");
            normalizedText[i] = text;
            contentFp[i] = SpanContentFingerprint.FromText(text);

            if (raw.Kind == SpanKind.Heading)
            {
                var level = raw.HeadingLevel <= 0 ? 1 : raw.HeadingLevel;
                while (stack.Count > 0 && stack[^1].Level >= level)
                    stack.RemoveAt(stack.Count - 1);

                int index;
                if (stack.Count > 0)
                {
                    var parent = stack[^1];
                    index = parent.ChildCount;
                    stack[^1] = parent with { ChildCount = parent.ChildCount + 1 };
                }
                else
                {
                    index = rootChildCount++;
                }

                // A heading's ancestry key is its PARENT path; its own slot index lives in that path's tail.
                ancestryPath[i] = PathString(stack);
                stack.Add(new HeadingFrame(level, index, 0));
            }
            else
            {
                // Body: owning heading is the current stack top (or root).
                ancestryPath[i] = PathString(stack);
            }
        }

        // Pass 2: neighbour content fingerprints within each (kind, ancestry) sibling group.
        var observations = new SpanObservation[rawSpans.Count];
        var groups = new Dictionary<(SpanKind, string), List<int>>();
        for (var i = 0; i < rawSpans.Count; i++)
        {
            var key = (rawSpans[i].Kind, ancestryPath[i]);
            if (!groups.TryGetValue(key, out var list))
            {
                list = new List<int>();
                groups[key] = list;
            }
            list.Add(i);
        }

        var siblingIndexOf = new Dictionary<int, (List<int> group, int pos)>();
        foreach (var list in groups.Values)
            for (var pos = 0; pos < list.Count; pos++)
                siblingIndexOf[list[pos]] = (list, pos);

        for (var i = 0; i < rawSpans.Count; i++)
        {
            var (group, pos) = siblingIndexOf[i];
            string? prev = pos > 0 ? contentFp[group[pos - 1]].Value : null;
            string? next = pos < group.Count - 1 ? contentFp[group[pos + 1]].Value : null;

            var structKey = new SpanStructuralContextKey(rawSpans[i].Kind, ancestryPath[i], prev, next);
            observations[i] = new SpanObservation(
                Ordinal: i,
                Kind: rawSpans[i].Kind,
                HeadingLevel: rawSpans[i].HeadingLevel,
                Text: normalizedText[i],
                ContentFingerprint: contentFp[i],
                AncestryPath: ancestryPath[i],
                StructuralKey: structKey,
                StructuralKeyFingerprint: structKey.Fingerprint(),
                CharStart: rawSpans[i].CharStart,
                CharLength: rawSpans[i].CharLength,
                PageNumber: rawSpans[i].PageNumber);
        }

        return observations;
    }

    private static string PathString(List<HeadingFrame> stack)
    {
        if (stack.Count == 0)
            return "/";
        var parts = new string[stack.Count];
        for (var i = 0; i < stack.Count; i++)
            parts[i] = stack[i].Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return "/" + string.Join("/", parts);
    }

    private readonly record struct HeadingFrame(int Level, int Index, int ChildCount);
}
