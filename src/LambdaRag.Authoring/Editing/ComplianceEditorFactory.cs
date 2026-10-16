using LambdaRag.Markup;
using LambdaRag.OpenRouter;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

namespace LambdaRag.Authoring.Editing;

/// <summary>
/// Reads <see cref="ComplianceEditorOptions"/> from <see cref="IConfiguration"/>
/// (with environment-variable fallback) and builds a real
/// <see cref="ComplianceEditor"/> backed by an <see cref="AIAgent"/>
/// constructed over the shared OpenRouter chat client.
///
/// Returns <c>null</c> when no OpenRouter API key is configured
/// — callers then fall back to <see cref="NoopClauseRewriter"/> so the
/// markup pipeline emits Comment annotations only.
/// </summary>
public static class ComplianceEditorFactory
{
    public const string EditCacheDirKey = "LambdaRag:Foundry:Edit:CacheDir";

    public const string EditCacheDirVar = "LAMBDA_RAG_REWRITE_CACHE";

    public static IClauseRewriter? TryCreate(IConfiguration? configuration)
    {
        var chatClient = OpenRouterChatClientFactory.TryCreate(configuration);
        if (chatClient is null)
            return null;

        AIAgent agent = chatClient.AsAIAgent(
            instructions: ComplianceEditor.SystemPrompt,
            name: ComplianceEditor.AgentName);

        var cacheDir = configuration?[EditCacheDirKey];
        if (string.IsNullOrWhiteSpace(cacheDir))
            cacheDir = Environment.GetEnvironmentVariable(EditCacheDirVar);
        if (string.IsNullOrWhiteSpace(cacheDir))
            cacheDir = Path.Combine("out", "rewrite-cache");

        var options = new ComplianceEditorOptions
        {
            Model = OpenRouterChatClientFactory.ResolveModel(configuration),
            CacheDir = cacheDir,
        };
        return new ComplianceEditor(agent, options);
    }
}
