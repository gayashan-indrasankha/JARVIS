using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Jarvis.Core.Voice;

public enum VoiceActivationMode
{
    VoiceActivityDetection,
    PushToTalk,
}

public sealed record ResponseSegmentationConfiguration
{
    public ResponseSegmentationConfiguration(
        int minimumSentenceCharacters = 24,
        int minimumClauseCharacters = 72,
        int maximumSegmentCharacters = 240)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumSentenceCharacters, 8);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumSentenceCharacters, 120);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            minimumClauseCharacters,
            minimumSentenceCharacters);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minimumClauseCharacters, 240);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumSegmentCharacters,
            minimumClauseCharacters);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            maximumSegmentCharacters,
            VoiceDataLimits.MaximumSpeechSegmentCharacters);

        MinimumSentenceCharacters = minimumSentenceCharacters;
        MinimumClauseCharacters = minimumClauseCharacters;
        MaximumSegmentCharacters = maximumSegmentCharacters;
    }

    public int MinimumSentenceCharacters { get; }

    public int MinimumClauseCharacters { get; }

    public int MaximumSegmentCharacters { get; }
}

public sealed record WakeWordSessionConfiguration
{
    public WakeWordSessionConfiguration(
        bool alwaysListeningEnabled = false,
        string phrase = "Jarvis",
        float keywordScore = 1.5F,
        float keywordThreshold = 0.25F,
        TimeSpan? cooldown = null,
        TimeSpan? continuationWindow = null,
        string acknowledgement = "Yes?")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phrase);
        ArgumentNullException.ThrowIfNull(acknowledgement);
        TimeSpan effectiveCooldown = cooldown ?? TimeSpan.FromSeconds(3);
        TimeSpan effectiveContinuationWindow = continuationWindow ?? TimeSpan.FromSeconds(30);
        if (phrase.Length > 32 || phrase.Any(char.IsControl))
        {
            throw new ArgumentException("The wake phrase is invalid.", nameof(phrase));
        }

        if (!float.IsFinite(keywordScore) || keywordScore is < 0.1F or > 10.0F)
        {
            throw new ArgumentOutOfRangeException(nameof(keywordScore));
        }

        if (!float.IsFinite(keywordThreshold) || keywordThreshold is < 0.01F or > 0.99F)
        {
            throw new ArgumentOutOfRangeException(nameof(keywordThreshold));
        }

        if (effectiveCooldown < TimeSpan.FromMilliseconds(100) ||
            effectiveCooldown > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(cooldown));
        }

        if (effectiveContinuationWindow < TimeSpan.FromMilliseconds(250) ||
            effectiveContinuationWindow > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(continuationWindow));
        }

        if (acknowledgement.Length > 80 || acknowledgement.Any(char.IsControl))
        {
            throw new ArgumentException("The wake acknowledgement is invalid.", nameof(acknowledgement));
        }

        AlwaysListeningEnabled = alwaysListeningEnabled;
        Phrase = phrase;
        KeywordScore = keywordScore;
        KeywordThreshold = keywordThreshold;
        Cooldown = effectiveCooldown;
        ContinuationWindow = effectiveContinuationWindow;
        Acknowledgement = acknowledgement;
    }

    public bool AlwaysListeningEnabled { get; }

    public string Phrase { get; }

    public float KeywordScore { get; }

    public float KeywordThreshold { get; }

    public TimeSpan Cooldown { get; }

    public TimeSpan ContinuationWindow { get; }

    public string Acknowledgement { get; }
}

