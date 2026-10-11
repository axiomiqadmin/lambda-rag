using LambdaRag.Core.Ontology;

namespace LambdaRag.Projection;

public enum OntologyTopicMode
{
    Static,
    Dynamic,
    Hybrid,
}

public static class AcceptedOntologyTopicMapAdapter
{
    public static TopicMap Create(
        OntologySnapshot snapshot,
        OntologyTopicMode mode,
        TopicMap? staticMap = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        OntologyArtifactValidator.ValidateSnapshot(snapshot);
        if (mode is OntologyTopicMode.Static or OntologyTopicMode.Hybrid && staticMap is null)
            throw new ArgumentNullException(nameof(staticMap), $"Mode '{mode}' requires a static topic map.");

        if (mode == OntologyTopicMode.Static)
            return staticMap!;

        var dynamicTopics = BuildDynamicTopics(snapshot);
        if (mode == OntologyTopicMode.Dynamic)
        {
            return new TopicMap(
                snapshot.OntologyId,
                snapshot.SnapshotId,
                dynamicTopics,
                new Dictionary<string, AxisDefinition>(StringComparer.Ordinal),
                Array.Empty<string>());
        }

        var combined = staticMap!.Topics.ToDictionary(topic => topic.Id, StringComparer.Ordinal);
        foreach (var dynamicTopic in dynamicTopics)
        {
            if (!combined.TryGetValue(dynamicTopic.Id, out var existing))
            {
                combined.Add(dynamicTopic.Id, dynamicTopic);
                continue;
            }

            if (existing.Axis != dynamicTopic.Axis)
                throw new OntologyArtifactValidationException(
                    $"Static and dynamic topic '{dynamicTopic.Id}' have incompatible axis definitions.");
            combined[dynamicTopic.Id] = existing with
            {
                Keywords = existing.Keywords.Concat(dynamicTopic.Keywords)
                    .Select(NormalizePhrase)
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(keyword => keyword, StringComparer.Ordinal)
                    .ToArray(),
            };
        }

        return new TopicMap(
            staticMap.Domain,
            $"{staticMap.Version}+ontology-{snapshot.SnapshotId}",
            combined.Values.OrderBy(topic => topic.Id, StringComparer.Ordinal).ToArray(),
            staticMap.Axes,
            staticMap.AmendmentPatterns);
    }

    private static IReadOnlyList<TopicDefinition> BuildDynamicTopics(OntologySnapshot snapshot)
    {
        var concepts = snapshot.Concepts
            .Where(concept => concept.Status == OntologyArtifactStatus.Active)
            .OrderBy(concept => concept.CanonicalSemanticKey, StringComparer.Ordinal)
            .ToArray();
        var phrases = new Dictionary<string, string>(StringComparer.Ordinal);
        var topics = new List<TopicDefinition>(concepts.Length);
        foreach (var concept in concepts)
        {
            var keywords = new[] { concept.CanonicalLabel }
                .Concat(concept.Aliases)
                .Select(NormalizePhrase)
                .Where(keyword => keyword.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(keyword => keyword, StringComparer.Ordinal)
                .ToArray();
            foreach (var keyword in keywords)
            {
                var key = keyword.ToUpperInvariant();
                if (phrases.TryGetValue(key, out var priorKey)
                    && !string.Equals(priorKey, concept.CanonicalSemanticKey, StringComparison.Ordinal))
                    throw new OntologyArtifactValidationException(
                        $"Dynamic label or alias '{keyword}' ambiguously maps to multiple concepts.");
                phrases[key] = concept.CanonicalSemanticKey;
            }

            topics.Add(new TopicDefinition(
                concept.CanonicalSemanticKey,
                null,
                keywords));
        }
        return topics;
    }

    private static string NormalizePhrase(string phrase)
        => string.Join(" ", OntologyCanonical.Normalize(phrase).Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
}