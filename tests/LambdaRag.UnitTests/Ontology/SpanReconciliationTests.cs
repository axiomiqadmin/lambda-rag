using FluentAssertions;
using LambdaRag.Core.Ontology;
using Xunit;
using static LambdaRag.UnitTests.Ontology.SpanFixture;

namespace LambdaRag.UnitTests.Ontology;

public sealed class SpanReconciliationTests
{
    private readonly DeterministicSpanReconciler _reconciler = new();

    private SpanReconciliationResultSet Reconcile(
        IReadOnlyList<RawSpan> oldDoc,
        IReadOnlyList<RawSpan> newDoc,
        SpanReconciliationPolicy policy = SpanReconciliationPolicy.Strict,
        string oldParser = "1.0.0",
        string newParser = "1.0.0")
    {
        var prev = Seed(oldDoc, Identity("v1", oldParser));
        var curr = Observe(newDoc);
        var set = _reconciler.Reconcile(prev, curr, Identity("v2", newParser), policy);
        AssertEverySpanAccountedForOnce(prev.Count, curr.Count, set);
        return set;
    }

    private static void AssertEverySpanAccountedForOnce(int prevCount, int currCount, SpanReconciliationResultSet set)
    {
        var prevOrdinals = set.Results.Where(r => r.PreviousOrdinal is not null)
            .Select(r => r.PreviousOrdinal!.Value).OrderBy(x => x).ToList();
        var currOrdinals = set.Results.Where(r => r.CurrentOrdinal is not null)
            .Select(r => r.CurrentOrdinal!.Value).OrderBy(x => x).ToList();

        prevOrdinals.Should().Equal(Enumerable.Range(0, prevCount));
        currOrdinals.Should().Equal(Enumerable.Range(0, currCount));
        set.CurrentRecords.Should().HaveCount(currCount);
    }

    private static SpanReconciliationResult ForCurrent(SpanReconciliationResultSet set, int ordinal)
        => set.Results.Single(r => r.CurrentOrdinal == ordinal);

    private static SpanReconciliationResult ForPrevious(SpanReconciliationResultSet set, int ordinal)
        => set.Results.Single(r => r.PreviousOrdinal == ordinal && r.CurrentOrdinal is null);

    [Fact]
    public void Identical_rerun_classifies_every_span_unchanged()
    {
        var doc = Doc(H(1, "Title"), B("alpha"), B("beta"), B("gamma"));
        var set = Reconcile(doc, doc);

        set.Results.Should().OnlyContain(r => r.Classification == SpanClassification.Unchanged);
        set.HasUnresolved.Should().BeFalse();
    }

    [Fact]
    public void Paragraph_inserted_near_beginning_is_added_and_others_keep_identity()
    {
        var oldDoc = Doc(H(1, "Title"), B("alpha"), B("beta"), B("gamma"));
        var newDoc = Doc(H(1, "Title"), B("inserted"), B("alpha"), B("beta"), B("gamma"));
        var prev = Seed(oldDoc, Identity("v1"));
        var set = _reconciler.Reconcile(prev, Observe(newDoc), Identity("v2"));

        ForCurrent(set, 1).Classification.Should().Be(SpanClassification.Added);
        set.Results.Count(r => r.Classification == SpanClassification.Added).Should().Be(1);

        // alpha/beta/gamma keep their previous identities.
        var alpha = prev[1].InstanceId;
        ForCurrent(set, 2).ResultingInstanceId.Should().Be(alpha);
        ForCurrent(set, 2).Classification.Should().Be(SpanClassification.Unchanged);
    }

    [Fact]
    public void Paragraph_deleted_near_beginning_is_removed_and_others_keep_identity()
    {
        var oldDoc = Doc(H(1, "Title"), B("alpha"), B("beta"), B("gamma"));
        var newDoc = Doc(H(1, "Title"), B("alpha"), B("gamma"));
        var set = Reconcile(oldDoc, newDoc);

        ForPrevious(set, 2).Classification.Should().Be(SpanClassification.Removed); // beta
        set.Results.Count(r => r.Classification == SpanClassification.Removed).Should().Be(1);
        set.Results.Where(r => r.CurrentOrdinal is not null)
            .Should().OnlyContain(r => r.Classification == SpanClassification.Unchanged);
    }

