using System.Net;
using System.Text.Json;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Voice.Local.Llama;

namespace Jarvis.Infrastructure.Tests.Voice;

public sealed class LlamaCppLocalLanguageModelTests
{
    [Fact]
    public async Task StreamsOnlyVisibleContentUsesNoThinkAndReusesClient()
    {
        const string eventStream = """
            data: {"choices":[{"delta":{"reasoning_content":"private reasoning","content":null}}]}

            data: {"choices":[{"delta":{"content":"Visible answer."}}]}

            data: {"timings":{"prompt_ms":12.5,"predicted_per_second":8.0},"choices":[]}

            data: [DONE]

            """;
        RecordingHandler handler = new(eventStream);
        FakeHttpClientFactory factory = new(handler);
        FakeSupervisor supervisor = new();
        RecordingMetrics metrics = new();
        await using LlamaCppLocalLanguageModel model = new(supervisor, factory, metrics);
        LanguageModelRequest request = new(
            [new ConversationMessage(ConversationRole.User, "hello")],
            64);

        string first = await CollectAsync(model.GenerateAsync(request, CancellationToken.None));
        string second = await CollectAsync(model.GenerateAsync(request, CancellationToken.None));

        Assert.Equal("Visible answer.", first);
        Assert.Equal("Visible answer.", second);
        Assert.Equal(1, factory.CreateCount);
        Assert.Single(metrics.Items, static metric =>
            metric.Kind == VoiceMetricKind.LanguageModelReady);
        Assert.Equal(2, handler.RequestBodies.Count);
        Assert.All(handler.RequestBodies, static body =>
        {
            Assert.Contains("/no_think", body, StringComparison.Ordinal);
            Assert.DoesNotContain("private reasoning", body, StringComparison.Ordinal);
        });
        Assert.Contains(metrics.Items, static metric =>
            metric.Kind == VoiceMetricKind.PromptProcessing && metric.Value == 12.5);
        Assert.Contains(metrics.Items, static metric =>
            metric.Kind == VoiceMetricKind.TokensPerSecond && metric.Value == 8.0);
    }

    [Fact]
    public async Task CancellationInterruptsAnInFlightLocalRequest()
    {
        BlockingHandler handler = new();
        await using LlamaCppLocalLanguageModel model = new(
            new FakeSupervisor(),
            new FakeHttpClientFactory(handler),
            new RecordingMetrics());
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CollectAsync(model.GenerateAsync(
                new LanguageModelRequest(
                    [new ConversationMessage(ConversationRole.User, "wait")],
                    64),
                cancellation.Token)));
    }

    [Fact]
    public async Task MalformedStreamEventIsRejected()
    {
        await using LlamaCppLocalLanguageModel model = new(
            new FakeSupervisor(),
            new FakeHttpClientFactory(new RecordingHandler("data: {not-json}\n")),
            new RecordingMetrics());

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(model.GenerateAsync(CreateRequest(), CancellationToken.None)));
    }

    [Fact]
    public async Task AggregateVisibleOutputLimitIsEnforced()
    {
        string content = new('x', VoiceDataLimits.MaximumTextCharacters + 1);
        string json = JsonSerializer.Serialize(new
        {
            choices = new[] { new { delta = new { content } } },
        });
        await using LlamaCppLocalLanguageModel model = new(
            new FakeSupervisor(),
            new FakeHttpClientFactory(new RecordingHandler($"data: {json}\n")),
            new RecordingMetrics());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(model.GenerateAsync(CreateRequest(), CancellationToken.None)));

        Assert.Contains("size limit", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedEventLineIsRejectedWhileItIsRead()
    {
        string eventStream = "data: " + new string('x', 64 * 1024);
        await using LlamaCppLocalLanguageModel model = new(
            new FakeSupervisor(),
            new FakeHttpClientFactory(new RecordingHandler(eventStream)),
            new RecordingMetrics());

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await CollectAsync(model.GenerateAsync(CreateRequest(), CancellationToken.None)));

        Assert.Contains("event", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HttpFailureReturnsStableLocalComponentError()
    {
        await using LlamaCppLocalLanguageModel model = new(
            new FakeSupervisor(),
            new FakeHttpClientFactory(new StatusHandler(HttpStatusCode.ServiceUnavailable)),
            new RecordingMetrics());

        LocalComponentUnavailableException exception = await Assert.ThrowsAsync<
            LocalComponentUnavailableException>(async () =>
                await CollectAsync(model.GenerateAsync(CreateRequest(), CancellationToken.None)));

        Assert.Equal("local_llm_request_failed", exception.Code);
    }

    [Fact]
    public async Task ConnectionFailureReturnsStableUnavailableError()
    {
        await using LlamaCppLocalLanguageModel model = new(
            new FakeSupervisor(),
            new FakeHttpClientFactory(new FailingHandler()),
            new RecordingMetrics());

        LocalComponentUnavailableException exception = await Assert.ThrowsAsync<
            LocalComponentUnavailableException>(async () =>
                await CollectAsync(model.GenerateAsync(CreateRequest(), CancellationToken.None)));

        Assert.Equal("local_llm_unavailable", exception.Code);
    }

    private static LanguageModelRequest CreateRequest() =>
        new(
            [new ConversationMessage(ConversationRole.User, "test")],
            64);

    private static async Task<string> CollectAsync(
        IAsyncEnumerable<LanguageModelToken> tokens)
    {
        List<string> output = [];
        await foreach (LanguageModelToken token in tokens)
        {
            output.Add(token.Text);
        }

        return string.Concat(output);
    }

    private sealed class FakeSupervisor : ILlamaServerSupervisor
    {
        public ValueTask<LlamaServerConnection> EnsureReadyAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new LlamaServerConnection(
                new Uri("http://127.0.0.1:18080/"),
                "test-ephemeral-token",
                8192));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) :
        ILoopbackHttpClientFactory
    {
        private int _createCount;

        public int CreateCount => Volatile.Read(ref _createCount);

        public HttpClient Create(Uri endpoint, string? authenticationToken)
        {
            _ = authenticationToken;
            Interlocked.Increment(ref _createCount);
            return new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = endpoint,
                Timeout = Timeout.InfiniteTimeSpan,
            };
        }
    }

    private sealed class RecordingHandler(string eventStream) : HttpMessageHandler
    {
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(eventStream),
            };
        }
    }

    private sealed class BlockingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class StatusHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            throw new HttpRequestException("simulated loopback disconnect");
        }
    }

    private sealed class RecordingMetrics : IVoiceMetrics
    {
        public List<VoiceMetric> Items { get; } = [];

        public void Record(VoiceMetric metric) => Items.Add(metric);
    }
}
