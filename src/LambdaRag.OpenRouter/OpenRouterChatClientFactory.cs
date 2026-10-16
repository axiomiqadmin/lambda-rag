using System.ClientModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using OpenAI.Chat;

namespace LambdaRag.OpenRouter;

public static class OpenRouterChatClientFactory
{
    public const string DefaultModel = "openai/gpt-5.6-luna";
    public const string DefaultEndpoint = "https://openrouter.ai/api/v1";

    public static IChatClient? TryCreate(IConfiguration? configuration = null)
    {
        var apiKey = ResolveApiKey(configuration);
        if (string.IsNullOrWhiteSpace(apiKey))
            return null;

        var model = ResolveModel(configuration);
        return Create(apiKey, model, ResolveEndpoint(configuration));
    }

    public static string ResolveModel(IConfiguration? configuration = null) =>
        Resolve(configuration, "OpenRouter:Model", "OPENROUTER_MODEL") ?? DefaultModel;

    public static string? ResolveApiKey(IConfiguration? configuration = null) =>
        Resolve(configuration, "OpenRouter:ApiKey", "OPENROUTER_API_KEY");

    public static Uri ResolveEndpoint(IConfiguration? configuration = null) =>
        new(Resolve(configuration, "OpenRouter:Endpoint", "OPENROUTER_BASE_URL") ?? DefaultEndpoint,
            UriKind.Absolute);

    public static IChatClient Create(
        string apiKey,
        string model = DefaultModel,
        Uri? endpoint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var options = new OpenAIClientOptions
        {
            Endpoint = endpoint ?? new Uri(DefaultEndpoint),
            Transport = OpenRouterRequestTransport.Create(),
        };
        return new ChatClient(model, new ApiKeyCredential(apiKey), options).AsIChatClient();
    }

    private static string? Resolve(IConfiguration? configuration, string key, string environmentVariable)
    {
        var configuredValue = configuration?[key];
        if (!string.IsNullOrWhiteSpace(configuredValue))
            return configuredValue;

        var environmentValue = Environment.GetEnvironmentVariable(environmentVariable);
        return string.IsNullOrWhiteSpace(environmentValue) ? null : environmentValue;
    }
}