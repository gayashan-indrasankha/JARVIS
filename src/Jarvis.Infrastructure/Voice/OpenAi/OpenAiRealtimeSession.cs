using System.Net.WebSockets;
using System.Threading.Channels;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace Jarvis.Infrastructure.Voice.OpenAi;

internal sealed class OpenAiRealtimeSession : IRealtimeConversationSession
{
    private readonly IRealtimeTransportFactory _transportFactory;
    private readonly OpenAiRealtimeOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<IReadOnlyList<byte[]>> _controlMessages;
    private readonly Channel<byte[]> _audioMessages;
    private readonly Channel<RealtimeConversationEvent> _events;
    private readonly SemaphoreSlim _outboundSignal = new(0);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly TaskCompletionSource _firstConnection =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _runTask;
    private RealtimeSessionConfiguration? _configuration;
    private int _connected;
    private int _disposed;

    public OpenAiRealtimeSession(
        IRealtimeTransportFactory transportFactory,
        OpenAiRealtimeOptions options,
        ILogger logger,
        TimeProvider? timeProvider = null)
    {
        _transportFactory = transportFactory;
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _controlMessages = Channel.CreateBounded<IReadOnlyList<byte[]>>(
            new BoundedChannelOptions(64)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
        _audioMessages = Channel.CreateBounded<byte[]>(
            new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        _events = Channel.CreateBounded<RealtimeConversationEvent>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
    }

    public async Task StartAsync(
        RealtimeSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _configuration = configuration;
        _runTask = RunConnectionLoopAsync(_lifetimeCancellation.Token);

        TimeSpan timeout = TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds);
        await _firstConnection.Task
            .WaitAsync(timeout, cancellationToken)
            .ConfigureAwait(false);
    }

    public IAsyncEnumerable<RealtimeConversationEvent> ReadEventsAsync(
        CancellationToken cancellationToken) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    public ValueTask<bool> SendInputAudioAsync(
        ReadOnlyMemory<byte> audio,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (audio.Length > VoiceDataLimits.MaximumAudioChunkBytes)
        {
            throw new ArgumentException("Input audio exceeds the size limit.", nameof(audio));
        }

        if (Volatile.Read(ref _connected) == 0 || audio.IsEmpty)
        {
            return ValueTask.FromResult(false);
        }

        byte[] message = OpenAiRealtimeProtocol.CreateAudioAppend(audio.Span);
        bool accepted = _audioMessages.Writer.TryWrite(message);
        if (accepted)
        {
            _outboundSignal.Release();
        }

        return ValueTask.FromResult(accepted);
    }

    public ValueTask SubmitTextAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text input cannot be empty.", nameof(text));
        }
        if (text.Length > VoiceDataLimits.MaximumTextCharacters)
        {
            throw new ArgumentException("Text input exceeds the size limit.", nameof(text));
        }

