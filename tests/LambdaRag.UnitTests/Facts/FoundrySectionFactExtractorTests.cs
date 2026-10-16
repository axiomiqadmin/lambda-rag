using System.Runtime.CompilerServices;
using System.Reflection;
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
    public void RetryDelay_parses_openrouter_unix_reset_header()
    {
        var parse = typeof(FoundrySectionFactExtractor).GetMethod(
            "TryParseRetryAfter", BindingFlags.NonPublic | BindingFlags.Static);
        var arguments = new object?[]
        {
            "X-RateLimit-Reset",
            DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeSeconds()
                .ToString(System.Globalization.CultureInfo.InvariantCulture),
            TimeSpan.Zero,
        };

        parse.Should().NotBeNull();
        parse!.Invoke(null, arguments).Should().Be(true);
        var delay = (TimeSpan)arguments[2]!;
        delay.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(110));
        delay.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task ExtractAsync_stops_after_rate_limit_and_resumes_uncached_sections()
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
                    ["sections"] = new JsonArray(
                        new JsonObject
                        {
                            ["id"] = "s1",
                            ["text"] = "This policy requires encryption of stored customer records at rest.",
                        },
                        new JsonObject
                        {
                            ["id"] = "s2",
                            ["text"] = "Administrative access requires multifactor authentication.",
                        },
                        new JsonObject
                        {
                            ["id"] = "s3",
                            ["text"] = "Systems encrypt data in transit using TLS.",
                        }),
                },
                new Dictionary<string, SourceSpan>());
            var schema = new FactSchema("test-schema", "1", new[]
            {
                new FactConcept("encryption_declared", FactType.Boolean, "Encryption is declared."),
            });

            var failedExtractor = new FoundrySectionFactExtractor(
                new FakeChatClient(new Queue<Func<string>>(new Func<string>[]
                {
                    () => """
                        {"encryption_declared":true,"supporting_quote":"requires encryption of stored customer records at rest"}
                        """,
                    () => throw new FakeThrottleException(),
                })),
                "test-model",
                maxRetries: 0,
                cacheDirOverride: cacheDir,
                requestsPerMinute: int.MaxValue);
            var failedSidecar = await failedExtractor.ExtractAsync(document, schema);
            failedSidecar.Sections.Keys.Should().BeEquivalentTo("s1", "s2");
            failedSidecar.Sections["s2"].Should().ContainKey("_extraction_failed");

            var retryClient = new FakeChatClient(new Queue<Func<string>>(new Func<string>[]
            {
                () => """
                    {"encryption_declared":true,"supporting_quote":"requires multifactor authentication"}
                    """,
                () => """
                    {"encryption_declared":true,"supporting_quote":"encrypt data in transit using TLS"}
                    """,
            }));
            var retryExtractor = new FoundrySectionFactExtractor(
                retryClient,
                "test-model",
                cacheDirOverride: cacheDir,
                requestsPerMinute: int.MaxValue);
            var retriedSidecar = await retryExtractor.ExtractAsync(document, schema);

            retryClient.Calls.Should().Be(2);
            retriedSidecar.Sections.Keys.Should().BeEquivalentTo("s1", "s2", "s3");
            retriedSidecar.Sections["s1"].Should().NotContainKey("_extraction_failed");
            retriedSidecar.Sections["s2"]["encryption_declared"].Should().Be(true);
            retriedSidecar.Sections["s3"]["encryption_declared"].Should().Be(true);
        }
        finally
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    private sealed class FakeChatClient(Queue<Func<string>> responses) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responses.Dequeue()())));
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