public sealed record VoiceSessionConfiguration
{
    public VoiceSessionConfiguration(
        VoiceActivationMode activationMode,
        string persona,
        bool speechInputEnabled,
        bool speechOutputEnabled,
        int maximumOutputTokens = 512,
        ResponseSegmentationConfiguration? responseSegmentation = null,
        WakeWordSessionConfiguration? wakeWord = null)
    {
        if (!Enum.IsDefined(activationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(activationMode));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(persona);
        if (persona.Length > VoiceDataLimits.MaximumInstructionsCharacters)
        {
            throw new ArgumentException("The persona exceeds the size limit.", nameof(persona));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(maximumOutputTokens, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumOutputTokens, 4_096);

        ActivationMode = activationMode;
        Persona = persona;
        SpeechInputEnabled = speechInputEnabled;
        SpeechOutputEnabled = speechOutputEnabled;
        MaximumOutputTokens = maximumOutputTokens;
        ResponseSegmentation = responseSegmentation ?? new ResponseSegmentationConfiguration();
        WakeWord = wakeWord ?? new WakeWordSessionConfiguration();
    }

    public VoiceActivationMode ActivationMode { get; }

    public string Persona { get; }

    public bool SpeechInputEnabled { get; }

    public bool SpeechOutputEnabled { get; }

    public int MaximumOutputTokens { get; }

    public ResponseSegmentationConfiguration ResponseSegmentation { get; }

    public WakeWordSessionConfiguration WakeWord { get; }
}

/// <summary>
/// Coordinates local VAD, recognition, language generation, synthesis, and interruption.
/// </summary>
public sealed partial class RealtimeVoiceCoordinator : IAsyncDisposable
{
    private const int MaximumHistoryMessages = 12;
    private const int MaximumHistoryCharacters = 24 * 1024;
    private const int MaximumPreRollFrames = 10;

    private readonly IAgentRuntime _agentRuntime;
    private readonly IAudioCapture _capture;
    private readonly IAudioPlayback _playback;
    private readonly IVoiceActivityDetector _voiceActivityDetector;
    private readonly ISpeechRecognizer _speechRecognizer;
    private readonly ISpeechSynthesizer _speechSynthesizer;
    private readonly IVoiceMetrics _metrics;
    private readonly IWakeWordDetector _wakeWordDetector;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _controlGate = new(1, 1);
    private readonly Channel<VoiceSessionNotification> _notifications;
    private readonly List<ConversationMessage> _history = [];
    private readonly Lock _historySync = new();
    private readonly Queue<AudioFrame> _preRollFrames = new();
    private readonly List<Task> _retiredGenerationTasks = [];

    private CancellationTokenSource? _sessionCancellation;
    private CancellationTokenSource? _captureCancellation;
    private CancellationTokenSource? _generationCancellation;
    private Task? _capturePump;
    private Task? _generationTask;
    private VoiceSessionConfiguration? _configuration;
    private volatile VoiceSessionState _state = VoiceSessionState.Stopped;
    private long _generationId;
    private bool _pushToTalkCaptureActive;
    private bool _speechActive;
    private bool _disposed;
    private VoiceCaptureState _captureState;

    public RealtimeVoiceCoordinator(
        IAgentRuntime agentRuntime,
        IAudioCapture capture,
        IAudioPlayback playback,
        IVoiceActivityDetector voiceActivityDetector,
        ISpeechRecognizer speechRecognizer,
        ISpeechSynthesizer speechSynthesizer,
        IVoiceMetrics metrics,
        IWakeWordDetector? wakeWordDetector = null,
        TimeProvider? timeProvider = null)
    {
        _agentRuntime = agentRuntime ?? throw new ArgumentNullException(nameof(agentRuntime));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _voiceActivityDetector = voiceActivityDetector ??
            throw new ArgumentNullException(nameof(voiceActivityDetector));
        _speechRecognizer = speechRecognizer ??
            throw new ArgumentNullException(nameof(speechRecognizer));
        _speechSynthesizer = speechSynthesizer ??
            throw new ArgumentNullException(nameof(speechSynthesizer));
        _metrics = metrics ?? throw new ArgumentNullException(nameof(metrics));
        _wakeWordDetector = wakeWordDetector ?? UnavailableWakeWordDetector.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;

        if (_capture.Format != _voiceActivityDetector.InputFormat ||
            _capture.Format != _speechRecognizer.InputFormat)
        {
            throw new ArgumentException("Capture, VAD, and recognition input formats must match.");
        }

        if (_playback.Format != _speechSynthesizer.OutputFormat)
        {
            throw new ArgumentException("Synthesis and playback output formats must match.");
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
        VoiceSessionConfiguration configuration,
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
            _configuration = configuration;
            _sessionCancellation = new CancellationTokenSource();

            try
            {
                await InitializeConversationUnsafeAsync(configuration, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                _sessionCancellation.Dispose();
                _sessionCancellation = null;
                _configuration = null;
                SetState(VoiceSessionState.Stopped);
                throw;
            }

            SetState(VoiceSessionState.Listening);
            if (configuration.SpeechInputEnabled &&
                configuration.ActivationMode == VoiceActivationMode.VoiceActivityDetection)
            {
                StartCapturePump(
                    useVoiceActivityDetection: true,
                    VoiceCaptureState.Conversation,
                    _sessionCancellation.Token);
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
            VoiceSessionConfiguration configuration = EnsureActiveConfiguration();
            if (!configuration.SpeechInputEnabled ||
                configuration.ActivationMode != VoiceActivationMode.PushToTalk)
            {
                throw new InvalidOperationException("The voice session is not in push-to-talk mode.");
            }

            if (_pushToTalkCaptureActive)
            {
                throw new InvalidOperationException("Push-to-talk capture is already active.");
            }

            await CancelGenerationUnsafeAsync(cancellationToken).ConfigureAwait(false);
            await _speechRecognizer.ResetAsync(cancellationToken).ConfigureAwait(false);
            _speechActive = true;
            _pushToTalkCaptureActive = true;
            SetState(VoiceSessionState.Listening);
            RefreshContinuationWindowUnsafe();
            StartCapturePump(
                useVoiceActivityDetection: false,
                VoiceCaptureState.PushToTalk,
                _sessionCancellation!.Token);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task EndPushToTalkAsync(CancellationToken cancellationToken)
    {
        Task? capturePump;
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            VoiceSessionConfiguration configuration = EnsureActiveConfiguration();
            if (configuration.ActivationMode != VoiceActivationMode.PushToTalk ||
                !_pushToTalkCaptureActive)
            {
                throw new InvalidOperationException("Push-to-talk capture is not active.");
            }

            capturePump = StopCapturePump();
            _pushToTalkCaptureActive = false;
            _speechActive = false;
            RefreshContinuationWindowUnsafe();
        }
        finally
        {
            _controlGate.Release();
        }

        await ObserveExpectedCancellationAsync(capturePump).ConfigureAwait(false);

        Stopwatch finalization = Stopwatch.StartNew();
        SpeechRecognitionResult result = await _speechRecognizer
            .CompleteUtteranceAsync(cancellationToken)
            .ConfigureAwait(false);
        _metrics.Record(new VoiceMetric(
            VoiceMetricKind.SpeechRecognitionFinalization,
            finalization.Elapsed.TotalMilliseconds));

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActiveConfiguration();
            await StartRecognizedTurnUnsafeAsync(result.Text).ConfigureAwait(false);
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task SubmitTextAsync(string text, CancellationToken cancellationToken)
    {
        ValidateInputText(text);

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureActiveConfiguration();
            if (_pushToTalkCaptureActive)
            {
                throw new InvalidOperationException(
                    "Finish the active push-to-talk capture before submitting text.");
            }

            await CancelGenerationUnsafeAsync(cancellationToken).ConfigureAwait(false);
            StartGenerationUnsafe(text.Trim());
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
            EnsureActiveConfiguration();
            bool interrupted = await CancelGenerationUnsafeAsync(cancellationToken).ConfigureAwait(false);
            if (!interrupted)
            {
                SetState(VoiceSessionState.Interrupted);
            }

            SetState(VoiceSessionState.Listening);
            RefreshContinuationWindowUnsafe();
        }
        finally
        {
            _controlGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? sessionCancellation;
        CancellationTokenSource? generationCancellation;
        Task? capturePump;
        Task? wakeTask;
        Task? idleTask;
        List<Task> generations;
        CancellationTokenSource? idleCancellation;

        // Wake-triggered initialization runs under the control gate. Cancel its session
        // before waiting so shutdown does not have to wait for a long model startup.
        try
        {
            Volatile.Read(ref _sessionCancellation)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_state == VoiceSessionState.Stopped)
            {
                return;
            }

            sessionCancellation = _sessionCancellation;
            generationCancellation = _generationCancellation;
            capturePump = StopCapturePump();
            wakeTask = _wakeTask;
            idleTask = _idleTask;
            idleCancellation = _idleCancellation;
            generations = [.. _retiredGenerationTasks];
            if (_generationTask is not null)
            {
                generations.Add(_generationTask);
            }

            _sessionCancellation = null;
            _generationCancellation = null;
            _generationTask = null;
            _retiredGenerationTasks.Clear();
            _configuration = null;
            _alwaysListeningConfiguration = null;
            _wakeTask = null;
            _idleTask = null;
            _idleCancellation = null;
            _lastWakeAcceptedAt = null;
            _pushToTalkCaptureActive = false;
            _speechActive = false;
            _preRollFrames.Clear();
            lock (_historySync)
            {
                _history.Clear();
            }

            checked
            {
                _generationId++;
            }

            sessionCancellation?.Cancel();
            generationCancellation?.Cancel();
            idleCancellation?.Cancel();
            SetCaptureState(VoiceCaptureState.Off);
            SetState(VoiceSessionState.Stopped);
        }
        finally
        {
            _controlGate.Release();
        }

        await ObserveExpectedCancellationAsync(capturePump).ConfigureAwait(false);
        await ObserveExpectedCancellationAsync(wakeTask).ConfigureAwait(false);
        await ObserveExpectedCancellationAsync(idleTask).ConfigureAwait(false);
        foreach (Task generation in generations)
        {
            await ObserveExpectedCancellationAsync(generation).ConfigureAwait(false);
        }

        await _playback.StopAsync(cancellationToken).ConfigureAwait(false);
        sessionCancellation?.Dispose();
        generationCancellation?.Dispose();
        idleCancellation?.Dispose();
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
        await _wakeWordDetector.DisposeAsync().ConfigureAwait(false);
        await _capture.DisposeAsync().ConfigureAwait(false);
        await _playback.DisposeAsync().ConfigureAwait(false);
        await _voiceActivityDetector.DisposeAsync().ConfigureAwait(false);
        await _speechRecognizer.DisposeAsync().ConfigureAwait(false);
        await _speechSynthesizer.DisposeAsync().ConfigureAwait(false);
        await _agentRuntime.DisposeAsync().ConfigureAwait(false);
    }

    private void StartCapturePump(
        bool useVoiceActivityDetection,
        VoiceCaptureState captureState,
        CancellationToken sessionToken)
    {
        if (_capturePump is { IsCompleted: false })
        {
            throw new InvalidOperationException("Microphone capture is already active.");
        }

        _captureCancellation?.Dispose();
        _captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        _capturePump = PumpCaptureAsync(
            useVoiceActivityDetection,
            _captureCancellation.Token);
        SetCaptureState(captureState);
    }

    private Task? StopCapturePump()
    {
        Task? capturePump = _capturePump;
        _capturePump = null;
        _captureCancellation?.Cancel();
        _captureCancellation?.Dispose();
        _captureCancellation = null;
        if (capturePump is not null)
        {
            SetCaptureState(VoiceCaptureState.Off);
        }

        return capturePump;
    }

    private async Task PumpCaptureAsync(
        bool useVoiceActivityDetection,
        CancellationToken cancellationToken)
    {
        try
        {
            await foreach (AudioFrame frame in
                _capture.CaptureAsync(cancellationToken).ConfigureAwait(false))
            {
                if (useVoiceActivityDetection)
                {
                    await ProcessVoiceActivityFrameAsync(frame, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    await ProcessRecognitionFrameAsync(frame, cancellationToken)
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
            _speechActive = false;
            SetCaptureState(VoiceCaptureState.Off);
            SetState(VoiceSessionState.Faulted);
            Publish(new VoiceSessionErrorNotification("audio_capture_failed", IsTransient: false));
        }
    }

    private async Task ProcessVoiceActivityFrameAsync(
        AudioFrame frame,
        CancellationToken cancellationToken)
    {
        if (!_speechActive)
        {
            _preRollFrames.Enqueue(frame);
            while (_preRollFrames.Count > MaximumPreRollFrames)
            {
                _preRollFrames.Dequeue();
            }
        }

        VoiceActivityChange activity = await _voiceActivityDetector
            .ProcessAsync(frame, cancellationToken)
            .ConfigureAwait(false);

        if (activity == VoiceActivityChange.SpeechStarted && !_speechActive)
        {
            Stopwatch bargeIn = Stopwatch.StartNew();
            await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _speechActive = true;
                RefreshContinuationWindowUnsafe();
                await CancelGenerationUnsafeAsync(cancellationToken).ConfigureAwait(false);
                await _speechRecognizer.ResetAsync(cancellationToken).ConfigureAwait(false);
                foreach (AudioFrame bufferedFrame in _preRollFrames)
                {
                    await ProcessRecognitionFrameAsync(bufferedFrame, cancellationToken)
                        .ConfigureAwait(false);
                }

                _preRollFrames.Clear();
                SetState(VoiceSessionState.Listening);
            }
            finally
            {
                _controlGate.Release();
            }

            _metrics.Record(new VoiceMetric(
                VoiceMetricKind.BargeInPlaybackStop,
                bargeIn.Elapsed.TotalMilliseconds));
            return;
        }

        if (_speechActive)
        {
            await ProcessRecognitionFrameAsync(frame, cancellationToken).ConfigureAwait(false);
        }

        if (activity == VoiceActivityChange.SpeechEnded && _speechActive)
        {
            _speechActive = false;
            Stopwatch finalization = Stopwatch.StartNew();
            SpeechRecognitionResult result = await _speechRecognizer
                .CompleteUtteranceAsync(cancellationToken)
                .ConfigureAwait(false);
            _metrics.Record(new VoiceMetric(
                VoiceMetricKind.SpeechRecognitionFinalization,
                finalization.Elapsed.TotalMilliseconds));

            await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_configuration is not null)
                {
                    await StartRecognizedTurnUnsafeAsync(result.Text).ConfigureAwait(false);
                }
            }
            finally
            {
                _controlGate.Release();
            }
        }
    }

    private async ValueTask ProcessRecognitionFrameAsync(
        AudioFrame frame,
        CancellationToken cancellationToken)
    {
        SpeechRecognitionUpdate? update = await _speechRecognizer
            .ProcessAudioAsync(frame, cancellationToken)
            .ConfigureAwait(false);
        if (update is not null && !string.IsNullOrWhiteSpace(update.Text))
        {
            Publish(new UserTranscriptNotification(update.Text, IsFinal: false));
        }
    }

    private ValueTask StartRecognizedTurnUnsafeAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SetState(VoiceSessionState.Listening);
            RefreshContinuationWindowUnsafe();
            return ValueTask.CompletedTask;
        }

        string normalized = text.Trim();
        Publish(new UserTranscriptNotification(normalized, IsFinal: true));
        StartGenerationUnsafe(normalized);
        return ValueTask.CompletedTask;
    }

    private void StartGenerationUnsafe(string userText)
    {
        VoiceSessionConfiguration configuration = EnsureActiveConfiguration();
        RefreshContinuationWindowUnsafe();
        AddHistory(new ConversationMessage(ConversationRole.User, userText));
        long generationId;
        checked
        {
            generationId = ++_generationId;
        }

        _generationCancellation?.Dispose();
        _generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _sessionCancellation!.Token);
        CancellationToken generationToken = _generationCancellation.Token;
        LanguageModelRequest request = new(
            CreateRequestMessages(configuration.Persona),
            configuration.MaximumOutputTokens);

        _retiredGenerationTasks.RemoveAll(static task => task.IsCompleted);
        _generationTask = RunGenerationAsync(
            generationId,
            Guid.NewGuid(),
            request,
            configuration.SpeechOutputEnabled,
            generationToken);
        SetState(VoiceSessionState.AwaitingResponse);
    }

    private async Task RunGenerationAsync(
        long generationId,
        Guid userRequestId,
        LanguageModelRequest request,
        bool speechOutputEnabled,
        CancellationToken cancellationToken)
    {
        Stopwatch endToEnd = Stopwatch.StartNew();
        Channel<string> speechSegments = Channel.CreateBounded<string>(
            new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
            });
        Task synthesis = speechOutputEnabled
            ? SynthesizeSegmentsAsync(generationId, speechSegments.Reader, cancellationToken)
            : Task.CompletedTask;
        VoiceSessionConfiguration configuration = EnsureActiveConfiguration();
        IncrementalResponseSegmenter segmenter = new(configuration.ResponseSegmentation);
        StringBuilder completedResponse = new();

        try
        {
            await foreach (LanguageModelToken token in
                _agentRuntime.GenerateAsync(request, userRequestId, cancellationToken)
                    .ConfigureAwait(false))
            {
                foreach (string segment in segmenter.Append(token.Text))
                {
                    await EmitSegmentAsync(segment).ConfigureAwait(false);
                }
            }

            foreach (string segment in segmenter.Complete())
            {
                await EmitSegmentAsync(segment).ConfigureAwait(false);
            }

            speechSegments.Writer.TryComplete();
            await synthesis.ConfigureAwait(false);

            if (completedResponse.Length > 0)
            {
                AddHistory(new ConversationMessage(
                    ConversationRole.Assistant,
                    completedResponse.ToString()));
            }

            if (await ReturnToListeningAsync(generationId, cancellationToken).ConfigureAwait(false))
            {
                _metrics.Record(new VoiceMetric(
                    VoiceMetricKind.EndToEndTurn,
                    endToEnd.Elapsed.TotalMilliseconds));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            speechSegments.Writer.TryComplete();
            await ObserveExpectedCancellationAsync(synthesis).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            speechSegments.Writer.TryComplete();
            await ObserveFaultedTaskAsync(synthesis).ConfigureAwait(false);
            if (IsCurrentGeneration(generationId))
            {
                string code = exception is LocalComponentUnavailableException unavailable
                    ? unavailable.Code
                    : "voice_pipeline_failed";
                SetState(VoiceSessionState.Faulted);
                Publish(new VoiceSessionErrorNotification(code, IsTransient: false));
            }
        }

        async ValueTask EmitSegmentAsync(string segment)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentGeneration(generationId))
            {
                throw new OperationCanceledException(cancellationToken);
            }

            AppendBoundedHistory(segment);
            Publish(new AssistantTranscriptNotification(segment));
            if (speechOutputEnabled)
            {
                await speechSegments.Writer
                    .WriteAsync(segment, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        void AppendBoundedHistory(string segment)
        {
            if (completedResponse.Length >= MaximumHistoryCharacters)
            {
                return;
            }

            if (completedResponse.Length > 0)
            {
                completedResponse.Append(' ');
            }

            int available = MaximumHistoryCharacters - completedResponse.Length;
            completedResponse.Append(segment.AsSpan(0, Math.Min(segment.Length, available)));
        }
    }

    private async ValueTask<bool> ReturnToListeningAsync(
        long generationId,
        CancellationToken cancellationToken)
    {
        await _controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsCurrentGeneration(generationId) || cancellationToken.IsCancellationRequested)
            {
                return false;
            }

            RefreshContinuationWindowUnsafe();
            SetState(VoiceSessionState.Listening);
            return true;
        }
        finally
        {
            _controlGate.Release();
        }
    }

    private async Task SynthesizeSegmentsAsync(
        long generationId,
        ChannelReader<string> segments,
        CancellationToken cancellationToken)
    {
        await foreach (string segment in segments.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            SpeechSynthesisRequest request = new(segment, generationId);
            await foreach (SynthesizedAudioChunk audio in
                _speechSynthesizer.SynthesizeAsync(request, cancellationToken)
                    .ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentGeneration(generationId))
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                await _playback
                    .EnqueueAsync(
                        new AssistantAudioChunk(audio.Data, generationId),
                        cancellationToken)
                    .ConfigureAwait(false);
                SetState(VoiceSessionState.Speaking);
            }
        }
    }

