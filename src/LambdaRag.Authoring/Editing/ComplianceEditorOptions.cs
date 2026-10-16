namespace LambdaRag.Authoring.Editing;

/// <summary>
/// Strongly-typed options for <see cref="ComplianceEditor"/>.
/// </summary>
public sealed class ComplianceEditorOptions
{
    public required string Model { get; init; }
    public string CacheDir { get; init; } = Path.Combine("out", "rewrite-cache");
    public int MaxRewriteLength { get; init; } = 1200;
}
