using System.Threading.Channels;

namespace Jarvis.Core.Voice;

/// <summary>
/// Serializes the provider-neutral realtime voice lifecycle and barge-in behavior.
/// </summary>
public sealed class RealtimeVoiceCoordinator : IAsyncDisposable
{
    private readonly IRealtimeConversationProvider _provider;
    private readonly IAudioCapture _capture;
    private readonly IAudioPlayback _playback;
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly Channel<VoiceSessionNotification> _notifications;

    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _captureCancellation;
    private volatile IRealtimeConversationSession? _session;
    private Task? _eventPump;
    private Task? _capturePump;
    private VoiceActivationMode _activationMode;
    private volatile string? _suppressedItemId;
    private volatile VoiceSessionState _state = VoiceSessionState.Stopped;
    private volatile bool _pushToTalkCaptureActive;
    private bool _disposed;

    public RealtimeVoiceCoordinator(
        IRealtimeConversationProvider provider,
        IAudioCapture capture,
        IAudioPlayback playback)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));

        if (_capture.Format != _playback.Format)
        {
            throw new ArgumentException("Capture and playback must use the same Core audio format.");
        }

        _notifications = Channel.CreateBounded<VoiceSessionNotification>(
            new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    public VoiceSessionState State => _state;

    public IAsyncEnumerable<VoiceSessionNotification> ReadNotificationsAsync(
        CancellationToken cancellationToken) =>
        _notifications.Reader.ReadAllAsync(cancellationToken);

    public async Task StartAsync(
        RealtimeSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != VoiceSessionState.Stopped)
            {
                throw new InvalidOperationException("The voice session is already active.");
            }

            SetState(VoiceSessionState.Activating);
            _activationMode = configuration.ActivationMode;
            _sessionCancellation = new CancellationTokenSource();

            try
            {
                _session = await _provider
                    .OpenSessionAsync(configuration, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                _sessionCancellation.Dispose();
                _sessionCancellation = null;
                SetState(VoiceSessionState.Stopped);
                throw;
            }

            CancellationToken sessionToken = _sessionCancellation.Token;
            _eventPump = ProcessEventsAsync(_session, sessionToken);
            SetState(VoiceSessionState.Listening);

            if (_activationMode == VoiceActivationMode.ServerVoiceActivityDetection)
            {
                StartCapturePump(sessionToken);
            }
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task BeginPushToTalkAsync(CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActivePushToTalkSession();
            if (_pushToTalkCaptureActive)
            {
                throw new InvalidOperationException("Push-to-talk capture is already active.");
            }

            await InterruptPlaybackAsync(cancelResponse: true, cancellationToken).ConfigureAwait(false);
            _pushToTalkCaptureActive = true;
            SetState(VoiceSessionState.Listening);
            StartCapturePump(_sessionCancellation!.Token);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task EndPushToTalkAsync(CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActivePushToTalkSession();
            if (!_pushToTalkCaptureActive)
            {
                throw new InvalidOperationException("Push-to-talk capture is not active.");
            }

            Task? capturePump = StopCapturePump();
            _pushToTalkCaptureActive = false;
            await ObserveExpectedCancellationAsync(capturePump).ConfigureAwait(false);

            if (_session is null)
            {
                return;
            }

            await _session.CompleteInputTurnAsync(cancellationToken).ConfigureAwait(false);
            SetState(VoiceSessionState.AwaitingResponse);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task SubmitTextAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text input cannot be empty.", nameof(text));
        }

        if (text.Length > VoiceDataLimits.MaximumTextCharacters)
        {
            throw new ArgumentException("Text input exceeds the size limit.", nameof(text));
        }

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActiveSession();
            if (_pushToTalkCaptureActive)
            {
                throw new InvalidOperationException(
                    "Finish the active push-to-talk capture before submitting text.");
            }

            await InterruptPlaybackAsync(cancelResponse: true, cancellationToken).ConfigureAwait(false);
            await _session!.SubmitTextAsync(text, cancellationToken).ConfigureAwait(false);
            SetState(VoiceSessionState.AwaitingResponse);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task InterruptAsync(CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActiveSession();
            await InterruptPlaybackAsync(cancelResponse: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        IRealtimeConversationSession? session;
        CancellationTokenSource? sessionCancellation;
        Task? eventPump;
        Task? capturePump;

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == VoiceSessionState.Stopped)
            {
                return;
            }

            session = _session;
            sessionCancellation = _sessionCancellation;
            eventPump = _eventPump;
            capturePump = StopCapturePump();

            _session = null;
            _sessionCancellation = null;
            _eventPump = null;
            _suppressedItemId = null;
            _pushToTalkCaptureActive = false;

            sessionCancellation?.Cancel();
            SetState(VoiceSessionState.Stopped);
        }
        finally
        {
            _controlGate.Release();
        }

        if (session is not null)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        await ObserveExpectedCancellationAsync(capturePump).ConfigureAwait(false);
        await ObserveExpectedCancellationAsync(eventPump).ConfigureAwait(false);
        await _playback.StopAsync(cancellationToken).ConfigureAwait(false);
        sessionCancellation?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _notifications.Writer.TryComplete();
        _controlGate.Dispose();
        await _capture.DisposeAsync().ConfigureAwait(false);
        await _playback.DisposeAsync().ConfigureAwait(false);
    }

    private async Task ProcessEventsAsync(
        IRealtimeConversationSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (RealtimeConversationEvent conversationEvent in
                session.ReadEventsAsync(cancellationToken).ConfigureAwait(false))
            {
                switch (conversationEvent)
                {
                    case RealtimeConnectedEvent:
                        SetState(VoiceSessionState.Listening);
                        break;
                    case RealtimeReconnectingEvent reconnecting:
                        await _playback.InterruptAsync(cancellationToken).ConfigureAwait(false);
                        SetState(VoiceSessionState.Recovering);
                        Publish(new VoiceSessionErrorNotification(
                            reconnecting.ReasonCode,
                            IsTransient: true));
                        break;
                    case RealtimeDisconnectedEvent disconnected:
                        await EnterFaultedStateAsync(
                            disconnected.ReasonCode,
                            cancellationToken).ConfigureAwait(false);
                        break;
                    case AssistantAudioDeltaEvent audio
                        when !string.Equals(
                            audio.Chunk.ItemId,
                            _suppressedItemId,
                            StringComparison.Ordinal):
                        await _playback.EnqueueAsync(audio.Chunk, cancellationToken).ConfigureAwait(false);
                        SetState(VoiceSessionState.Speaking);
                        break;
                    case AssistantTranscriptDeltaEvent transcript:
                        Publish(new AssistantTranscriptNotification(transcript.Text));
                        break;
                    case UserSpeechStartedEvent:
                        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            await InterruptPlaybackAsync(
                                cancelResponse: false,
                                cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            _controlGate.Release();
                        }

                        break;
                    case UserSpeechStoppedEvent:
                        SetState(VoiceSessionState.AwaitingResponse);
                        break;
                    case AssistantResponseCompletedEvent completed:
                        if (string.Equals(
                            completed.ItemId,
                            _suppressedItemId,
                            StringComparison.Ordinal))
                        {
                            _suppressedItemId = null;
                        }

                        SetState(VoiceSessionState.Listening);
                        break;
                    case RealtimeProviderErrorEvent error:
                        if (error.IsTransient)
                        {
                            Publish(new VoiceSessionErrorNotification(
                                error.Code,
                                IsTransient: true));
                        }
                        else
                        {
                            await EnterFaultedStateAsync(
                                error.Code,
                                cancellationToken).ConfigureAwait(false);
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            await EnterFaultedStateAsync(
                "event_pump_failed",
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private void StartCapturePump(CancellationToken sessionToken)
    {
        if (_capturePump is { IsCompleted: false })
        {
            throw new InvalidOperationException("Microphone capture is already active.");
        }

        _captureCancellation?.Dispose();
        _captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _capturePump = PumpCaptureAsync(_captureCancellation.Token);
    }

    private Task? StopCapturePump()
    {
        Task? capturePump = _capturePump;
        _capturePump = null;
        _captureCancellation?.Cancel();
        _captureCancellation?.Dispose();
        _captureCancellation = null;
        return capturePump;
    }

    private async Task PumpCaptureAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (AudioFrame frame in
                _capture.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                IRealtimeConversationSession? session = _session;
                if (session is not null)
                {
                    _ = await session
                        .SendInputAudioAsync(frame.Data, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _pushToTalkCaptureActive = false;
            SetState(VoiceSessionState.Faulted);
            Publish(new VoiceSessionErrorNotification("audio_capture_failed", IsTransient: false));
        }
    }

    private async ValueTask InterruptPlaybackAsync(
        bool cancelResponse,
        CancellationToken cancellationToken)
    {
        IRealtimeConversationSession? session = _session;
        if (session is null)
        {
            return;
        }

        PlaybackCursor? cursor = await _playback
            .InterruptAsync(cancellationToken)
            .ConfigureAwait(false);

        if (cancelResponse &&
            _state is VoiceSessionState.Speaking or VoiceSessionState.AwaitingResponse)
        {
            await session.CancelResponseAsync(cancellationToken).ConfigureAwait(false);
        }

        if (cursor is not null)
        {
            _suppressedItemId = cursor.ItemId;
            await session.TruncateResponseAsync(cursor, cancellationToken).ConfigureAwait(false);
        }

        SetState(VoiceSessionState.Interrupted);
    }

    private async Task EnterFaultedStateAsync(
        string errorCode,
        CancellationToken cancellationToken)
    {
        Task? capturePump;

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            capturePump = StopCapturePump();
            _pushToTalkCaptureActive = false;
            _ = await _playback.InterruptAsync(cancellationToken).ConfigureAwait(false);
            SetState(VoiceSessionState.Faulted);
            Publish(new VoiceSessionErrorNotification(errorCode, IsTransient: false));
        }
        finally
        {
            _controlGate.Release();
        }

        await ObserveExpectedCancellationAsync(capturePump).ConfigureAwait(false);
    }

    private void EnsureActiveSession()
    {
        if (_session is null || _state == VoiceSessionState.Stopped)
        {
            throw new InvalidOperationException("The voice session is not active.");
        }
    }

    private void EnsureActivePushToTalkSession()
    {
        EnsureActiveSession();

        if (_activationMode != VoiceActivationMode.PushToTalk)
        {
            throw new InvalidOperationException("The voice session is not in push-to-talk mode.");
        }
    }

    private void SetState(VoiceSessionState state)
    {
        if (_state == VoiceSessionState.Stopped && state != VoiceSessionState.Activating)
        {
            return;
        }

        if (_state == state)
        {
            return;
        }

        _state = state;
        Publish(new VoiceSessionStateChangedNotification(state));
    }

    private void Publish(VoiceSessionNotification notification) =>
        _notifications.Writer.TryWrite(notification);

    private static async Task ObserveExpectedCancellationAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
