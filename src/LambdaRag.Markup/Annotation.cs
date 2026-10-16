using LambdaRag.Core.Domain;
using LambdaRag.Core.Hashing;

namespace LambdaRag.Markup;

/// <summary>
/// A single annotation derived from a Verdict that the markup engine knows
/// how to render. Stable Id (derived from verdict id) means re-running over
/// the same input produces byte-identical artifacts.
///
/// <para>
/// <see cref="Span"/> is the narrow *evidence* anchor — used for the
/// reviewer comment marker so the highlight in Word's review pane lands on
/// the offending substring. <see cref="ClauseSpan"/>, when non-null,
/// widens tracked-change deletions / replacements to the enclosing
/// paragraph(s). Issue #87 introduced this split so multi-paragraph
/// clauses get fully struck through instead of partially.
/// </para>
/// </summary>
public sealed record Annotation(
    string Id,
    AnnotationKind Kind,
    SourceSpan Span,
    string Author,
    string Text,
    string? Replacement)
{
    /// <summary>
    /// Optional paragraph- or section-aligned span used to widen
    /// tracked-change deletions / replacements. Null = the markup engine
    /// clamps the change to the single paragraph that contains
    /// <see cref="Span"/> (pre-#87 behaviour, preserved for backward
    /// compatibility). Only meaningful for
    /// <see cref="AnnotationKind.Delete"/> and
    /// <see cref="AnnotationKind.Replace"/>; ignored for
    /// <see cref="AnnotationKind.Comment"/> and
    /// <see cref="AnnotationKind.Insert"/>.
    /// </summary>
    public SourceSpan? ClauseSpan { get; init; }

    /// <summary>
    /// Rule ids this annotation reports on. More than one when findings
    /// were coalesced (same normalized obligation, same target location).
    /// Empty for annotations that are not tied to a rule verdict.
    /// </summary>
    public IReadOnlyList<string> RuleIds { get; init; } = Array.Empty<string>();

    /// <summary>Ids of every verdict folded into this annotation.</summary>
    public IReadOnlyList<string> VerdictIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Stable, auditable reason codes explaining why a finding was grouped,
    /// left unanchored, or had its rewrite withheld (e.g.
    /// <c>grouped:same-obligation-same-location:rules=A,B</c>,
    /// <c>unanchored:document-level-gap</c>,
    /// <c>rewrite-withheld:shared-clause-span</c>). Empty when nothing
    /// noteworthy happened.
    /// </summary>
    public IReadOnlyList<string> Reasons { get; init; } = Array.Empty<string>();

    /// <summary>What <see cref="Span"/> represents.</summary>
    public AnnotationAnchorKind AnchorKind { get; init; } = AnnotationAnchorKind.Evidence;
}

/// <summary>Describes how an annotation's span relates to the finding.</summary>
public enum AnnotationAnchorKind
{
    /// <summary>Span overlaps the text that evidences the finding.</summary>
    Evidence,
    /// <summary>
    /// Span is a scoped section anchor: the finding is a missing requirement
    /// or no quote backing the span was located, so the span must not be
    /// read as contradictory evidence.
    /// </summary>
    ScopedSection,
    /// <summary>Document-level gap summary anchored at the top of the document.</summary>
    DocumentLevelGap,
}

/// <summary>
/// One or more verdicts coalesced into a single reviewer comment because
/// they share a normalized obligation and a target location.
/// </summary>
public sealed record FindingGroup(
    string Key,
    Annotation Comment,
    IReadOnlyList<Verdict> Verdicts)
{
    /// <summary>First verdict (ordinal by rule id) — drives rewrite input.</summary>
    public Verdict Representative => Verdicts[0];

    public IReadOnlyList<string> RuleIds => Comment.RuleIds;

    public bool IsGrouped => Verdicts.Count > 1;

    /// <summary>
    /// True when another group (a distinct obligation) targets the same
    /// clause span. A rewrite for either would be incompatible with the
    /// other, so callers must keep such groups comment-only.
    /// </summary>
    public bool ClauseSharedWithOtherGroups { get; init; }
}

public enum AnnotationKind
{
    /// <summary>A reviewer comment anchored to the span.</summary>
    Comment,
    /// <summary>A tracked-change deletion.</summary>
    Delete,
    /// <summary>A tracked-change insertion (Replacement is the new text).</summary>
    Insert,
    /// <summary>A tracked-change replacement (delete + insert at same span).</summary>
    Replace,
}