    [Fact]
    public void Insertion_produces_local_effect_not_global_identity_churn()
    {
        // Insert AFTER alpha: only alpha.next and beta.prev contexts change.
        var oldDoc = Doc(H(1, "Title"), B("alpha"), B("beta"), B("gamma"));
        var newDoc = Doc(H(1, "Title"), B("alpha"), B("x"), B("beta"), B("gamma"));
        var prev = Seed(oldDoc, Identity("v1"));
        var set = _reconciler.Reconcile(prev, Observe(newDoc), Identity("v2"));
        AssertEverySpanAccountedForOnce(prev.Count, Observe(newDoc).Count, set);

        set.Results.Count(r => r.Classification == SpanClassification.Added).Should().Be(1);

        // Every surviving span keeps its original identity (no churn).
        ForCurrent(set, 0).ResultingInstanceId.Should().Be(prev[0].InstanceId); // heading
        ForCurrent(set, 1).ResultingInstanceId.Should().Be(prev[1].InstanceId); // alpha
        ForCurrent(set, 3).ResultingInstanceId.Should().Be(prev[2].InstanceId); // beta
        ForCurrent(set, 4).ResultingInstanceId.Should().Be(prev[3].InstanceId); // gamma
        set.HasUnresolved.Should().BeFalse();
    }

    [Fact]
    public void Unique_paragraph_moved_within_heading_is_classified_moved_within()
    {
        var oldDoc = Doc(H(1, "Title"), B("alpha"), B("beta"), B("gamma"));
        var newDoc = Doc(H(1, "Title"), B("beta"), B("alpha"), B("gamma"));
        var set = Reconcile(oldDoc, newDoc);

        set.Results.Count(r => r.Classification == SpanClassification.MovedWithinHeading).Should().Be(2);
        set.Results.Should().NotContain(r => r.Classification == SpanClassification.MovedCrossHeading);
    }

    [Fact]
    public void Unique_paragraph_moved_across_headings_is_classified_moved_cross()
    {
        var oldDoc = Doc(H(1, "One"), B("mobile"), H(1, "Two"));
        var newDoc = Doc(H(1, "One"), H(1, "Two"), B("mobile"));
        var set = Reconcile(oldDoc, newDoc);

        set.Results.Single(r => r.Classification == SpanClassification.MovedCrossHeading)
            .CurrentOrdinal.Should().Be(2);
    }

    [Fact]
    public void Heading_renamed_with_body_unchanged_keeps_body_and_replaces_heading()
    {
        var oldDoc = Doc(H(1, "Old Title"), B("body text"));
        var newDoc = Doc(H(1, "New Title"), B("body text"));
        var prev = Seed(oldDoc, Identity("v1"));
        var set = _reconciler.Reconcile(prev, Observe(newDoc), Identity("v2"));

        ForCurrent(set, 0).Classification.Should().Be(SpanClassification.UnresolvedReplacement);
        ForCurrent(set, 0).ResultingInstanceId.Should().NotBe(prev[0].InstanceId);
        ForCurrent(set, 1).Classification.Should().Be(SpanClassification.Unchanged);
        ForCurrent(set, 1).ResultingInstanceId.Should().Be(prev[1].InstanceId);
    }

    [Fact]
    public void Body_replacement_in_same_slot_becomes_unresolved_replacement()
    {
        var oldDoc = Doc(H(1, "Title"), B("original body"));
        var newDoc = Doc(H(1, "Title"), B("completely different body"));
        var prev = Seed(oldDoc, Identity("v1"));
        var set = _reconciler.Reconcile(prev, Observe(newDoc), Identity("v2"));

        var r = ForCurrent(set, 1);
        r.Classification.Should().Be(SpanClassification.UnresolvedReplacement);
        r.PreviousInstanceId.Should().Be(prev[1].InstanceId);
        r.ResultingInstanceId.Should().NotBe(prev[1].InstanceId);
        r.Diagnostics.Should().Contain("same-structural-slot-different-content");
    }

    [Fact]
    public void Duplicate_paragraphs_under_one_heading_are_unresolved_in_strict_mode()
    {
        var doc = Doc(H(1, "Title"), B("dup"), B("dup"), B("dup"), B("dup"));
        var set = Reconcile(doc, doc, SpanReconciliationPolicy.Strict);

        set.HasUnresolved.Should().BeTrue();
        set.Results.Count(r => r.Classification == SpanClassification.Unresolved).Should().BeGreaterThan(0);
    }

    [Fact]
    public void Duplicate_paragraphs_under_one_heading_are_positionally_assigned_in_opt_in_mode()
    {
        var doc = Doc(H(1, "Title"), B("dup"), B("dup"), B("dup"), B("dup"));
        var set = Reconcile(doc, doc, SpanReconciliationPolicy.PositionalEquivalent);

        set.Results.Should().NotContain(r => r.Classification == SpanClassification.Unresolved);
        set.Results.Should().Contain(r => r.Diagnostics.Contains("PositionalDuplicateAssignment"));
    }

    [Fact]
    public void Duplicate_paragraphs_under_different_headings_are_disambiguated_by_ancestry()
    {
        var doc = Doc(H(1, "One"), B("same"), H(1, "Two"), B("same"));
        var set = Reconcile(doc, doc, SpanReconciliationPolicy.Strict);

        set.HasUnresolved.Should().BeFalse();
        set.Results.Should().OnlyContain(r => r.Classification == SpanClassification.Unchanged);
    }

