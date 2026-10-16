using LambdaRag.Markup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LambdaRag.Authoring;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the configured rule authoring agent + an
    /// <see cref="IRuleEmbedder"/>. When <paramref name="configuration"/>
    /// supplies <c>OPENROUTER_API_KEY</c>, a real OpenRouter embedder is
    /// wired up; otherwise the deterministic hash embedder is
    /// used so unit tests + offline replays still work without any cloud
    /// credentials.
    ///
    /// The <see cref="IRuleAuthoringAgent"/> follows the same pattern:
    /// <see cref="FoundryRuleAuthoringAgentFactory.TryCreate"/> is
    /// consulted first; if <c>OPENROUTER_API_KEY</c> is configured, the
    /// LLM-backed <see cref="FoundryRuleAuthoringAgent"/>
    /// wins. Otherwise the deterministic <see cref="DeterministicMockAuthoringAgent"/>
    /// is used so offline / unit-test paths keep working unchanged.
    /// </summary>
    public static IServiceCollection AddLambdaRagAuthoring(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        services.AddSingleton<IRuleEmbedder>(_ =>
            (IRuleEmbedder?)Embeddings.OpenRouterEmbedderFactory.TryCreate(configuration)
                ?? new DeterministicHashEmbedder());
#pragma warning disable OPENAI001
        services.AddSingleton<IRuleAuthoringAgent>(sp =>
            FoundryRuleAuthoringAgentFactory.TryCreate(
                configuration,
                sp.GetRequiredService<IRuleEmbedder>(),
                sp.GetService<ILoggerFactory>())
            ?? new DeterministicMockAuthoringAgent(sp.GetRequiredService<IRuleEmbedder>()));
#pragma warning restore OPENAI001

        // Bind IClauseRewriter: real OpenRouter-backed ComplianceEditor when
        // OpenRouter credentials are configured, Noop otherwise. We do
        // NOT fall back to DeterministicMockClauseRewriter here — that
        // mock returns the rule's remediation text verbatim as the new
        // clause body, which would inject rule guidance into the
        // document. Noop returns null so the markup pipeline keeps the
        // historical Comment-only behavior when no real LLM editor is
        // configured (see issue #90).
#pragma warning disable OPENAI001
        services.AddSingleton<IClauseRewriter>(_ =>
            Editing.ComplianceEditorFactory.TryCreate(configuration)
                ?? (IClauseRewriter)NoopClauseRewriter.Instance);
#pragma warning restore OPENAI001
        return services;
    }
}
