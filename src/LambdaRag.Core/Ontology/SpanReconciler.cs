namespace LambdaRag.Core.Ontology;

/// <summary>Thrown when two versions were produced by incompatible parser/canonicalisation versions.</summary>
public sealed class ParserVersionMismatchException : Exception
{
    public ParserVersionMismatchException(string message) : base(message) { }
}

public interface ISpanReconciler
{
    IReadOnlyList<SpanVersionRecord> Seed(
        IReadOnlyList<SpanObservation> observations, SpanVersionIdentity identity);

    SpanReconciliationResultSet Reconcile(
        IReadOnlyList<SpanVersionRecord> previous,
        IReadOnlyList<SpanObservation> current,
        SpanVersionIdentity currentIdentity,
        SpanReconciliationPolicy policy = SpanReconciliationPolicy.Strict);
}

/// <summary>
/// Deterministic, evidence-staged span reconciler. Carries an existing
/// identity forward only on exact structural anchoring or globally-unique
/// content; otherwise it mints fresh identities and prefers
/// UnresolvedReplacement / Unresolved over a false match. No embeddings,
/// edit-distance, similarity metrics or LLM decisions are used.
/// </summary>
public sealed class DeterministicSpanReconciler : ISpanReconciler
{
    public IReadOnlyList<SpanVersionRecord> Seed(
        IReadOnlyList<SpanObservation> observations, SpanVersionIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(observations);
        ArgumentNullException.ThrowIfNull(identity);
        var list = new SpanVersionRecord[observations.Count];
        for (var i = 0; i < observations.Count; i++)
            list[i] = new SpanVersionRecord(
                SpanInstanceId.Mint(identity, observations[i].Ordinal), identity, observations[i]);
        return list;
    }

    public SpanReconciliationResultSet Reconcile(
        IReadOnlyList<SpanVersionRecord> previous,
        IReadOnlyList<SpanObservation> current,
        SpanVersionIdentity currentIdentity,
        SpanReconciliationPolicy policy = SpanReconciliationPolicy.Strict)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(currentIdentity);

        SpanVersionIdentity? previousIdentity = previous.Count > 0 ? previous[0].VersionIdentity : null;
        ValidateCompatibility(previous, previousIdentity, currentIdentity);

        var p = previous.Count;
        var c = current.Count;
        var matchedPrev = new bool[p];
        var matchedCurr = new bool[c];
        var assignedId = new SpanInstanceId?[c];
        var results = new List<SpanReconciliationResult>();

        var allPrevContent = new HashSet<string>(previous.Select(r => r.ContentFingerprint.Value), StringComparer.Ordinal);
        var allCurrContent = new HashSet<string>(current.Select(o => o.ContentFingerprint.Value), StringComparer.Ordinal);

        // Stage A — exact anchor: unique (structuralKey, content) on both sides.
        var prevByAnchor = GroupPrev(previous, matchedPrev, r => (r.StructuralKeyFingerprint, r.ContentFingerprint.Value));
        var currByAnchor = GroupCurr(current, matchedCurr, o => (o.StructuralKeyFingerprint, o.ContentFingerprint.Value));
        foreach (var key in prevByAnchor.Keys)
        {
            if (!currByAnchor.TryGetValue(key, out var currList)) continue;
            var prevList = prevByAnchor[key];
            if (prevList.Count != 1 || currList.Count != 1) continue;
            var pi = prevList[0];
            var ci = currList[0];
            matchedPrev[pi] = true;
            matchedCurr[ci] = true;
            assignedId[ci] = previous[pi].InstanceId;
            results.Add(Carried(previous[pi], current[ci], currentIdentity,
                SpanClassification.Unchanged, "exact-anchor", Array.Empty<string>()));
        }

        // Stage B — globally unique content among the still-unmatched.
        var movePairs = new List<(int p, int c)>();
        var prevByContent = GroupPrev(previous, matchedPrev, r => r.ContentFingerprint.Value);
        var currByContent = GroupCurr(current, matchedCurr, o => o.ContentFingerprint.Value);
        foreach (var key in prevByContent.Keys)
        {
            if (!currByContent.TryGetValue(key, out var currList)) continue;
            var prevList = prevByContent[key];
            if (prevList.Count != 1 || currList.Count != 1) continue;
            var pi = prevList[0];
            var ci = currList[0];
            if (matchedPrev[pi] || matchedCurr[ci]) continue;
            matchedPrev[pi] = true;
            matchedCurr[ci] = true;
            assignedId[ci] = previous[pi].InstanceId;
            movePairs.Add((pi, ci));
        }

        ClassifyMovePairs(previous, current, currentIdentity, movePairs, results);

