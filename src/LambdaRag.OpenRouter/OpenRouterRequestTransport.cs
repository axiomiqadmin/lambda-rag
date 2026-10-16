using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json.Nodes;

namespace LambdaRag.OpenRouter;

internal static class OpenRouterRequestTransport
{
    public static HttpClientPipelineTransport Create() =>
        new(new HttpClient(new OpenRouterRequestHandler()));

    private sealed class OpenRouterRequestHandler : DelegatingHandler
    {
        public OpenRouterRequestHandler() : base(new HttpClientHandler())
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath;
            if (request.Content is { } content && path is not null)
            {
                var isChat = path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);
                var isEmbedding = path.EndsWith("/embeddings", StringComparison.OrdinalIgnoreCase);
                if (isChat || isEmbedding)
                {
                    var payload = await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    var json = JsonNode.Parse(payload) as JsonObject
                        ?? throw new InvalidOperationException("OpenRouter request body must be a JSON object.");

                    if (isChat)
                    {
                        var reasoning = json["reasoning"] as JsonObject ?? new JsonObject();
                        reasoning["enabled"] = true;
                        json["reasoning"] = reasoning;
                    }
                    if (isEmbedding)
                        json["encoding_format"] = "float";

                    var replacement = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json");
                    foreach (var header in content.Headers)
                    {
                        if (header.Key is not ("Content-Length" or "Content-Type" or "Transfer-Encoding"))
                            replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                    request.Content = replacement;
                    content.Dispose();
                }
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }
}