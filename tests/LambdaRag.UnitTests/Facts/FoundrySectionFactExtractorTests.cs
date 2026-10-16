using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using FluentAssertions;
using LambdaRag.Authoring;
using LambdaRag.Core.Domain;
using LambdaRag.Core.Facts;
using LambdaRag.Core.Hashing;
using Microsoft.Extensions.AI;
using Xunit;

namespace LambdaRag.UnitTests.Facts;

public sealed class FoundrySectionFactExtractorTests
{
    [Fact]
    public async Task ExtractAsync_retries_sidecar_sections_that_failed_previously()
    {
        var cacheDir = Path.Combine(Path.GetTempPath(), "lambda-rag-facts-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDir);
        try
        {
            var document = new ProjectedDocument(
                ContentHash.OfString("document"),
                "test-projector",
                "1.0",
                new JsonObject
                {
                    ["sections"] = new JsonArray(new JsonObject
                    {
                        ["id"] = "s1",
                        ["text"] = "This policy requires encryption of stored customer records at rest.",
                    }),
                },
                new Dictionary<string, SourceSpan>());
            var schema = new FactSchema("test-schema", "1", new[]
            {
                new FactConcept("encryption_declared", FactType.Boolean, "Encryption is declared."),
            });

            var failedExtractor = new FoundrySectionFactExtractor(
                new FakeChatClient(() => throw new FakeThrottleException()),
                "test-model",
                maxRetries: 0,
                cacheDirOverride: cacheDir);
            var failedSidecar = await failedExtractor.ExtractAsync(document, schema);
            failedSidecar.Sections["s1"].Should().ContainKey("_extraction_failed");

            var retryClient = new FakeChatClient(() => """
                {"encryption_declared":true,"supporting_quote":"requires encryption of stored customer records at rest"}
                """);
            var retryExtractor = new FoundrySectionFactExtractor(
                retryClient,
                "test-model",
                cacheDirOverride: cacheDir);
            var retriedSidecar = await retryExtractor.ExtractAsync(document, schema);

            retryClient.Calls.Should().Be(1);
            retriedSidecar.Sections["s1"]["encryption_declared"].Should().Be(true);
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    private sealed class FakeChatClient(Func<string> response) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response())));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => EmptyStream();

        private static async IAsyncEnumerable<ChatResponseUpdate> EmptyStream(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class FakeThrottleException() : Exception("429 Too Many Requests")
    {
        public int Status => 429;
    }
}