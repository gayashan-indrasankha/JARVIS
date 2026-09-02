using System.Diagnostics;

namespace Jarvis.Core.Voice;

public sealed partial class RealtimeVoiceCoordinator
{
    private VoiceSessionConfiguration? _alwaysListeningConfiguration;
    private Task? _wakeTask;
    private Task? _idleTask;
    private CancellationTokenSource? _idleCancellation;
    private DateTimeOffset? _lastWakeAcceptedAt;
    private long _falseActivationCount;

    /// <summary>
    /// Arms the local wake-word listener without initializing conversation models.
    /// </summary>
    public async Task ArmWakeWordAsync(
        VoiceSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!configuration.WakeWord.AlwaysListeningEnabled)
        {
            throw new InvalidOperationException("Always-listening wake-word detection is disabled.");
        }

        if (!configuration.SpeechInputEnabled)
        {
            throw new InvalidOperationException("Wake-word detection requires speech input.");
        }

        if (!_wakeWordDetector.IsAvailable)
        {
            throw new LocalComponentUnavailableException(
                "wake_word_detector_unavailable",
                "The configured local wake-word detector is unavailable.");
        }

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != VoiceSessionState.Stopped)
            {
                throw new InvalidOperationException("The voice session is already active.");
            }

            _alwaysListeningConfiguration = configuration;
            _sessionCancellation = new CancellationTokenSource();
            SetState(VoiceSessionState.Sleeping);
            StartWakeListeningUnsafe(configuration, _sessionCancellation.Token);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    /// <summary>
    /// Records user-confirmed or otherwise measured false activations without audio content.
    /// </summary>
    public void RecordFalseActivation()
    {
        long count = Interlocked.Increment(ref _falseActivationCount);
        _metrics.Record(new VoiceMetric(VoiceMetricKind.FalseActivationCount, count));
    }

    private async ValueTask InitializeConversationUnsafeAsync(
        VoiceSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        await _agentRuntime.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (configuration.SpeechInputEnabled)
        {
            await _voiceActivityDetector.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _speechRecognizer.InitializeAsync(cancellationToken).ConfigureAwait(false);
            await _voiceActivityDetector.ResetAsync(cancellationToken).ConfigureAwait(false);
            await _speechRecognizer.ResetAsync(cancellationToken).ConfigureAwait(false);
        }

        if (configuration.SpeechOutputEnabled)
        {
            await _speechSynthesizer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void StartWakeListeningUnsafe(
        VoiceSessionConfiguration configuration,
        CancellationToken sessionToken)
    {
        if (_wakeTask is { IsCompleted: false })
        {
            throw new InvalidOperationException("Wake-word listening is already active.");
        }

        SetCaptureState(VoiceCaptureState.WakeWord);
        _wakeTask = ListenForWakeWordAsync(configuration, sessionToken);
    }

    private async Task ListenForWakeWordAsync(
        VoiceSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                bool detectionProduced = false;
                await foreach (WakeWordDetection detection in
                    _wakeWordDetector.ListenAsync(cancellationToken).ConfigureAwait(false))
                {
                    detectionProduced = true;
                    if (await TryActivateFromWakeAsync(configuration, detection, cancellationToken)
                        .ConfigureAwait(false))
                    {
                        return;
                    }
                }

                if (!detectionProduced)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), _timeProvider, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await HandleWakeFailureAsync(exception, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> TryActivateFromWakeAsync(
        VoiceSessionConfiguration configuration,
        WakeWordDetection detection,
        CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state != VoiceSessionState.Sleeping ||
                !ReferenceEquals(configuration, _alwaysListeningConfiguration))
            {
                return false;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_lastWakeAcceptedAt is DateTimeOffset previous &&
                now - previous < configuration.WakeWord.Cooldown)
            {
                RecordFalseActivation();
                return false;
            }

            _lastWakeAcceptedAt = now;
            _metrics.Record(new VoiceMetric(
                VoiceMetricKind.KeywordDetectionLatency,
                detection.ProcessingLatency.TotalMilliseconds));
            Publish(new WakeWordDetectedNotification(configuration.WakeWord.Phrase));
            SetCaptureState(VoiceCaptureState.Off);
            SetState(VoiceSessionState.Activating);
            Stopwatch wakeToListening = Stopwatch.StartNew();

            _configuration = configuration;
            try
            {
                await InitializeConversationUnsafeAsync(configuration, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _configuration = null;
                return true;
            }
            catch (Exception exception)
            {
                _configuration = null;
                SetState(VoiceSessionState.Faulted);
                Publish(new VoiceSessionErrorNotification(
                    GetSafeErrorCode(exception, "wake_activation_failed"),
                    IsTransient: false));
                return true;
            }

            if (configuration.ActivationMode == VoiceActivationMode.VoiceActivityDetection)
            {
                StartCapturePump(
                    useVoiceActivityDetection: true,
                    VoiceCaptureState.Conversation,
                    _sessionCancellation!.Token);
            }

            _metrics.Record(new VoiceMetric(
                VoiceMetricKind.WakeToListeningLatency,
                wakeToListening.Elapsed.TotalMilliseconds));
            SetState(VoiceSessionState.Listening);
            RefreshContinuationWindowUnsafe();
            StartAcknowledgementUnsafe(configuration);
            return true;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private async Task HandleWakeFailureAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        try
        {
            await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            if (_state == VoiceSessionState.Sleeping)
            {
                SetCaptureState(VoiceCaptureState.Off);
                SetState(VoiceSessionState.Faulted);
                Publish(new VoiceSessionErrorNotification(
                    GetSafeErrorCode(exception, "wake_word_detection_failed"),
                    IsTransient: false));
            }
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private void StartAcknowledgementUnsafe(VoiceSessionConfiguration configuration)
    {
        if (!configuration.SpeechOutputEnabled ||
            string.IsNullOrWhiteSpace(configuration.WakeWord.Acknowledgement))
        {
            return;
        }

        long generationId;
        checked
        {
            generationId = ++_generationId;
        }

        _generationCancellation?.Dispose();
        _generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionCancellation!.Token);
        CancellationToken generationToken = _generationCancellation.Token;
        _generationTask = RunAcknowledgementAsync(
            configuration.WakeWord.Acknowledgement,
            generationId,
            generationToken);
    }

    private async Task RunAcknowledgementAsync(
        string acknowledgement,
        long generationId,
        CancellationToken cancellationToken)
    {
        try
        {
            SpeechSynthesisRequest request = new(acknowledgement, generationId);
            await foreach (SynthesizedAudioChunk audio in
                _speechSynthesizer.SynthesizeAsync(request, cancellationToken)
                    .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentGeneration(generationId))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                await _playback.EnqueueAsync(
                    new AssistantAudioChunk(audio.Data, generationId),
                    cancellationToken).ConfigureAwait(false);
                SetState(VoiceSessionState.Speaking);
            }

            await ReturnToListeningAsync(generationId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (IsCurrentGeneration(generationId))
            {
                SetState(VoiceSessionState.Faulted);
                Publish(new VoiceSessionErrorNotification(
                    GetSafeErrorCode(exception, "wake_acknowledgement_failed"),
                    IsTransient: false));
            }
        }
    }

    private void RefreshContinuationWindowUnsafe()
    {
        if (_alwaysListeningConfiguration is null ||
            _sessionCancellation is null ||
            _state is VoiceSessionState.Stopped or VoiceSessionState.Sleeping or VoiceSessionState.Faulted)
        {
            return;
        }

        _idleCancellation?.Cancel();
        _idleCancellation?.Dispose();
        _idleCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionCancellation.Token);
        CancellationToken token = _idleCancellation.Token;
        VoiceSessionConfiguration configuration = _alwaysListeningConfiguration;
        _idleTask = WaitForIdleAsync(configuration, token);
    }

    private async Task WaitForIdleAsync(
        VoiceSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                configuration.WakeWord.ContinuationWindow,
                _timeProvider,
                cancellationToken).ConfigureAwait(false);
            await TransitionToSleepingAsync(configuration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task TransitionToSleepingAsync(
        VoiceSessionConfiguration configuration,
        CancellationToken cancellationToken)
    {
        Task? capturePump;
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(configuration, _alwaysListeningConfiguration) ||
                _state is VoiceSessionState.Stopped or VoiceSessionState.Sleeping or VoiceSessionState.Faulted)
            {
                return;
            }

            if (_speechActive || _pushToTalkCaptureActive ||
                _generationTask is { IsCompleted: false })
            {
                RefreshContinuationWindowUnsafe();
                return;
            }

            capturePump = StopCapturePump();
            _configuration = null;
            _preRollFrames.Clear();
            lock (_historySync)
            {
                _history.Clear();
            }

            SetState(VoiceSessionState.Sleeping);
        }
        finally
        {
            _controlGate.Release();
        }

        await ObserveExpectedCancellationAsync(capturePump).ConfigureAwait(false);
        await _playback.StopAsync(cancellationToken).ConfigureAwait(false);

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == VoiceSessionState.Sleeping &&
                ReferenceEquals(configuration, _alwaysListeningConfiguration) &&
                _sessionCancellation is { IsCancellationRequested: false })
            {
                StartWakeListeningUnsafe(configuration, _sessionCancellation.Token);
            }
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private static string GetSafeErrorCode(Exception exception, string fallback) =>
        exception is LocalComponentUnavailableException unavailable
            ? unavailable.Code
            : fallback;

    private sealed class UnavailableWakeWordDetector : IWakeWordDetector
    {
        public static UnavailableWakeWordDetector Instance { get; } = new();

        public bool IsAvailable => false;

        public async IAsyncEnumerable<WakeWordDetection> ListenAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation]
            CancellationToken cancellationToken)
        {
            await Task.CompletedTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
