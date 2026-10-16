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
    private const int DefaultRequestsPerMinute = 18;

    public static IFactExtractor? TryCreate(
        IConfiguration? configuration,
        ILoggerFactory? loggerFactory = null,
        string? cacheDirOverride = null,
        bool refresh = false)
    {
        var chatClient = OpenRouterChatClientFactory.TryCreate(configuration);
        if (chatClient is null)
            return null;

        var requestsPerMinute = configuration?["OpenRouter:FactRequestsPerMinute"]
            ?? Environment.GetEnvironmentVariable("OPENROUTER_FACT_REQUESTS_PER_MINUTE");
        var requestLimit = DefaultRequestsPerMinute;
        if (requestsPerMinute is not null
            && (!int.TryParse(requestsPerMinute, out requestLimit) || requestLimit < 1))
        {
            throw new InvalidOperationException(
                "OpenRouter:FactRequestsPerMinute must be a positive integer.");
        }

        var logger = loggerFactory?.CreateLogger<FoundrySectionFactExtractor>();
        return new FoundrySectionFactExtractor(
            chatClient,
            modelId: OpenRouterChatClientFactory.ResolveModel(configuration),
            log: logger,
            cacheDirOverride: cacheDirOverride,
            refresh: refresh,
            requestsPerMinute: requestLimit);
    }

}
