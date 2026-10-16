using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LambdaRag.OpenRouter;
using Microsoft.Extensions.AI;
using Xunit;

namespace LambdaRag.UnitTests.OpenRouter;

public sealed class OpenRouterChatClientFactoryTests
{
    [Fact]
    public async Task SendsConfiguredModelReasoningAndJsonFormat()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = CaptureRequestAsync(listener);
        var client = OpenRouterChatClientFactory.Create(
            "test-key",
            endpoint: new Uri($"http://127.0.0.1:{port}/api/v1"));

        var response = await client.GetResponseAsync(
            new[] { new ChatMessage(ChatRole.User, "test prompt") },
            new ChatOptions { ResponseFormat = ChatResponseFormat.Json });
        var captured = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("POST /api/v1/chat/completions ", captured.RequestLine);
        Assert.Contains("Authorization: Bearer test-key", captured.Headers, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("openai/gpt-5.6-luna", body.RootElement.GetProperty("model").GetString());
        Assert.True(body.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.Equal("json_object", body.RootElement.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("test prompt", response.Text);
    }

    [Fact]
    public async Task SendsEmbeddingRequestWithFloatEncoding()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        const string embeddingResponse = "{\"object\":\"list\",\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.25,0.75]}],\"model\":\"openai/text-embedding-3-large\",\"usage\":{\"prompt_tokens\":2,\"total_tokens\":2}}";
        var serverTask = CaptureRequestAsync(listener, embeddingResponse);
        var generator = OpenRouterEmbeddingClientFactory.Create(
            "test-key",
            endpoint: new Uri($"http://127.0.0.1:{port}/api/v1"));

        var embeddings = await generator.GenerateAsync(new[] { "embedding input" });
        var captured = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("POST /api/v1/embeddings ", captured.RequestLine);
        Assert.Contains("Authorization: Bearer test-key", captured.Headers, StringComparison.OrdinalIgnoreCase);
        using var body = JsonDocument.Parse(captured.Body);
        Assert.Equal("openai/text-embedding-3-large", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("float", body.RootElement.GetProperty("encoding_format").GetString());
        var input = body.RootElement.GetProperty("input");
        Assert.Equal("embedding input", input.ValueKind == JsonValueKind.Array
            ? input[0].GetString()
            : input.GetString());
        Assert.Equal(new[] { 0.25f, 0.75f }, embeddings[0].Vector.ToArray());
    }

    private static async Task<CapturedRequest> CaptureRequestAsync(
        TcpListener listener,
        string? responseBody = null)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var requestLine = await reader.ReadLineAsync() ?? string.Empty;
        var headers = new StringBuilder();
        var contentLength = 0;
        string? line;
        while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        {
            headers.AppendLine(line);
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line.AsSpan("Content-Length:".Length).Trim());
        }

        var bodyChars = new char[contentLength];
        await reader.ReadBlockAsync(bodyChars, 0, bodyChars.Length);
        var body = new string(bodyChars);
        responseBody ??= "{\"id\":\"test\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"openai/gpt-5.6-luna\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"test prompt\"},\"finish_reason\":\"stop\"}]}";
        var responseBytes = Encoding.UTF8.GetBytes(responseBody);
        var responseHeaders = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {responseBytes.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(responseHeaders);
        await stream.WriteAsync(responseBytes);
        return new CapturedRequest(requestLine, headers.ToString(), body);
    }

    private sealed record CapturedRequest(string RequestLine, string Headers, string Body);
}