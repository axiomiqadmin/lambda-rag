using LambdaRag.Core.Facts;
using LambdaRag.OpenRouter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LambdaRag.Authoring;

/// <summary>
/// Pillar 12 (#153) — builds a <see cref="FoundrySectionFactExtractor"/>
/// from <see cref="IConfiguration"/>. The legacy class name is retained;
/// chat completions are sent through OpenRouter.
///
/// Returns <c>null</c> when <c>OPENROUTER_API_KEY</c> is unavailable.
/// </summary>
public static class FoundrySectionFactExtractorFactory
{
    public static IFactExtractor? TryCreate(
        IConfiguration? configuration,
        ILoggerFactory? loggerFactory = null,
        string? cacheDirOverride = null,
        bool refresh = false)
    {
        var chatClient = OpenRouterChatClientFactory.TryCreate(configuration);
        if (chatClient is null)
            return null;

        var logger = loggerFactory?.CreateLogger<FoundrySectionFactExtractor>();
        return new FoundrySectionFactExtractor(
            chatClient,
            modelId: OpenRouterChatClientFactory.ResolveModel(configuration),
            log: logger,
            cacheDirOverride: cacheDirOverride,
            refresh: refresh);
    }

}
