using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Embeddings;

namespace LambdaRag.OpenRouter;

public static class OpenRouterEmbeddingClientFactory
{
    public const string DefaultModel = "openai/text-embedding-3-large";

    public static IEmbeddingGenerator<string, Embedding<float>>? TryCreate(
        IConfiguration? configuration = null)
    {
        var apiKey = OpenRouterChatClientFactory.ResolveApiKey(configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        return Create(apiKey, ResolveModel(configuration), OpenRouterChatClientFactory.ResolveEndpoint(configuration));
    }

    public static IEmbeddingGenerator<string, Embedding<float>> Create(
        string apiKey,
        string model = DefaultModel,
        Uri? endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var options = new OpenAIClientOptions
        {
            Endpoint = endpoint ?? new Uri(OpenRouterChatClientFactory.DefaultEndpoint),
            Transport = OpenRouterRequestTransport.Create(),
        };
        return new EmbeddingClient(model, new ApiKeyCredential(apiKey), options)
            .AsIEmbeddingGenerator();
    }

    public static string ResolveModel(IConfiguration? configuration = null)
    {
        var configured = configuration?["OpenRouter:EmbeddingModel"];
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var environment = Environment.GetEnvironmentVariable("OPENROUTER_EMBEDDING_MODEL");
        return string.IsNullOrWhiteSpace(environment) ? DefaultModel : environment;
    }
}