    [Fact]
    public void Paragraph_split_mints_all_new_identities_with_relationships()
    {
        var oldDoc = Doc(H(1, "Title"), B("Hello world"));
        var newDoc = Doc(H(1, "Title"), B("Hello "), B("world"));
        var prev = Seed(oldDoc, Identity("v1"));
        var set = _reconciler.Reconcile(prev, Observe(newDoc), Identity("v2"));

        var fragments = set.Results.Where(r => r.Classification == SpanClassification.SplitFrom).ToList();
        fragments.Should().HaveCount(2);
        fragments.Should().OnlyContain(r => r.ResultingInstanceId != prev[1].InstanceId);
        fragments.Should().OnlyContain(r => r.Relationships.Any(x => x.Type == SpanRelationshipType.SplitFrom));

        var source = ForPrevious(set, 1);
        source.Classification.Should().Be(SpanClassification.Removed);
        source.ReasonCode.Should().Be("retired-by-split");
        source.Relationships.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_mints_new_identity_with_relationships()
    {
        var oldDoc = Doc(H(1, "Title"), B("Hello "), B("world"));
        var newDoc = Doc(H(1, "Title"), B("Hello world"));
        var prev = Seed(oldDoc, Identity("v1"));
        var set = _reconciler.Reconcile(prev, Observe(newDoc), Identity("v2"));

        var merged = set.Results.Single(r => r.Classification == SpanClassification.MergedFrom);
        merged.ResultingInstanceId.Should().NotBe(prev[1].InstanceId);
        merged.ResultingInstanceId.Should().NotBe(prev[2].InstanceId);
        merged.Relationships.Should().HaveCount(2);

        set.Results.Count(r => r.Classification == SpanClassification.Removed
                               && r.ReasonCode == "retired-by-merge").Should().Be(2);
    }

    [Fact]
    public void Nfc_and_nfd_equivalent_bodies_reconcile_as_unchanged()
    {
        var oldDoc = Doc(H(1, "Title"), B("caf\u00e9"));
        var newDoc = Doc(H(1, "Title"), B("cafe\u0301"));
        var set = Reconcile(oldDoc, newDoc);

        set.Results.Should().OnlyContain(r => r.Classification == SpanClassification.Unchanged);
    }

    [Fact]
    public void Formatting_only_change_with_different_offsets_is_unchanged()
    {
        var oldDoc = Doc(H(1, "Title"), B("alpha"), B("beta"));
        var newDoc = DocShifted(H(1, "Title"), B("alpha"), B("beta")); // identical text, different provenance
        var prev = Seed(oldDoc, Identity("v1"));
        var set = _reconciler.Reconcile(prev, Observe(newDoc), Identity("v2"));

        set.Results.Should().OnlyContain(r => r.Classification == SpanClassification.Unchanged);
        ForCurrent(set, 1).ResultingInstanceId.Should().Be(prev[1].InstanceId);
    }

    [Fact]
    public void Parser_version_mismatch_fails_closed()
    {
        var oldDoc = Doc(H(1, "Title"), B("alpha"));
        var newDoc = Doc(H(1, "Title"), B("alpha"));
        var prev = Seed(oldDoc, Identity("v1", "1.0.0"));

        Action act = () => _reconciler.Reconcile(prev, Observe(newDoc), Identity("v2", "2.0.0"));
        act.Should().Throw<ParserVersionMismatchException>();
    }

    [Fact]
    public void Control_characters_in_span_text_are_rejected()
    {
        var raw = new[] { new RawSpan(SpanKind.Body, 0, "bad\u0007text", 0, 8) };
        Action act = () => SpanObservationBuilder.Build(raw);
        act.Should().Throw<OntologyCanonicalException>();
    }

    [Fact]
    public void Reconciliation_canonical_bytes_are_identical_across_consecutive_replays()
    {
        var oldDoc = Doc(H(1, "Title"), B("alpha"), B("beta"));
        var newDoc = Doc(H(1, "Title"), B("alpha"), B("inserted"), B("beta"));

        var first = Reconcile(oldDoc, newDoc);
        var second = Reconcile(oldDoc, newDoc);
        first.ToCanonicalBytes().Should().Equal(second.ToCanonicalBytes());
    }

    [Fact]
    public void Candidate_order_variation_produces_identical_canonical_output()
    {
        var oldDoc = Doc(H(1, "Title"), B("alpha"), B("beta"), B("gamma"));
        var newDoc = Doc(H(1, "Title"), B("gamma"), B("alpha"), B("beta"));
        var set = Reconcile(oldDoc, newDoc);

        var reversed = set with { Results = set.Results.Reverse().ToList() };
        set.ToCanonicalBytes().Should().Equal(reversed.ToCanonicalBytes());
    }
}