public static class AnnotationFactory
{
    /// <summary>
    /// Generic fallback author label, used when a verdict has no
    /// matching rule definition (the rule was disabled / removed
    /// between report and markup) and category resolution can't run.
    /// Format mirrors <see cref="CommentFormatting.BuildAuthor"/> so the
    /// reviewer always sees the 🕵 prefix in the Word review pane.
    /// </summary>
    public const string Author =
        CommentFormatting.AuthorEmojiPrefix + CommentFormatting.GenericLabel + " guidance";

    /// <summary>
    /// Default mapping: every Failed / Error verdict becomes a Comment
    /// anchored to the matched section, with a category-derived author
    /// (e.g. <c>"🕵 - Legal guidance"</c>), an CTSO-style severity banner,
    /// the rule's optional plain-English synopsis, the natural-language
    /// statement, and (when present) the rendered remediation text.
    ///
    /// Gap verdicts are deliberately excluded here — they are not tied to
    /// a specific paragraph in the reviewed document. Use
    /// <see cref="BuildGapsSummary"/> to render them as a single summary
    /// comment anchored to the top of the document.
    /// </summary>
    public static IEnumerable<Annotation> FromReport(ComplianceReport report, IReadOnlyDictionary<string, Rule> rules)
    {
        foreach (var g in GroupFindings(report, rules))
            yield return g.Comment;
        foreach (var a in BuildScopedGapAnnotations(report, rules))
            yield return a;
    }

    /// <summary>
    /// True for an in-scope omission the evaluator anchored to a section of
    /// the reviewed document (<c>AnchorKind=section_scope</c>) whose span
    /// belongs to <paramref name="reviewedDocumentId"/> and is non-empty.
    /// Such gaps are rendered as comment-only scoped-gap annotations; all
    /// other gaps stay in the document-level summary.
    /// </summary>
    public static bool IsScopedGap(Verdict v, string reviewedDocumentId)
        => v.Outcome == VerdictOutcome.Gap
           && string.Equals(v.AnchorKind, AnchorKinds.SectionScope, StringComparison.Ordinal)
           && v.SourceSpan.CharLength > 0
           && string.Equals(v.SourceSpan.DocumentId, reviewedDocumentId, StringComparison.Ordinal);

    /// <summary>
    /// Comment-only annotations for scoped gaps, coalesced by normalized
    /// obligation + section span. The body states plainly that the
    /// requirement is MISSING from the anchored section; the span is a
    /// scope anchor, never contradictory evidence. No rewrite is ever
    /// requested (no clause span, no replacement).
    /// </summary>
    public static IReadOnlyList<Annotation> BuildScopedGapAnnotations(
        ComplianceReport report, IReadOnlyDictionary<string, Rule> rules)
    {
        var order = new List<string>();
        var buckets = new Dictionary<string, List<Verdict>>(StringComparer.Ordinal);
        foreach (var v in report.Verdicts)
        {
            if (!IsScopedGap(v, report.DocumentId.Value)) continue;
            var key = NormalizeObligation(rules.GetValueOrDefault(v.RuleId), v.RuleId) + "|" + SpanKey(v.SourceSpan);
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<Verdict>();
                buckets[key] = list;
                order.Add(key);
            }
            list.Add(v);
        }

        var result = new List<Annotation>(order.Count);
        foreach (var key in order)
        {
            var members = buckets[key]
                .OrderBy(v => v.RuleId, StringComparer.Ordinal)
                .ThenBy(v => v.Id, StringComparer.Ordinal)
                .ToList();
            var first = members[0];
            var firstRule = rules.GetValueOrDefault(first.RuleId);
            var ruleIds = members.Select(m => m.RuleId).Distinct(StringComparer.Ordinal).ToList();
            var ids = members.Select(m => m.Id).ToList();

            var reasons = new List<string> { "anchor:scoped-section:missing-requirement" };
            if (ruleIds.Count > 1)
                reasons.Add("grouped:same-obligation-same-location:rules=" + string.Join(",", ruleIds)
                    + ";verdicts=" + members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

            result.Add(new Annotation(
                Id: ContentHash.Compose("annot", "scoped-gap", string.Join("|", ids)).Value,
                Kind: AnnotationKind.Comment,
                Span: first.SourceSpan,
                Author: firstRule is null ? Author : CommentFormatting.BuildAuthor(firstRule),
                Text: CommentFormatting.BuildScopedGapBody(
                    members.Select(m => (m, rules.GetValueOrDefault(m.RuleId))).ToList()),
                Replacement: null)
            {
                RuleIds = ruleIds,
                VerdictIds = ids,
                Reasons = reasons,
                AnchorKind = AnnotationAnchorKind.ScopedSection,
            });
        }
        return result;
    }

