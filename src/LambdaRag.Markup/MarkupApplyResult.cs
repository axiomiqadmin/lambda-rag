namespace LambdaRag.Markup;

/// <summary>What happened to one annotation during <see cref="OpenXmlMarkupService.Apply"/>.</summary>
public enum MarkupDisposition
{
    /// <summary>Applied exactly as requested.</summary>
    Applied,
    /// <summary>
    /// A tracked change (w:ins / w:del) was requested but withheld; the
    /// reviewer comment was still emitted. See <see cref="MarkupAnnotationResult.Reason"/>.
    /// </summary>
    CommentOnly,
    /// <summary>Nothing was emitted (e.g. the span matched no paragraph).</summary>
    Skipped,
    /// <summary>An unexpected exception occurred while applying the annotation.</summary>
    Failed,
}

/// <summary>
/// Stable reason codes used in <see cref="MarkupAnnotationResult.Reason"/>.
/// Codes are strings (not an enum) so they can be written verbatim to
/// reports; every code is listed here.
/// </summary>
public static class MarkupReasons
{
    public const string CommentTargetNotFound = "skipped:comment-target-not-found";
    public const string ApplicationError = "failed:application-error";

    public const string ReplacementMissing = "tracked-change-withheld:replacement-missing";
    public const string ReplacementDeclined = "tracked-change-withheld:replacement-declined";
    public const string ReplacementInvalidFormat = "tracked-change-withheld:replacement-invalid-format";
    public const string ReplacementIdentical = "tracked-change-withheld:replacement-identical-to-original";
    public const string ReplacementOverBroad = "tracked-change-withheld:replacement-over-broad";
    public const string TargetMissing = "tracked-change-withheld:target-missing";
    public const string TargetEmpty = "tracked-change-withheld:target-empty";
    public const string TargetOverBroad = "tracked-change-withheld:target-over-broad";
    public const string TargetAmbiguous = "tracked-change-withheld:target-ambiguous";
    public const string StructurallyUnsafe = "tracked-change-withheld:structurally-unsafe";
}

/// <summary>Per-annotation outcome with a stable audit reason.</summary>
public sealed record MarkupAnnotationResult(
    string AnnotationId,
    AnnotationKind RequestedKind,
    AnnotationKind AppliedKind,
    MarkupDisposition Disposition,
    string? Reason,
    string? Detail,
    IReadOnlyList<string> RuleIds,
    IReadOnlyList<string> AnnotationReasons)
{
    /// <summary>
    /// For tracked changes (<see cref="AnnotationKind.Replace"/> /
    /// <see cref="AnnotationKind.Delete"/> / <see cref="AnnotationKind.Insert"/>) that were applied:
    /// the original text the w:del targets (null otherwise).
    /// </summary>
    public string? TargetText { get; init; }
}

/// <summary>
/// Observable result of applying annotations to a DOCX. Callers must not
/// report a clean run when <see cref="Skips"/> or <see cref="Failures"/>
/// are non-empty.
/// </summary>
public sealed class MarkupApplyResult
{
    public MarkupApplyResult(IReadOnlyList<MarkupAnnotationResult> results)
    {
        Results = results;
    }

    public IReadOnlyList<MarkupAnnotationResult> Results { get; }

    public int AppliedCount => Results.Count(r => r.Disposition == MarkupDisposition.Applied);

    public int TrackedChangesApplied => Results.Count(r =>
        r.Disposition == MarkupDisposition.Applied && r.AppliedKind != AnnotationKind.Comment);

    /// <summary>Tracked changes that were withheld; the comment was still written.</summary>
    public IReadOnlyList<MarkupAnnotationResult> CommentOnly =>
        Results.Where(r => r.Disposition == MarkupDisposition.CommentOnly).ToList();

    /// <summary>Annotations for which nothing was emitted.</summary>
    public IReadOnlyList<MarkupAnnotationResult> Skipped =>
        Results.Where(r => r.Disposition == MarkupDisposition.Skipped).ToList();

    /// <summary>Annotations that threw while being applied.</summary>
    public IReadOnlyList<MarkupAnnotationResult> Failures =>
        Results.Where(r => r.Disposition == MarkupDisposition.Failed).ToList();

    /// <summary>Everything that was not applied as requested (CommentOnly + Skipped + Failed).</summary>
    public IReadOnlyList<MarkupAnnotationResult> Skips =>
        Results.Where(r => r.Disposition != MarkupDisposition.Applied).ToList();

    /// <summary>True only when every annotation was applied as requested.</summary>
    public bool IsClean => Results.All(r => r.Disposition == MarkupDisposition.Applied);

    /// <summary>True when at least one annotation failed with an exception.</summary>
    public bool HasFailures => Results.Any(r => r.Disposition == MarkupDisposition.Failed);
}
