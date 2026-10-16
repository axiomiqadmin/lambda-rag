using LambdaRag.Authoring.Editing;
using LambdaRag.Authoring.Embeddings;
using LambdaRag.OpenRouter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LambdaRag.Authoring;

/// <summary>
/// Builds a <see cref="FoundryRuleAuthoringAgent"/> from
/// <see cref="IConfiguration"/>. The legacy class name is retained for API
/// compatibility; chat completions are sent through OpenRouter.
///
/// Returns <c>null</c> when <c>OPENROUTER_API_KEY</c> is unavailable — the
/// caller then falls back to <see cref="DeterministicMockAuthoringAgent"/>.
/// </summary>
public static class FoundryRuleAuthoringAgentFactory
{
    public static IRuleAuthoringAgent? TryCreate(
        IConfiguration? configuration,
        IRuleEmbedder embedder,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(embedder);

        var chatClient = OpenRouterChatClientFactory.TryCreate(configuration);
        if (chatClient is null)
            return null;

        var logger = loggerFactory?.CreateLogger<FoundryRuleAuthoringAgent>();
        return new FoundryRuleAuthoringAgent(chatClient, embedder, logger);
    }
}