    /// <summary>
    /// Coalesces Fail / Error verdicts into <see cref="FindingGroup"/>s keyed
    /// by (outcome, normalized obligation, evidence span, clause span).
    /// Findings that share all four become ONE comment that keeps every rule
    /// reference and remediation; distinct obligations, distinct locations
    /// (including separate occurrences of the same rule) stay separate.
    /// Never dedupes by rule id. Group order follows first appearance in the
    /// report, so output is deterministic.
    /// </summary>
    public static IReadOnlyList<FindingGroup> GroupFindings(
        ComplianceReport report, IReadOnlyDictionary<string, Rule> rules)
        => GroupFindings(report.Verdicts, rules);

    public static IReadOnlyList<FindingGroup> GroupFindings(
        IEnumerable<Verdict> verdicts, IReadOnlyDictionary<string, Rule> rules)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        ArgumentNullException.ThrowIfNull(rules);

        var order = new List<string>();
        var buckets = new Dictionary<string, List<Verdict>>(StringComparer.Ordinal);
        foreach (var v in verdicts)
        {
            if (v.Outcome is not VerdictOutcome.Fail and not VerdictOutcome.Error) continue;
            var rule = rules.GetValueOrDefault(v.RuleId);
            var key = string.Join('|',
                v.Outcome.ToString(),
                NormalizeObligation(rule, v.RuleId),
                SpanKey(v.SourceSpan),
                v.ClauseSpan is null ? "-" : SpanKey(v.ClauseSpan));
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<Verdict>();
                buckets[key] = list;
                order.Add(key);
            }
            list.Add(v);
        }

        var groups = new List<FindingGroup>(order.Count);
        foreach (var key in order)
        {
            var members = buckets[key]
                .OrderBy(v => v.RuleId, StringComparer.Ordinal)
                .ThenBy(v => v.Id, StringComparer.Ordinal)
                .ToList();
            groups.Add(new FindingGroup(key, BuildGroupComment(members, rules), members));
        }

        var clauseShare = groups
            .GroupBy(g => ClauseKey(g.Representative), StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .SelectMany(x => x)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.Ordinal);
        if (clauseShare.Count == 0) return groups;
        return groups
            .Select(g => clauseShare.Contains(g.Key) ? g with { ClauseSharedWithOtherGroups = true } : g)
            .ToList();
    }

    private static string SpanKey(SourceSpan s)
        => $"{s.CharStart.ToString(System.Globalization.CultureInfo.InvariantCulture)}+{s.CharLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

    private static string ClauseKey(Verdict v) => SpanKey(v.ClauseSpan ?? v.SourceSpan);

    /// <summary>
    /// Lower-cased, punctuation-stripped, whitespace-collapsed obligation
    /// text. Rules without a natural-language statement (or missing rules)
    /// are keyed by rule id so they never merge with another control.
    /// </summary>
    public static string NormalizeObligation(Rule? rule, string ruleId)
    {
        var nl = rule?.NaturalLanguage;
        if (string.IsNullOrWhiteSpace(nl)) return "rule:" + ruleId;
        var sb = new System.Text.StringBuilder(nl.Length);
        var pendingSpace = false;
        foreach (var ch in nl)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && sb.Length > 0) sb.Append(' ');
                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingSpace = true;
            }
        }
        return sb.Length == 0 ? "rule:" + ruleId : "ob:" + sb;
    }

    /// <summary>
    /// Same shape as <see cref="FromReport"/> but consults an
    /// <see cref="IClauseRewriter"/> per Fail verdict. When the rewriter
    /// returns non-null, the annotation is upgraded to
    /// <see cref="AnnotationKind.Replace"/> (tracked-change rewrite)
    /// with the LLM's text in <see cref="Annotation.Replacement"/>; the
    /// comment body still carries the rule guidance so reviewers see
    /// both the rationale and the proposed wording in Word's pane.
    ///
    /// <paramref name="clauseTextResolver"/> turns a verdict's
    /// <see cref="SourceSpan"/> into the original clause text (so the
    /// rewriter knows what to rewrite). Pass <c>null</c> to fall back
    /// to <see cref="Verdict.MatchedText"/>.
    ///
    /// Error verdicts and verdicts the rewriter declines for (returns
    /// null) keep the historical Comment-only behavior so the offline
    /// path stays byte-identical.
    /// </summary>
    public static async IAsyncEnumerable<Annotation> FromReportWithRewritesAsync(
        ComplianceReport report,
        IReadOnlyDictionary<string, Rule> rules,
        IClauseRewriter rewriter,
        Func<Verdict, string>? clauseTextResolver = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rewriter);
        foreach (var group in GroupFindings(report, rules))
        {
            var v = group.Representative;
            var rule = rules.GetValueOrDefault(v.RuleId);
            var comment = group.Comment;

            if (v.Outcome != VerdictOutcome.Fail)
            {
                yield return comment;
                continue;
            }

            if (group.ClauseSharedWithOtherGroups)
            {
                yield return WithReason(comment, "rewrite-withheld:shared-clause-span");
                continue;
            }

            var clauseText = clauseTextResolver?.Invoke(v)
                ?? (v.EvidenceQuotes.Count > 0 ? v.EvidenceQuotes[0] : string.Empty);

            // Don't rewrite content that lives outside any heading — that
            // section is the document title / pre-amble. Striking the title
            // is never a desirable redline even when a rule fails against it.
            // Comment still fires so the reviewer sees the finding.
            var rewriteSpan = v.ClauseSpan ?? v.SourceSpan;
            if (IsTitleOrPreambleSpan(rewriteSpan))
            {
                yield return WithReason(comment, "rewrite-withheld:title-or-preamble");
                continue;
            }

            string? rewrite;
            try
            {
                rewrite = await rewriter
                    .RewriteAsync(v, clauseText, rule, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Rewriter failures degrade to a plain comment — never
                // block the markup pipeline because the LLM tripped.
                rewrite = null;
            }

            if (string.IsNullOrWhiteSpace(rewrite)
                || string.Equals(rewrite.Trim(), NoRewriteMarker, StringComparison.OrdinalIgnoreCase))
            {
                yield return WithReason(comment, "rewrite-withheld:declined-or-unavailable");
                continue;
            }

            yield return BuildReplaceAnnotation(group, rewrite);
        }

        foreach (var gap in BuildScopedGapAnnotations(report, rules))
            yield return gap;
    }

    /// <summary>Sentinel the editor may return to explicitly decline a rewrite.</summary>
    public const string NoRewriteMarker = "NO_REWRITE";

    private static Annotation WithReason(Annotation a, string reason)
        => a.Reasons.Contains(reason, StringComparer.Ordinal)
            ? a
            : a with { Reasons = a.Reasons.Append(reason).ToList() };

    /// <summary>
    /// Returns a copy of <paramref name="comment"/> with an additional
    /// stable reason code. Lets callers (e.g. the CLI rewrite loop) record
    /// why a rewrite was withheld on the comment that remains.
    /// </summary>
    public static Annotation AddReason(Annotation comment, string reason)
        => WithReason(comment, reason);

    /// <summary>
    /// Builds the Replace annotation for a group; keeps rule refs, reasons
    /// and anchor metadata of the group comment.
    /// </summary>
    public static Annotation BuildReplaceAnnotation(FindingGroup group, string rewrite)
        => BuildReplaceAnnotation(group.Representative, group.Comment, rewrite);

    /// <summary>
    /// True when the verdict's span sits outside any heading — i.e. the
    /// document title or pre-amble before the first heading. The projector
    /// reports these spans with a <c>HeadingPath</c> of <c>null</c>, empty,
    /// or <c>"/"</c>. We refuse to strike-through the title under
    /// <c>--rewrite</c> even when a rule fails there; the comment still
    /// fires so reviewers see the finding.
    /// </summary>
    private static bool IsTitleOrPreambleSpan(SourceSpan? span)
    {
        if (span is null) return false;
        var hp = span.HeadingPath;
        // Only skip the literal document-root title span ("/").
        // Sections without a detected heading (empty path) but with
        // real clause text should still be rewritten.
        return hp == "/";
    }

    /// <summary>
    /// Returns true when the verdict's span should be skipped for rewriting
    /// (title/preamble area). Exposed for callers that inline the rewrite loop.
    /// </summary>
    public static bool IsSkippableSpan(Verdict v)
        => IsTitleOrPreambleSpan(v.ClauseSpan ?? v.SourceSpan);

    /// <summary>
    /// Builds a Comment annotation for a verdict. Exposed for callers that
    /// inline the rewrite loop outside <see cref="FromReportWithRewritesAsync"/>.
    /// </summary>
    public static Annotation BuildCommentForVerdict(Verdict v, Rule? rule)
        => BuildCommentAnnotation(v, rule);

    /// <summary>
    /// Builds a Replace (tracked-change) annotation from a verdict, its
    /// comment, and the rewritten clause text. Group metadata (rule ids,
    /// reasons, anchor kind) carried by <paramref name="comment"/> is kept.
    /// </summary>
    public static Annotation BuildReplaceAnnotation(Verdict v, Annotation comment, string rewrite)
        => comment with
        {
            Id = ContentHash.Compose("annot", v.Id, "replace").Value,
            Kind = AnnotationKind.Replace,
            Span = v.SourceSpan,
            Replacement = rewrite,
            ClauseSpan = v.ClauseSpan,
        };

    private static Annotation BuildGroupComment(IReadOnlyList<Verdict> members, IReadOnlyDictionary<string, Rule> rules)
    {
        var first = members[0];
        var rule = rules.GetValueOrDefault(first.RuleId);
        var single = BuildCommentAnnotation(first, rule);
        var reasons = new List<string>(single.Reasons);

        if (members.Count == 1)
        {
            return single with
            {
                RuleIds = new[] { first.RuleId },
                VerdictIds = new[] { first.Id },
            };
        }

        var ruleIds = members.Select(m => m.RuleId).Distinct(StringComparer.Ordinal).ToList();
        reasons.Add("grouped:same-obligation-same-location:rules=" + string.Join(",", ruleIds)
            + ";verdicts=" + members.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));

        foreach (var member in members)
        {
            var severityRule = rules.GetValueOrDefault(member.RuleId);
            var severityReason = severityRule is null ? null : RuleSeverityPolicy.ReviewReason(severityRule);
            if (severityReason is not null && !reasons.Contains(severityReason, StringComparer.Ordinal))
                reasons.Add(severityReason);
        }

        var pairs = members
            .Select(m => (Verdict: m, Rule: rules.GetValueOrDefault(m.RuleId)))
            .ToList();
        var text = CommentFormatting.BuildGroupedBody(pairs);
        var ids = members.Select(m => m.Id).ToList();

        return new Annotation(
            Id: ContentHash.Compose("annot", "group", string.Join("|", ids), "comment").Value,
            Kind: AnnotationKind.Comment,
            Span: first.SourceSpan,
            Author: single.Author,
            Text: text,
            Replacement: null)
        {
            RuleIds = ruleIds,
            VerdictIds = ids,
            Reasons = reasons,
            AnchorKind = single.AnchorKind,
            ClauseSpan = null,
        };
    }

    private static Annotation BuildCommentAnnotation(Verdict v, Rule? rule)
    {
        string author;
        string text;
        if (rule is null)
        {
            author = Author;
            text = $"{CommentFormatting.ErrorBanner}\n\nRule {v.RuleId} reported {v.Outcome}.";
            if (v.ErrorMessage is { Length: > 0 })
                text += $"\n\nDetail: {v.ErrorMessage}";
        }
        else
        {
            author = CommentFormatting.BuildAuthor(rule);
            text = CommentFormatting.BuildBody(rule, v);
        }

        var reasons = new List<string>();
        var anchorKind = AnnotationAnchorKind.Evidence;
        if (v.Outcome == VerdictOutcome.Fail && v.EvidenceQuotes.Count == 0)
        {
            // No quote backs this span, so it must not be read as the
            // contradictory text itself — it only scopes the section.
            anchorKind = AnnotationAnchorKind.ScopedSection;
            reasons.Add("anchor:scoped-section:no-evidence-quote");
        }
        if (v.Outcome != VerdictOutcome.Error
            && rule is not null
            && RuleSeverityPolicy.ReviewReason(rule) is { } severityReason)
            reasons.Add(severityReason);

        return new Annotation(
            Id: ContentHash.Compose("annot", v.Id, "comment").Value,
            Kind: AnnotationKind.Comment,
            Span: v.SourceSpan,
            Author: author,
            Text: text,
            Replacement: null)
        {
            RuleIds = new[] { v.RuleId },
            VerdictIds = new[] { v.Id },
            Reasons = reasons,
            AnchorKind = anchorKind,
        };
    }

    /// <summary>
    /// Opt-in mapping: every Pass verdict becomes a positive-confirmation
    /// Comment anchored to the matched section. Used by markup mode's
    /// <c>--annotate-pass</c> flag to produce full coverage proof — i.e.
    /// the reviewed document shows not only what failed, but what was
    /// checked and passed.
    ///
    /// Off by default. The volume can be high (one comment per matched
    /// section per applicable rule), so reviewers must opt in.
    ///
    /// Annotation Ids derive from <c>(verdict.Id, "pass")</c> so they are
    /// distinct from the Fail/Error ids in <see cref="FromReport"/> and
    /// the run is idempotent across two invocations of the same inputs.
    /// </summary>
    public static IEnumerable<Annotation> BuildPassAnnotations(ComplianceReport report, IReadOnlyDictionary<string, Rule> rules)
    {
        foreach (var v in report.Verdicts)
        {
            if (v.Outcome is not VerdictOutcome.Pass) continue;
            var rule = rules.GetValueOrDefault(v.RuleId);
            var statement = rule?.NaturalLanguage ?? v.RuleId;
            var author = rule is null ? Author : CommentFormatting.BuildAuthor(rule);
            var text = CommentFormatting.BuildPassBody(rule, v, statement);

            yield return new Annotation(
                Id: ContentHash.Compose("annot", v.Id, "pass").Value,
                Kind: AnnotationKind.Comment,
                Span: v.SourceSpan,
                Author: author,
                Text: text,
                Replacement: null);
        }
    }

    /// <summary>
    /// Build a single summary Annotation that lists every Gap verdict that
    /// has no safe reviewed-document span (see <see cref="IsScopedGap"/>) —
    /// mandatory rules the reviewed document is silent on. Gaps with a
    /// resolvable section span get their own anchored comment from
    /// <see cref="BuildScopedGapAnnotations"/> and are NOT repeated here.
    /// Entries are distinct by rule id. Anchored to the top of the document
    /// (charStart=0). Returns <c>null</c> when nothing remains so callers
    /// can skip emission entirely.
    /// </summary>
    public static Annotation? BuildGapsSummary(ComplianceReport report, IReadOnlyDictionary<string, Rule> rules)
    {
        var all = report.Verdicts
            .Where(v => v.Outcome == VerdictOutcome.Gap && !IsScopedGap(v, report.DocumentId.Value))
            .OrderBy(v => v.RuleId, StringComparer.Ordinal)
            .ThenBy(v => v.Id, StringComparer.Ordinal)
            .ToList();
        var gaps = all.GroupBy(v => v.RuleId, StringComparer.Ordinal).Select(g => g.First()).ToList();
        if (gaps.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.Append("\U0001F4CB LAMBDA-RAG GAP ANALYSIS — ")
          .Append(gaps.Count)
          .AppendLine(" mandatory topic(s) not addressed:");
        foreach (var v in gaps)
        {
            var rule = rules.GetValueOrDefault(v.RuleId);
            var nl = rule?.NaturalLanguage ?? v.RuleId;
            var label = rule is null ? CommentFormatting.GenericLabel
                                     : CommentFormatting.ResolveCategoryLabel(rule);
            sb.Append("\n• [").Append(label).Append(" / ")
              .Append(v.RuleId).Append("] ").Append(nl);
        }
        var anchorSpan = new SourceSpan(report.DocumentId.Value, 0, 0, null, null);
        return new Annotation(
            Id: ContentHash.Compose("annot", "gaps-summary", report.RuleSetFingerprint.Value).Value,
            Kind: AnnotationKind.Comment,
            Span: anchorSpan,
            Author: Author,
            Text: sb.ToString(),
            Replacement: null)
        {
            RuleIds = gaps.Select(g => g.RuleId).Distinct(StringComparer.Ordinal).ToList(),
            VerdictIds = all.Select(g => g.Id).ToList(),
            Reasons = new[] { "unanchored:document-level-gap" },
            AnchorKind = AnnotationAnchorKind.DocumentLevelGap,
        };
    }
}