        // Stage D — split (1 previous -> >=2 contiguous current fragments with identical concatenation).
        for (var pi = 0; pi < p; pi++)
        {
            if (matchedPrev[pi]) continue;
            var run = FindConcatRunInCurrent(current, matchedCurr, previous[pi]);
            if (run is null) continue;

            matchedPrev[pi] = true;
            var fragmentIds = new List<SpanInstanceId>();
            foreach (var ci in run)
            {
                matchedCurr[ci] = true;
                var newId = SpanInstanceId.Mint(currentIdentity, current[ci].Ordinal);
                assignedId[ci] = newId;
                fragmentIds.Add(newId);
                results.Add(new SpanReconciliationResult(
                    SpanClassification.SplitFrom, null, newId, null, currentIdentity,
                    null, current[ci].Ordinal, "split-fragment", Array.Empty<string>(),
                    new[] { new SpanRelationship(SpanRelationshipType.SplitFrom, newId.Value, previous[pi].InstanceId.Value) }));
            }
            results.Add(new SpanReconciliationResult(
                SpanClassification.Removed, previous[pi].InstanceId, null, previous[pi].VersionIdentity, null,
                previous[pi].Ordinal, null, "retired-by-split", Array.Empty<string>(),
                fragmentIds.Select(f => new SpanRelationship(
                    SpanRelationshipType.SplitInto, previous[pi].InstanceId.Value, f.Value)).ToArray()));
        }

        // Stage E — merge (>=2 contiguous previous -> 1 current with identical concatenation).
        for (var ci = 0; ci < c; ci++)
        {
            if (matchedCurr[ci]) continue;
            var run = FindConcatRunInPrevious(previous, matchedPrev, current[ci]);
            if (run is null) continue;

            matchedCurr[ci] = true;
            var newId = SpanInstanceId.Mint(currentIdentity, current[ci].Ordinal);
            assignedId[ci] = newId;
            var sourceIds = new List<SpanInstanceId>();
            foreach (var pi in run)
            {
                matchedPrev[pi] = true;
                sourceIds.Add(previous[pi].InstanceId);
                results.Add(new SpanReconciliationResult(
                    SpanClassification.Removed, previous[pi].InstanceId, null, previous[pi].VersionIdentity, null,
                    previous[pi].Ordinal, null, "retired-by-merge", Array.Empty<string>(),
                    new[] { new SpanRelationship(SpanRelationshipType.MergedInto, previous[pi].InstanceId.Value, newId.Value) }));
            }
            results.Add(new SpanReconciliationResult(
                SpanClassification.MergedFrom, null, newId, null, currentIdentity,
                null, current[ci].Ordinal, "merged-span", Array.Empty<string>(),
                sourceIds.Select(s => new SpanRelationship(
                    SpanRelationshipType.MergedFrom, newId.Value, s.Value)).ToArray()));
        }

        // Stage F — same structural slot, different content -> UnresolvedReplacement.
        var prevBySlot = GroupPrev(previous, matchedPrev, r => r.StructuralKeyFingerprint);
        var currBySlot = GroupCurr(current, matchedCurr, o => o.StructuralKeyFingerprint);
        foreach (var key in prevBySlot.Keys)
        {
            if (!currBySlot.TryGetValue(key, out var currList)) continue;
            var prevList = prevBySlot[key];
            if (prevList.Count != 1 || currList.Count != 1) continue;
            var pi = prevList[0];
            var ci = currList[0];
            if (matchedPrev[pi] || matchedCurr[ci]) continue;
            if (previous[pi].ContentFingerprint.Value == current[ci].ContentFingerprint.Value) continue;
            matchedPrev[pi] = true;
            matchedCurr[ci] = true;
            var newId = SpanInstanceId.Mint(currentIdentity, current[ci].Ordinal);
            assignedId[ci] = newId;
            results.Add(new SpanReconciliationResult(
                SpanClassification.UnresolvedReplacement, previous[pi].InstanceId, newId,
                previous[pi].VersionIdentity, currentIdentity,
                previous[pi].Ordinal, current[ci].Ordinal, "replacement-in-slot",
                new[] { "same-structural-slot-different-content" }, Array.Empty<SpanRelationship>()));
        }

        // Stage G — PositionalEquivalent opt-in: pair exact-content duplicates by position.
        if (policy == SpanReconciliationPolicy.PositionalEquivalent)
            PositionalPass(previous, current, currentIdentity, matchedPrev, matchedCurr, assignedId, results);

        // Stage H — remainder.
        for (var ci = 0; ci < c; ci++)
        {
            if (matchedCurr[ci]) continue;
            matchedCurr[ci] = true;
            var newId = SpanInstanceId.Mint(currentIdentity, current[ci].Ordinal);
            assignedId[ci] = newId;
            if (!allPrevContent.Contains(current[ci].ContentFingerprint.Value))
                results.Add(new SpanReconciliationResult(
                    SpanClassification.Added, null, newId, null, currentIdentity,
                    null, current[ci].Ordinal, "added", Array.Empty<string>(), Array.Empty<SpanRelationship>()));
            else
                results.Add(new SpanReconciliationResult(
                    SpanClassification.Unresolved, null, newId, null, currentIdentity,
                    null, current[ci].Ordinal, "ambiguous-duplicate", Array.Empty<string>(), Array.Empty<SpanRelationship>()));
        }

