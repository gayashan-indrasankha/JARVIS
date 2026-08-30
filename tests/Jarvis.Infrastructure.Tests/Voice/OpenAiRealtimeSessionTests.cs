using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.Voice.OpenAi;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Infrastructure.Tests.Voice;

public sealed class OpenAiRealtimeSessionTests
{
    [Fact]
    public async Task RemoteDisconnectReconnectsWithoutEndingSession()
    {
        FakeTransport first = new();
        FakeTransport second = new();
        FakeTransportFactory factory = new(first, second);
        OpenAiRealtimeOptions options = CreateOptions();
        await using OpenAiRealtimeSession session = new(
            factory,
            options,
            NullLogger.Instance);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        await session.StartAsync(
            new RealtimeSessionConfiguration(
                VoiceActivationMode.ServerVoiceActivityDetection,
                "Test instructions"),
            timeout.Token);
        first.CloseFromRemote();

        await WaitUntilAsync(() => factory.CreatedCount >= 2, timeout.Token);
        await WaitUntilAsync(() => second.SessionUpdateSent, timeout.Token);
        bool accepted = await session.SendInputAudioAsync(
            new byte[] { 1, 2, 3, 4 },
            timeout.Token);
        await WaitUntilAsync(() => second.AudioAppendSent, timeout.Token);

        Assert.True(accepted);
        Assert.Equal(2, factory.CreatedCount);
        Assert.Equal("Bearer test-secret", second.AuthorizationHeader);
        Assert.Equal("gpt-realtime-test", second.Endpoint?.Query.TrimStart('?').Split('=')[1]);
    }

    [Fact]
    public async Task InitialNetworkFailureIsRetriedAndCanConnect()
    {
        FakeTransport failed = new(connectException: new IOException("offline"));
        FakeTransport recovered = new();
        FakeTransportFactory factory = new(failed, recovered);
        await using OpenAiRealtimeSession session = new(
            factory,
            CreateOptions(),
            NullLogger.Instance);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        await session.StartAsync(
            new RealtimeSessionConfiguration(
                VoiceActivationMode.ServerVoiceActivityDetection,
                "Test instructions"),
            timeout.Token);

        Assert.Equal(2, factory.CreatedCount);
        Assert.True(recovered.SessionUpdateSent);
    }

    private static OpenAiRealtimeOptions CreateOptions() =>
        new()
        {
            ApiKey = "test-secret",
            Endpoint = new Uri("wss://unit.test/realtime"),
            Model = "gpt-realtime-test",
            Voice = "marin",
            ConnectTimeoutSeconds = 2,
            MaximumReconnectAttempts = 3,
            InitialReconnectDelayMilliseconds = 1,
            MaximumReconnectDelayMilliseconds = 2,
        };

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    private sealed class FakeTransportFactory(params FakeTransport[] transports) :
        IRealtimeTransportFactory
    {
        private readonly ConcurrentQueue<FakeTransport> _transports = new(transports);
        private int _createdCount;

        public int CreatedCount => Volatile.Read(ref _createdCount);

        public IRealtimeTransport Create()
        {
            Interlocked.Increment(ref _createdCount);
            return _transports.TryDequeue(out FakeTransport? transport)
                ? transport
                : new FakeTransport();
        }
    }

    private sealed class FakeTransport(Exception? connectException = null) : IRealtimeTransport
    {
        private readonly Channel<ReceiveResult> _incoming =
            Channel.CreateUnbounded<ReceiveResult>();

        public string? AuthorizationHeader { get; private set; }

        public Uri? Endpoint { get; private set; }

        public bool SessionUpdateSent { get; private set; }

        public bool AudioAppendSent { get; private set; }

        public Task ConnectAsync(
            Uri endpoint,
            IReadOnlyDictionary<string, string> headers,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (connectException is not null)
            {
                throw connectException;
            }

            Endpoint = endpoint;
            AuthorizationHeader = headers["Authorization"];
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string message = Encoding.UTF8.GetString(payload.Span);
            if (message.Contains("session.update", StringComparison.Ordinal))
            {
                SessionUpdateSent = true;
                _incoming.Writer.TryWrite(
                    new ReceiveResult("""{"type":"session.updated"}"""u8.ToArray()));
            }

            if (message.Contains("input_audio_buffer.append", StringComparison.Ordinal))
            {
                AudioAppendSent = true;
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<RealtimeTransportMessage> ReceiveAsync(
            CancellationToken cancellationToken)
        {
            ReceiveResult result = await _incoming.Reader
                .ReadAsync(cancellationToken)
                .ConfigureAwait(false);
            return result.Closed
                ? new RealtimeTransportMessage(default, IsClosed: true)
                : new RealtimeTransportMessage(result.Payload);
        }

        public void CloseFromRemote() =>
            Assert.True(_incoming.Writer.TryWrite(new ReceiveResult(Closed: true)));

        public ValueTask CloseAsync(CancellationToken cancellationToken)
        {
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        private readonly record struct ReceiveResult(
            ReadOnlyMemory<byte> Payload = default,
            bool Closed = false);
    }
}