    private async ValueTask<bool> CancelGenerationUnsafeAsync(
        CancellationToken cancellationToken)
    {
        bool active = _generationTask is { IsCompleted: false } ||
            _state is VoiceSessionState.AwaitingResponse or VoiceSessionState.Speaking;
        checked
        {
            _generationId++;
        }

        if (_generationTask is not null)
        {
            _retiredGenerationTasks.Add(_generationTask);
            _generationTask = null;
        }

        _generationCancellation?.Cancel();
        _generationCancellation?.Dispose();
        _generationCancellation = null;
        await _playback
            .InterruptAsync(_generationId, cancellationToken)
            .ConfigureAwait(false);
        if (active)
        {
            SetState(VoiceSessionState.Interrupted);
        }

        return active;
    }

    private List<ConversationMessage> CreateRequestMessages(string persona)
    {
        lock (_historySync)
        {
            List<ConversationMessage> messages =
                [new ConversationMessage(ConversationRole.System, persona), .. _history];
            while (messages.Count > VoiceDataLimits.MaximumConversationMessages)
            {
                messages.RemoveAt(1);
            }

            return messages;
        }
    }

    private void AddHistory(ConversationMessage message)
    {
        lock (_historySync)
        {
            _history.Add(message);
            int characters = _history.Sum(static item => item.Text.Length);
            while (_history.Count > MaximumHistoryMessages ||
                characters > MaximumHistoryCharacters)
            {
                characters -= _history[0].Text.Length;
                _history.RemoveAt(0);
            }
        }
    }

    private bool IsCurrentGeneration(long generationId) =>
        generationId == Volatile.Read(ref _generationId) &&
        _state != VoiceSessionState.Stopped;

    private VoiceSessionConfiguration EnsureActiveConfiguration() =>
        _state is VoiceSessionState.Stopped or VoiceSessionState.Sleeping
            ? throw new InvalidOperationException("The voice session is not active.")
            : _configuration ?? throw new InvalidOperationException("The voice session is not active.");

    private void SetState(VoiceSessionState state)
    {
        if (_state == VoiceSessionState.Stopped &&
            state is not VoiceSessionState.Activating and not VoiceSessionState.Sleeping)
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

    private void SetCaptureState(VoiceCaptureState state)
    {
        if (_captureState == state)
        {
            return;
        }

        _captureState = state;
        Publish(new VoiceCaptureStateChangedNotification(state));
    }

    private static void ValidateInputText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("Text input cannot be empty.", nameof(text));
        }

        if (text.Length > VoiceDataLimits.MaximumTextCharacters)
        {
            throw new ArgumentException("Text input exceeds the size limit.", nameof(text));
        }
    }

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

    private static async Task ObserveFaultedTaskAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The generation error path reports only a stable, content-free error code.
        }
    }
}