        for (var pi = 0; pi < p; pi++)
        {
            if (matchedPrev[pi]) continue;
            matchedPrev[pi] = true;
            if (!allCurrContent.Contains(previous[pi].ContentFingerprint.Value))
                results.Add(new SpanReconciliationResult(
                    SpanClassification.Removed, previous[pi].InstanceId, null, previous[pi].VersionIdentity, null,
                    previous[pi].Ordinal, null, "removed", Array.Empty<string>(), Array.Empty<SpanRelationship>()));
            else
                results.Add(new SpanReconciliationResult(
                    SpanClassification.Unresolved, previous[pi].InstanceId, null, previous[pi].VersionIdentity, null,
                    previous[pi].Ordinal, null, "ambiguous-duplicate", Array.Empty<string>(), Array.Empty<SpanRelationship>()));
        }

        var currentRecords = new SpanVersionRecord[c];
        for (var ci = 0; ci < c; ci++)
        {
            var id = assignedId[ci] ?? throw new InvalidOperationException(
                $"Span at ordinal {ci} was not assigned an identity.");
            currentRecords[ci] = new SpanVersionRecord(id, currentIdentity, current[ci]);
        }

        return new SpanReconciliationResultSet(
            previousIdentity, currentIdentity, policy, results, currentRecords);
    }

    private static void ClassifyMovePairs(
        IReadOnlyList<SpanVersionRecord> previous,
        IReadOnlyList<SpanObservation> current,
        SpanVersionIdentity currentIdentity,
        List<(int p, int c)> movePairs,
        List<SpanReconciliationResult> results)
    {
        var within = new List<(int p, int c)>();
        var cross = new List<(int p, int c)>();
        foreach (var pair in movePairs)
        {
            var sameAncestry = previous[pair.p].AncestryPath == current[pair.c].AncestryPath
                               && previous[pair.p].Kind == current[pair.c].Kind;
            if (sameAncestry) within.Add(pair); else cross.Add(pair);
        }

        foreach (var pair in cross)
            results.Add(Carried(previous[pair.p], current[pair.c], currentIdentity,
                SpanClassification.MovedCrossHeading, "cross-heading-move", Array.Empty<string>()));

        foreach (var group in within.GroupBy(pair => (current[pair.c].Kind, current[pair.c].AncestryPath)))
        {
            var byPrev = group.OrderBy(x => previous[x.p].Ordinal).ToList();
            var byCurr = group.OrderBy(x => current[x.c].Ordinal).ToList();
            for (var i = 0; i < byPrev.Count; i++)
            {
                var pair = byPrev[i];
                var posCurr = byCurr.IndexOf(pair);
                if (posCurr == i)
                    results.Add(Carried(previous[pair.p], current[pair.c], currentIdentity,
                        SpanClassification.Unchanged, "unique-content-reanchored",
                        new[] { "neighbour-context-changed" }));
                else
                    results.Add(Carried(previous[pair.p], current[pair.c], currentIdentity,
                        SpanClassification.MovedWithinHeading, "reorder-within-heading", Array.Empty<string>()));
            }
        }
    }

    private static void PositionalPass(
        IReadOnlyList<SpanVersionRecord> previous,
        IReadOnlyList<SpanObservation> current,
        SpanVersionIdentity currentIdentity,
        bool[] matchedPrev,
        bool[] matchedCurr,
        SpanInstanceId?[] assignedId,
        List<SpanReconciliationResult> results)
    {
        var prevByContent = GroupPrev(previous, matchedPrev, r => r.ContentFingerprint.Value);
        var currByContent = GroupCurr(current, matchedCurr, o => o.ContentFingerprint.Value);
        foreach (var key in prevByContent.Keys)
        {
            if (!currByContent.TryGetValue(key, out var currList)) continue;
            var prevList = prevByContent[key]
                .Where(i => !matchedPrev[i]).OrderBy(i => previous[i].Ordinal).ToList();
            var curr = currList.Where(i => !matchedCurr[i]).OrderBy(i => current[i].Ordinal).ToList();
            var n = Math.Min(prevList.Count, curr.Count);
            for (var k = 0; k < n; k++)
            {
                var pi = prevList[k];
                var ci = curr[k];
                matchedPrev[pi] = true;
                matchedCurr[ci] = true;
                assignedId[ci] = previous[pi].InstanceId;
                var classification = previous[pi].AncestryPath == current[ci].AncestryPath
                    ? SpanClassification.Unchanged
                    : SpanClassification.MovedCrossHeading;
                results.Add(Carried(previous[pi], current[ci], currentIdentity,
                    classification, "positional-duplicate", new[] { "PositionalDuplicateAssignment" }));
            }
        }
    }

    private static SpanReconciliationResult Carried(
        SpanVersionRecord prev,
        SpanObservation curr,
        SpanVersionIdentity currentIdentity,
        SpanClassification classification,
        string reason,
        IReadOnlyList<string> diagnostics)
        => new(
            classification, prev.InstanceId, prev.InstanceId, prev.VersionIdentity, currentIdentity,
            prev.Ordinal, curr.Ordinal, reason, diagnostics, Array.Empty<SpanRelationship>());

    private static List<int>? FindConcatRunInCurrent(
        IReadOnlyList<SpanObservation> current, bool[] matchedCurr, SpanVersionRecord target)
    {
        for (var start = 0; start < current.Count; start++)
        {
            if (matchedCurr[start]) continue;
            if (current[start].Kind != target.Kind || current[start].AncestryPath != target.AncestryPath) continue;
            var run = new List<int>();
            var concat = string.Empty;
            for (var j = start; j < current.Count; j++)
            {
                if (matchedCurr[j]) break;
                if (current[j].Kind != target.Kind || current[j].AncestryPath != target.AncestryPath) break;
                run.Add(j);
                concat += current[j].Text;
                if (concat.Length > target.Text.Length) break;
                if (concat == target.Text && run.Count >= 2) return run;
            }
        }
        return null;
    }

    private static List<int>? FindConcatRunInPrevious(
        IReadOnlyList<SpanVersionRecord> previous, bool[] matchedPrev, SpanObservation target)
    {
        for (var start = 0; start < previous.Count; start++)
        {
            if (matchedPrev[start]) continue;
            if (previous[start].Kind != target.Kind || previous[start].AncestryPath != target.AncestryPath) continue;
            var run = new List<int>();
            var concat = string.Empty;
            for (var j = start; j < previous.Count; j++)
            {
                if (matchedPrev[j]) break;
                if (previous[j].Kind != target.Kind || previous[j].AncestryPath != target.AncestryPath) break;
                run.Add(j);
                concat += previous[j].Text;
                if (concat.Length > target.Text.Length) break;
                if (concat == target.Text && run.Count >= 2) return run;
            }
        }
        return null;
    }

    private static Dictionary<TKey, List<int>> GroupPrev<TKey>(
        IReadOnlyList<SpanVersionRecord> previous, bool[] matched, Func<SpanVersionRecord, TKey> selector)
        where TKey : notnull
    {
        var map = new Dictionary<TKey, List<int>>();
        for (var i = 0; i < previous.Count; i++)
        {
            if (matched[i]) continue;
            var key = selector(previous[i]);
            if (!map.TryGetValue(key, out var list)) { list = new List<int>(); map[key] = list; }
            list.Add(i);
        }
        return map;
    }

    private static Dictionary<TKey, List<int>> GroupCurr<TKey>(
        IReadOnlyList<SpanObservation> current, bool[] matched, Func<SpanObservation, TKey> selector)
        where TKey : notnull
    {
        var map = new Dictionary<TKey, List<int>>();
        for (var i = 0; i < current.Count; i++)
        {
            if (matched[i]) continue;
            var key = selector(current[i]);
            if (!map.TryGetValue(key, out var list)) { list = new List<int>(); map[key] = list; }
            list.Add(i);
        }
        return map;
    }

    private static void ValidateCompatibility(
        IReadOnlyList<SpanVersionRecord> previous,
        SpanVersionIdentity? previousIdentity,
        SpanVersionIdentity currentIdentity)
    {
        if (previousIdentity is null) return;

        foreach (var record in previous)
        {
            if (record.VersionIdentity != previousIdentity)
                throw new ParserVersionMismatchException(
                    "Previous span records belong to more than one version identity.");
        }

        if (previousIdentity.FamilyId != currentIdentity.FamilyId)
            throw new ArgumentException(
                $"Cannot reconcile across document families '{previousIdentity.FamilyId}' and '{currentIdentity.FamilyId}'.");

        if (previousIdentity.ParserId != currentIdentity.ParserId
            || previousIdentity.ParserVersion != currentIdentity.ParserVersion
            || previousIdentity.CanonicalisationVersion != currentIdentity.CanonicalisationVersion)
        {
            throw new ParserVersionMismatchException(
                $"Incompatible producer: previous ({previousIdentity.ParserId}/{previousIdentity.ParserVersion}/{previousIdentity.CanonicalisationVersion}) "
                + $"vs current ({currentIdentity.ParserId}/{currentIdentity.ParserVersion}/{currentIdentity.CanonicalisationVersion}).");
        }
    }
}