        return EnqueueControlAsync(OpenAiRealtimeProtocol.CreateTextTurn(text), cancellationToken);
    }

    public ValueTask CompleteInputTurnAsync(CancellationToken cancellationToken) =>
        EnqueueControlAsync(OpenAiRealtimeProtocol.CreateAudioCommit(), cancellationToken);

    public ValueTask CancelResponseAsync(CancellationToken cancellationToken) =>
        EnqueueControlAsync(
            [OpenAiRealtimeProtocol.CreateResponseCancellation()],
            cancellationToken);

    public ValueTask TruncateResponseAsync(
        PlaybackCursor cursor,
        CancellationToken cancellationToken) =>
        EnqueueControlAsync(
            [OpenAiRealtimeProtocol.CreateTruncation(cursor)],
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        _controlMessages.Writer.TryComplete();
        _audioMessages.Writer.TryComplete();

        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
        }

        _events.Writer.TryComplete();
        _outboundSignal.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async Task RunConnectionLoopAsync(CancellationToken cancellationToken)
    {
        int failedAttempts = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string reasonCode;
                try
                {
                    await RunSingleConnectionAsync(cancellationToken).ConfigureAwait(false);
                    reasonCode = "remote_closed";
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (TimeoutException)
                {
                    reasonCode = "connection_timeout";
                }
                catch (Exception exception) when (
                    exception is WebSocketException or IOException or InvalidDataException)
                {
                    reasonCode = ClassifyConnectionFailure(exception);
                }
                catch (Exception)
                {
                    reasonCode = "unexpected_connection_failure";
                }

                Volatile.Write(ref _connected, 0);
                DrainOutboundQueues();
                failedAttempts++;

                if (failedAttempts > _options.MaximumReconnectAttempts)
                {
                    OpenAiRealtimeLog.ReconnectLimitReached(_logger, failedAttempts, reasonCode);
                    _firstConnection.TrySetException(
                        new InvalidOperationException("The realtime provider could not be reached."));
                    await _events.Writer
                        .WriteAsync(new RealtimeDisconnectedEvent(reasonCode), cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }

                OpenAiRealtimeLog.Reconnecting(_logger, failedAttempts, reasonCode);
                await _events.Writer
                    .WriteAsync(
                        new RealtimeReconnectingEvent(failedAttempts, reasonCode),
                        cancellationToken)
                    .ConfigureAwait(false);

                TimeSpan delay = CalculateReconnectDelay(failedAttempts);
                await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _connected, 0);
            _events.Writer.TryComplete();
        }
    }

    private async Task RunSingleConnectionAsync(CancellationToken cancellationToken)
    {
        RealtimeSessionConfiguration configuration = _configuration ??
            throw new InvalidOperationException("The realtime session was not configured.");

        await using IRealtimeTransport transport = _transportFactory.Create();
        using CancellationTokenSource connectionCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Uri endpoint = BuildEndpoint(_options.Endpoint, _options.Model);
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {_options.ApiKey}",
        };

        try
        {
            await transport
                .ConnectAsync(endpoint, headers, connectionCancellation.Token)
                .ConfigureAwait(false);
            await transport
                .SendAsync(
                    OpenAiRealtimeProtocol.CreateSessionUpdate(configuration, _options),
                    connectionCancellation.Token)
                .ConfigureAwait(false);

            TaskCompletionSource handshake =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task receiveTask = ReceiveLoopAsync(
                transport,
                handshake,
                connectionCancellation.Token);

            await handshake.Task
                .WaitAsync(
                    TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds),
                    connectionCancellation.Token)
                .ConfigureAwait(false);

            Volatile.Write(ref _connected, 1);
            _firstConnection.TrySetResult();
            OpenAiRealtimeLog.Connected(_logger);

            Task sendTask = SendLoopAsync(transport, connectionCancellation.Token);
            _ = await Task.WhenAny(receiveTask, sendTask).ConfigureAwait(false);
            connectionCancellation.Cancel();
            await ObserveConnectionTaskAsync(receiveTask).ConfigureAwait(false);
            await ObserveConnectionTaskAsync(sendTask).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _connected, 0);
            using CancellationTokenSource closeCancellation = new(TimeSpan.FromSeconds(2));
            try
            {
                await transport.CloseAsync(closeCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or WebSocketException)
            {
            }
        }
    }

    private async Task ReceiveLoopAsync(
        IRealtimeTransport transport,
        TaskCompletionSource handshake,
        CancellationToken cancellationToken)
    {
        OpenAiRealtimeEventParser parser = new();
        while (!cancellationToken.IsCancellationRequested)
        {
            RealtimeTransportMessage message = await transport
                .ReceiveAsync(cancellationToken)
                .ConfigureAwait(false);
            if (message.IsClosed)
            {
                handshake.TrySetException(new IOException("The realtime connection closed."));
                return;
            }

            RealtimeConversationEvent? conversationEvent = parser.Parse(message.Payload);
            if (conversationEvent is null)
            {
                continue;
            }

            if (conversationEvent is RealtimeConnectedEvent)
            {
                handshake.TrySetResult();
            }

            await _events.Writer
                .WriteAsync(conversationEvent, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task SendLoopAsync(
        IRealtimeTransport transport,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await _outboundSignal.WaitAsync(cancellationToken).ConfigureAwait(false);

            while (_controlMessages.Reader.TryRead(out IReadOnlyList<byte[]>? command))
            {
                foreach (byte[] message in command)
                {
                    await transport.SendAsync(message, cancellationToken).ConfigureAwait(false);
                }
            }

            if (_audioMessages.Reader.TryRead(out byte[]? audioMessage))
            {
                await transport.SendAsync(audioMessage, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask EnqueueControlAsync(
        IReadOnlyList<byte[]> messages,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _connected) == 0)
        {
            throw new InvalidOperationException("The realtime provider is reconnecting.");
        }

        await _controlMessages.Writer
            .WriteAsync(messages, cancellationToken)
            .ConfigureAwait(false);
        _outboundSignal.Release();
    }

    private TimeSpan CalculateReconnectDelay(int attempt)
    {
        double exponential = _options.InitialReconnectDelayMilliseconds *
            Math.Pow(2, Math.Min(attempt - 1, 16));
        double bounded = Math.Min(exponential, _options.MaximumReconnectDelayMilliseconds);
        double jittered = bounded * (0.8 + (Random.Shared.NextDouble() * 0.4));
        return TimeSpan.FromMilliseconds(
            Math.Min(jittered, _options.MaximumReconnectDelayMilliseconds));
    }

    private void DrainOutboundQueues()
    {
        while (_controlMessages.Reader.TryRead(out _))
        {
        }

        while (_audioMessages.Reader.TryRead(out _))
        {
        }

        while (_outboundSignal.Wait(0))
        {
        }
    }

    private static Uri BuildEndpoint(Uri endpoint, string model)
    {
        UriBuilder builder = new(endpoint)
        {
            Query = $"model={Uri.EscapeDataString(model)}",
        };
        return builder.Uri;
    }

    private static string ClassifyConnectionFailure(Exception exception) =>
        exception switch
        {
            WebSocketException => "websocket_failure",
            InvalidDataException => "protocol_failure",
            _ => "network_failure",
        };

    private static async Task ObserveConnectionTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

internal static partial class OpenAiRealtimeLog
{
    [LoggerMessage(
        EventId = 2100,
        Level = LogLevel.Information,
        Message = "OpenAI realtime session connected")]
    public static partial void Connected(ILogger logger);

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Warning,
        Message = "OpenAI realtime session reconnect attempt {Attempt}; reason {ReasonCode}")]
    public static partial void Reconnecting(ILogger logger, int attempt, string reasonCode);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Error,
        Message = "OpenAI realtime session stopped after {Attempts} failures; reason {ReasonCode}")]
    public static partial void ReconnectLimitReached(
        ILogger logger,
        int attempts,
        string reasonCode);
}
