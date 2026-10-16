using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using FluentAssertions;
using LambdaRag.Core.Domain;
using LambdaRag.Markup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LambdaRag.UnitTests.Markup;

/// <summary>
/// Reopens the written DOCX and asserts the comment ranges and w:ins / w:del
/// targets (not just that a file exists), plus fail-closed behavior and
/// observable application results.
/// </summary>
public sealed class DocxTrackedChangeVerificationTests : IDisposable
{
    private const string DocId = "doc";

    private static readonly string[] Paras =
    {
        "1. Scope",                                              // 0..8
        "The vendor shall retain logs for thirty days.",         // 9..54
        "Backups are performed weekly.",
    };

    private readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "docx-verify-" + Guid.NewGuid().ToString("N")[..10]);

    public DocxTrackedChangeVerificationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static int OffsetOf(int paragraph, string needle)
    {
        var offset = 0;
        for (var i = 0; i < paragraph; i++) offset += Paras[i].Length + 1;
        return offset + Paras[paragraph].IndexOf(needle, StringComparison.Ordinal);
    }

    private string NewDoc(string name = "in.docx")
    {
        var path = Path.Combine(_dir, name);
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        main.Document = new Document(body);
        foreach (var t in Paras)
            body.AppendChild(new Paragraph(new Run(new Text(t) { Space = SpaceProcessingModeValues.Preserve })));
        main.Document.Save();
        return path;
    }

    private static Annotation Comment(string id, int start, int len, string text = "c") => new(
        id, AnnotationKind.Comment, new SourceSpan(DocId, start, len, null, null), "🕵 - Test guidance", text, null);

    private static Annotation Replace(string id, int start, int len, string replacement, SourceSpan? clause = null) => new(
        id, AnnotationKind.Replace, new SourceSpan(DocId, start, len, null, null), "🕵 - Test guidance", "c", replacement)
    {
        ClauseSpan = clause,
    };

    private static MarkupApplyResult Run(string path, params Annotation[] annotations)
        => new OpenXmlMarkupService(NullLogger<OpenXmlMarkupService>.Instance).Apply(path, path, annotations);

    // Text of everything (live + deleted) strictly between the comment range markers.
    private static string CommentRangeText(Body body, string commentId)
    {
        var sb = new System.Text.StringBuilder();
        var inside = false;
        foreach (var el in body.Descendants())
        {
            if (el is CommentRangeStart s && s.Id?.Value == commentId) inside = true;
            else if (el is CommentRangeEnd e && e.Id?.Value == commentId) break;
            else if (inside && el is Text t && el.Ancestors<InsertedRun>().Any() == false) sb.Append(t.Text);
            else if (inside && el is DeletedText d) sb.Append(d.Text);
        }
        return sb.ToString();
    }

    [Fact]
    public void Comment_range_covers_exactly_the_evidence_text()
    {
        var path = NewDoc();
        var start = OffsetOf(1, "thirty days");
        var result = Run(path, Comment("a1", start, "thirty days".Length));

        result.IsClean.Should().BeTrue();
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        CommentRangeText(body, "1").Should().Be("thirty days");
        body.Descendants<InsertedRun>().Should().BeEmpty();
        body.Descendants<DeletedRun>().Should().BeEmpty();
    }

    [Fact]
    public void Valid_replace_deletes_exactly_the_target_and_inserts_the_replacement_preserving_surroundings()
    {
        var path = NewDoc();
        var start = OffsetOf(1, "thirty days");
        var result = Run(path, Replace("r1", start, "thirty days".Length, "ninety days"));

        result.IsClean.Should().BeTrue();
        result.TrackedChangesApplied.Should().Be(1);
        result.Results.Single().TargetText.Should().Be("thirty days");

        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        body.Descendants<DeletedText>().Select(d => d.Text).Should().Equal("thirty days");
        body.Descendants<InsertedRun>().SelectMany(r => r.Descendants<Text>()).Select(t => t.Text)
            .Should().Equal("ninety days");
        CommentRangeText(body, "1").Should().Be("thirty days");

        var p = body.Elements<Paragraph>().ElementAt(1);
        var liveBefore = string.Concat(p.Elements<Run>().Select(r => r.InnerText));
        liveBefore.Should().Contain("The vendor shall retain logs for ").And.EndWith(".");
        body.Elements<Paragraph>().Select(x => x.InnerText).Should().Contain("Backups are performed weekly.");
        body.Elements<Paragraph>().Count().Should().Be(Paras.Length, "no paragraph may be added or removed for a one-line rewrite");
    }

    [Fact]
    public void Multi_paragraph_clause_replace_strikes_only_the_clause_paragraphs()
    {
        var path = NewDoc();
        var start = OffsetOf(1, "The vendor");
        var end = OffsetOf(2, "Backups") + Paras[2].Length;
        var clause = new SourceSpan(DocId, start, end - start, null, null);
        var result = Run(path, Replace("r2", start, 10, "Logs and backups are governed by Schedule B.", clause));

        result.IsClean.Should().BeTrue();
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        string.Concat(body.Descendants<DeletedText>().Select(d => d.Text))
            .Should().Be(Paras[1] + Paras[2]);
        body.Elements<Paragraph>().First().Descendants<DeletedRun>().Should().BeEmpty("the heading is outside the clause");
        CommentRangeText(body, "1").Should().Be(Paras[1] + Paras[2]);
    }

    public static IEnumerable<object[]> UnsafeReplacements()
    {
        yield return new object[] { "NO_REWRITE", MarkupReasons.ReplacementDeclined };
        yield return new object[] { "   ", MarkupReasons.ReplacementMissing };
        yield return new object[] { "thirty days", MarkupReasons.ReplacementIdentical };
        yield return new object[] { "{\"rewrite\":\"ninety days\"}", MarkupReasons.ReplacementInvalidFormat };
        yield return new object[] { new string('x', 2000), MarkupReasons.ReplacementOverBroad };
    }

    [Theory]
    [MemberData(nameof(UnsafeReplacements))]
    public void Invalid_replacements_stay_comment_only_with_a_reason(string replacement, string expectedReason)
    {
        var path = NewDoc();
        var start = OffsetOf(1, "thirty days");
        var result = Run(path, Replace("r3", start, "thirty days".Length, replacement));

        result.IsClean.Should().BeFalse();
        var r = result.Results.Single();
        r.Disposition.Should().Be(MarkupDisposition.CommentOnly);
        r.Reason.Should().Be(expectedReason);
        result.Skips.Should().HaveCount(1);

        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        body.Descendants<InsertedRun>().Should().BeEmpty();
        body.Descendants<DeletedRun>().Should().BeEmpty();
        body.Descendants<DeletedText>().Should().BeEmpty();
        CommentRangeText(body, "1").Should().Be("thirty days", "the finding is still commented");
        string.Concat(body.Elements<Paragraph>().Select(p => p.InnerText))
            .Should().Be(string.Concat(Paras));
    }

    [Fact]
    public void Clause_extending_past_the_document_is_comment_only_target_missing()
    {
        var path = NewDoc();
        var start = OffsetOf(1, "thirty days");
        var clause = new SourceSpan(DocId, start, 100_000, null, null);
        var result = Run(path, Replace("r4", start, 11, "ninety days", clause));

        var r = result.Results.Single();
        r.Disposition.Should().Be(MarkupDisposition.CommentOnly);
        r.Reason.Should().Be(MarkupReasons.TargetMissing);
        using var doc = WordprocessingDocument.Open(path, false);
        doc.MainDocumentPart!.Document!.Body!.Descendants<DeletedText>().Should().BeEmpty();
    }

    [Fact]
    public void Evidence_outside_the_clause_span_is_comment_only_target_ambiguous()
    {
        var path = NewDoc();
        var start = OffsetOf(0, "1. Scope");
        var evidenceOutside = new Annotation(
            "r5", AnnotationKind.Replace, new SourceSpan(DocId, OffsetOf(2, "Backups"), 7, null, null),
            "a", "c", "Replacement text here.")
        {
            ClauseSpan = new SourceSpan(DocId, start, Paras[0].Length, null, null),
        };
        var result = Run(path, evidenceOutside);

        result.Results.Single().Reason.Should().Be(MarkupReasons.TargetAmbiguous);
    }

    [Fact]
    public void Unanchorable_span_is_reported_as_skipped_not_silently_clean()
    {
        var path = NewDoc();
        var result = Run(path, Comment("a9", 99_999, 4));

        result.IsClean.Should().BeFalse();
        result.Skipped.Should().ContainSingle().Which.Reason.Should().Be(MarkupReasons.CommentTargetNotFound);
        using var doc = WordprocessingDocument.Open(path, false);
        doc.MainDocumentPart!.Document!.Body!.Descendants<CommentRangeStart>().Should().BeEmpty();
    }

    [Fact]
    public void Two_comments_on_separate_occurrences_each_cover_their_own_text()
    {
        var path = NewDoc();
        var a = OffsetOf(1, "retain");
        var b = OffsetOf(2, "weekly");
        var result = Run(path, Comment("c1", a, 6, "first"), Comment("c2", b, 6, "second"));

        result.IsClean.Should().BeTrue();
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        CommentRangeText(body, "1").Should().Be("retain");
        CommentRangeText(body, "2").Should().Be("weekly");
        doc.MainDocumentPart.WordprocessingCommentsPart!.Comments!.Elements<Comment>()
            .Select(c => c.InnerText).Should().Equal("first", "second");
    }

    private static ComplianceReport ReportOf(params Verdict[] verdicts) => new(
        new LambdaRag.Core.Hashing.ContentHash(DocId), "rs", "1", LambdaRag.Core.Hashing.ContentHash.OfString("fp"),
        "p", "1", 0, verdicts.Length, 0, 0, 0, 0, verdicts, DateTimeOffset.UnixEpoch);

    private static Verdict GapVerdict(string id, string ruleId, SourceSpan span, string? anchorKind = AnchorKinds.SectionScope) => new(
        id, ruleId, "1", VerdictOutcome.Gap, "true", new System.Text.Json.Nodes.JsonObject(), span, null,
        Array.Empty<string>(), DateTimeOffset.UnixEpoch)
    {
        AnchorKind = anchorKind,
        RewriteEligible = false,
        RemediationText = "Document does not address: log retention.",
    };

    private static Rule RuleOf(string id, string nl) => new(
        id, "1.0.0", nl, "true", new System.Text.Json.Nodes.JsonObject(),
        new LambdaRag.Core.Selectors.PathSelector("$.sections[*]"), RuleSeverity.Deviation,
        new SourceSpan("policy", 0, 0, 1, null), id, new Dictionary<string, string>())
    {
        Predicate = "true",
    };

    [Fact]
    public void Scoped_gap_is_a_comment_only_section_anchor_and_leaves_the_summary()
    {
        var path = NewDoc();
        var section = new SourceSpan(DocId, OffsetOf(1, "The vendor"), Paras[1].Length, null, null);
        var rules = new Dictionary<string, Rule>
        {
            ["G-1"] = RuleOf("G-1", "Backups MUST be encrypted."),
            ["G-2"] = RuleOf("G-2", "Offsite copies MUST exist."),
        };
        var report = ReportOf(
            GapVerdict("v1", "G-1", section),
            GapVerdict("v2", "G-1", section),                                   // duplicate rule+section
            GapVerdict("v3", "G-2", new SourceSpan("policy", 0, 30, 1, null)),  // not a reviewed-doc span
            GapVerdict("v4", "G-2", section, anchorKind: AnchorKinds.Unresolved));

        var scoped = AnnotationFactory.BuildScopedGapAnnotations(report, rules);
        scoped.Should().ContainSingle();
        var a = scoped[0];
        a.Kind.Should().Be(AnnotationKind.Comment);
        a.Replacement.Should().BeNull();
        a.ClauseSpan.Should().BeNull();
        a.AnchorKind.Should().Be(AnnotationAnchorKind.ScopedSection);
        a.Reasons.Should().Contain("anchor:scoped-section:missing-requirement");
        a.Text.Should().Contain("Missing Requirement").And.Contain("not contradictory text")
            .And.Contain("[Policy Reference: G-1 v1.0.0]");

        var summary = AnnotationFactory.BuildGapsSummary(report, rules)!;
        summary.RuleIds.Should().Equal("G-2");
        summary.Text.Should().NotContain("G-1").And.Contain("G-2");
        summary.Text.Split("[").Length.Should().Be(2, "G-2 listed once despite two gap verdicts");

        AnnotationFactory.FromReport(report, rules).Should().Contain(x => x.Id == a.Id);

        var result = Run(path, a);
        result.IsClean.Should().BeTrue();
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart!.Document!.Body!;
        CommentRangeText(body, "1").Should().Be(Paras[1]);
        body.Descendants<InsertedRun>().Should().BeEmpty();
        body.Descendants<DeletedText>().Should().BeEmpty();
        doc.MainDocumentPart.WordprocessingCommentsPart!.Comments!.Elements<Comment>().Single().InnerText
            .Should().Contain("Missing Requirement in This Section");
    }

    [Fact]
    public void Gap_summary_is_omitted_when_every_gap_has_a_section_anchor()
    {
        var section = new SourceSpan(DocId, 9, 20, null, null);
        var rules = new Dictionary<string, Rule> { ["G-1"] = RuleOf("G-1", "Backups MUST be encrypted.") };
        AnnotationFactory.BuildGapsSummary(ReportOf(GapVerdict("v1", "G-1", section)), rules).Should().BeNull();
    }

    [Fact]
    public void Markup_is_byte_deterministic_across_runs()
    {
        var start = OffsetOf(1, "thirty days");
        byte[] Produce(string name)
        {
            var path = NewDoc(name);
            Run(path,
                Replace("r1", start, 11, "ninety days"),
                Comment("c2", OffsetOf(2, "weekly"), 6, "second"));
            using var doc = WordprocessingDocument.Open(path, false);
            using var ms = new MemoryStream();
            using (var s = doc.MainDocumentPart!.GetStream()) s.CopyTo(ms);
            using (var s = doc.MainDocumentPart.WordprocessingCommentsPart!.GetStream()) s.CopyTo(ms);
            return ms.ToArray();
        }

        Produce("one.docx").Should().Equal(Produce("two.docx"));
    }
}
