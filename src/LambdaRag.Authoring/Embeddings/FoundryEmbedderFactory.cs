using LambdaRag.OpenRouter;
using Microsoft.Extensions.Configuration;

namespace LambdaRag.Authoring.Embeddings;

/// <summary>
/// Builds an <see cref="OpenRouterEmbeddingProvider"/> from
/// <see cref="IConfiguration"/> with environment-variable fallback. Returns
/// <c>null</c> when the required values are missing — the caller can then
/// fall back to <see cref="DeterministicHashEmbedder"/> for offline /
/// unit-test runs.
///
/// Configuration keys:
///   • <c>OpenRouter:EmbeddingModel</c>      — defaults to "openai/text-embedding-3-large"
///   • <c>LambdaRag:Embedding:Dimensions</c> — defaults per model (3072 for 3-large)
///   • <c>LambdaRag:EmbeddingCache</c>       — directory for the file-backed cache; defaults to <c>out/embedding-cache</c>
///
/// The API key and base endpoint are shared with OpenRouter chat completions.
/// Model and dimension overrides are available through environment variables.
///
/// All embeddings flow through a <see cref="FileBackedEmbeddingCache"/> so
/// repeat runs (and CI replays) are 100% offline once the cache is warm.
/// </summary>
public static class OpenRouterEmbedderFactory
{
    public const string ModelVar = "OPENROUTER_EMBEDDING_MODEL";
    public const string DimensionsVar = "LAMBDA_RAG_EMBEDDING_DIMENSIONS";
    public const string CacheDirVar = "LAMBDA_RAG_EMBEDDING_CACHE";

    public const string ModelKey = "OpenRouter:EmbeddingModel";
    public const string DimensionsKey = "LambdaRag:Embedding:Dimensions";
    public const string CacheDirKey = "LambdaRag:EmbeddingCache";

    /// <summary>
    /// Reads OpenRouter settings from <paramref name="configuration"/> and
    /// environment variables. Returns <c>null</c> when the shared
    /// <c>OPENROUTER_API_KEY</c> is unavailable.
    /// </summary>
    public static OpenRouterEmbeddingProvider? TryCreate(IConfiguration? configuration)
    {
        var generator = OpenRouterEmbeddingClientFactory.TryCreate(configuration);
        if (generator is null)
            return null;

        var modelId = OpenRouterEmbeddingClientFactory.ResolveModel(configuration);
        var dimensions = ResolveDimensions(configuration, modelId);

        var cacheDir = Resolve(configuration, CacheDirKey, CacheDirVar)
            ?? Path.Combine("out", "embedding-cache");
        var cache = new FileBackedEmbeddingCache(cacheDir, modelId, dimensions);

        return new OpenRouterEmbeddingProvider(generator, modelId, dimensions, cache);
    }

    /// <summary>
    /// Backwards-compatible wrapper that reads only from environment variables.
    /// New callers should prefer <see cref="TryCreate(IConfiguration?)"/>.
    /// </summary>
    public static OpenRouterEmbeddingProvider? TryCreateFromEnvironment()
        => TryCreate(configuration: null);

    private static string? Resolve(IConfiguration? configuration, string key, string envVar)
    {
        var fromConfig = configuration?[key];
        if (!string.IsNullOrWhiteSpace(fromConfig))
            return fromConfig;
        var fromEnv = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    private static int ResolveDimensions(IConfiguration? configuration, string modelId)
    {
        var explicitDims = Resolve(configuration, DimensionsKey, DimensionsVar);
        if (!string.IsNullOrWhiteSpace(explicitDims) && int.TryParse(explicitDims, out var d) && d > 0)
            return d;
        return modelId switch
        {
            "openai/text-embedding-3-large" or "text-embedding-3-large" => 3072,
            "openai/text-embedding-3-small" or "text-embedding-3-small" => 1536,
            "openai/text-embedding-ada-002" or "text-embedding-ada-002" => 1536,
            _ => 3072,
        };
    }
}
