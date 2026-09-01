using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Jarvis.Core.Voice;
using Microsoft.Extensions.Time.Testing;

namespace Jarvis.Core.Tests.Voice;

public sealed class WakeWordLifecycleTests
{
    [Fact]
    public async Task WakeDetectionTransitionsFromSleepingWithoutEagerConversationInitialization()
    {
        WakeRig rig = new();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        ConcurrentQueue<VoiceSessionNotification> notifications = new();
        using CancellationTokenSource notificationCancellation = new();
        Task observer = ObserveNotificationsAsync(
            coordinator,
            notifications,
            notificationCancellation.Token);

        await coordinator.ArmWakeWordAsync(Configuration(), CancellationToken.None);

        Assert.Equal(VoiceSessionState.Sleeping, coordinator.State);
        Assert.Equal(0, rig.LanguageModel.InitializeCount);
        Assert.Equal(0, rig.Recognizer.InitializeCount);
        rig.WakeWord.Emit(TimeSpan.FromMilliseconds(7));
        await WaitUntilAsync(() => coordinator.State == VoiceSessionState.Listening);

        Assert.Equal(1, rig.LanguageModel.InitializeCount);
        Assert.Equal(1, rig.Recognizer.InitializeCount);
        Assert.Contains(
            rig.Metrics.Recorded,
            static metric => metric.Kind == VoiceMetricKind.KeywordDetectionLatency &&
                metric.Value >= 7);
        Assert.Contains(
            rig.Metrics.Recorded,
            static metric => metric.Kind == VoiceMetricKind.WakeToListeningLatency);
        await WaitUntilAsync(() => notifications.OfType<VoiceCaptureStateChangedNotification>()
            .Any(static state => state.State == VoiceCaptureState.Conversation));
        Assert.Contains(
            notifications,
            static notification => notification is VoiceCaptureStateChangedNotification
            {
                State: VoiceCaptureState.WakeWord,
            });
        VoiceSessionState[] states = notifications
            .OfType<VoiceSessionStateChangedNotification>()
            .Select(static notification => notification.State)
            .ToArray();
        Assert.Contains(VoiceSessionState.Sleeping, states);
        Assert.Contains(VoiceSessionState.Activating, states);
        Assert.Contains(VoiceSessionState.Listening, states);
        Assert.Contains(
            notifications,
            static notification => notification is WakeWordDetectedNotification
            {
                Phrase: "Jarvis",
            });
        notificationCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer);
    }

    [Fact]
    public async Task RepeatedWakeInsideCooldownIsSuppressedAsFalseDuplicate()
    {
        WakeRig rig = new();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        VoiceSessionConfiguration configuration = Configuration(
            cooldown: TimeSpan.FromSeconds(1),
            continuation: TimeSpan.FromMilliseconds(250));
        await coordinator.ArmWakeWordAsync(configuration, CancellationToken.None);

        rig.WakeWord.Emit();
        await WaitUntilAsync(() => coordinator.State == VoiceSessionState.Listening);
        rig.TimeProvider.Advance(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => coordinator.State == VoiceSessionState.Sleeping);
        rig.WakeWord.Emit();
        await WaitUntilAsync(() => rig.Metrics.Recorded.Any(static metric =>
            metric.Kind == VoiceMetricKind.FalseActivationCount && metric.Value == 1));

        Assert.Equal(VoiceSessionState.Sleeping, coordinator.State);
        Assert.Equal(1, rig.LanguageModel.InitializeCount);
        Assert.Contains(
            rig.Metrics.Recorded,
            static metric => metric.Kind == VoiceMetricKind.FalseActivationCount &&
                metric.Value == 1);

        rig.TimeProvider.Advance(TimeSpan.FromSeconds(1));
        rig.WakeWord.Emit();
        await WaitUntilAsync(() => coordinator.State == VoiceSessionState.Listening);
        Assert.Equal(2, rig.LanguageModel.InitializeCount);
    }

    [Fact]
    public async Task CompletedTurnRefreshesContinuationWindowWithoutSecondWake()
    {
        WakeRig rig = new();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        VoiceSessionConfiguration configuration = Configuration(
            continuation: TimeSpan.FromMilliseconds(500));
        await coordinator.ArmWakeWordAsync(configuration, CancellationToken.None);
        rig.WakeWord.Emit();
        await WaitUntilAsync(() => coordinator.State == VoiceSessionState.Listening);

        rig.TimeProvider.Advance(TimeSpan.FromMilliseconds(300));
        rig.LanguageModel.QueueResponse("Follow-up completed.");
        await coordinator.SubmitTextAsync("And explain the authentication code.", CancellationToken.None);
        await WaitUntilAsync(() => rig.LanguageModel.RequestCount == 1);
        await WaitUntilAsync(() => coordinator.State == VoiceSessionState.Listening);
        rig.TimeProvider.Advance(TimeSpan.FromMilliseconds(300));

        Assert.Equal(VoiceSessionState.Listening, coordinator.State);
        Assert.Equal(1, rig.WakeWord.DetectionCount);
        rig.TimeProvider.Advance(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => coordinator.State == VoiceSessionState.Sleeping);
    }

    [Fact]
    public async Task IdleTimeoutStopsConversationCaptureAndReturnsToSleeping()
    {
        WakeRig rig = new();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        await coordinator.ArmWakeWordAsync(
            Configuration(continuation: TimeSpan.FromMilliseconds(250)),
            CancellationToken.None);
        rig.WakeWord.Emit();
        await WaitUntilAsync(() => coordinator.State == VoiceSessionState.Listening);
        rig.TimeProvider.Advance(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => coordinator.State == VoiceSessionState.Sleeping);
        await WaitUntilAsync(() =>
            rig.Capture.StopCount >= 1 && rig.WakeWord.ListenerStartCount >= 2);

        Assert.True(rig.Capture.StopCount >= 1);
        Assert.True(rig.WakeWord.ListenerStartCount >= 2);
    }

    [Fact]
    public async Task StopCancelsDormantListenerAndLeavesNoActiveCaptureState()
    {
        WakeRig rig = new();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        await coordinator.ArmWakeWordAsync(Configuration(), CancellationToken.None);
        await WaitUntilAsync(() => rig.WakeWord.ListenerStartCount == 1);

        await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(VoiceSessionState.Stopped, coordinator.State);
        Assert.Equal(1, rig.WakeWord.ListenerStopCount);
        Assert.Equal(0, rig.LanguageModel.InitializeCount);
    }

    [Fact]
    public async Task StopCancelsWakeTriggeredInitializationWithoutWaitingForModelTimeout()
    {
        WakeRig rig = new();
        rig.LanguageModel.BlockInitialization = true;
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        await coordinator.ArmWakeWordAsync(Configuration(), CancellationToken.None);
        rig.WakeWord.Emit();
        await rig.LanguageModel.InitializationStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(VoiceSessionState.Stopped, coordinator.State);
        Assert.True(rig.LanguageModel.InitializationCancelled.Task.IsCompleted);
    }

    [Fact]
    public async Task PushToTalkCoexistsWithWakeActivationWithoutStartingVadCapture()
    {
        WakeRig rig = new();
        rig.Recognizer.FinalResults.Enqueue(string.Empty);
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        VoiceSessionConfiguration configuration = Configuration(
            activationMode: VoiceActivationMode.PushToTalk,
            continuation: TimeSpan.FromSeconds(1));
        await coordinator.ArmWakeWordAsync(configuration, CancellationToken.None);
        rig.WakeWord.Emit();
        await WaitUntilAsync(() => coordinator.State == VoiceSessionState.Listening);

        Assert.Equal(0, rig.Capture.StartCount);
        await coordinator.BeginPushToTalkAsync(CancellationToken.None);
        await WaitUntilAsync(() => rig.Capture.StartCount == 1);
        rig.Capture.Emit([0, 0]);
        await WaitUntilAsync(() => rig.Recognizer.ProcessCount == 1);
        await coordinator.EndPushToTalkAsync(CancellationToken.None);

        Assert.Equal(VoiceSessionState.Listening, coordinator.State);
        Assert.Equal(1, rig.Capture.StopCount);
    }

    private static VoiceSessionConfiguration Configuration(
        VoiceActivationMode activationMode = VoiceActivationMode.VoiceActivityDetection,
        TimeSpan? cooldown = null,
        TimeSpan? continuation = null) =>
        new(
            activationMode,
            "Wake lifecycle test persona /no_think",
            speechInputEnabled: true,
            speechOutputEnabled: false,
            wakeWord: new WakeWordSessionConfiguration(
                alwaysListeningEnabled: true,
                cooldown: cooldown ?? TimeSpan.FromMilliseconds(100),
                continuationWindow: continuation ?? TimeSpan.FromSeconds(2),
                acknowledgement: string.Empty));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static async Task ObserveNotificationsAsync(
        RealtimeVoiceCoordinator coordinator,
        ConcurrentQueue<VoiceSessionNotification> notifications,
        CancellationToken cancellationToken)
    {
        await foreach (VoiceSessionNotification notification in
            coordinator.ReadNotificationsAsync(cancellationToken))
        {
            notifications.Enqueue(notification);
        }
    }

    private sealed class WakeRig
    {
        public FakeLanguageModel LanguageModel { get; } = new();

        public FakeCapture Capture { get; } = new();

        public FakePlayback Playback { get; } = new();

        public FakeVad Vad { get; } = new();

        public FakeRecognizer Recognizer { get; } = new();

        public FakeSynthesizer Synthesizer { get; } = new();

        public FakeMetrics Metrics { get; } = new();

        public FakeWakeWordDetector WakeWord { get; } = new();

        public FakeTimeProvider TimeProvider { get; } = new();

        public RealtimeVoiceCoordinator CreateCoordinator() =>
            new(
                LanguageModel,
                Capture,
                Playback,
                Vad,
                Recognizer,
                Synthesizer,
                Metrics,
                WakeWord,
                TimeProvider);
    }

    private sealed class FakeWakeWordDetector : IWakeWordDetector
    {
        private readonly Channel<WakeWordDetection> _detections =
            Channel.CreateUnbounded<WakeWordDetection>();
        private int _detectionCount;
        private int _listenerStartCount;
        private int _listenerStopCount;

        public bool IsAvailable => true;

        public int DetectionCount => Volatile.Read(ref _detectionCount);

        public int ListenerStartCount => Volatile.Read(ref _listenerStartCount);

        public int ListenerStopCount => Volatile.Read(ref _listenerStopCount);

        public void Emit(TimeSpan? processingLatency = null) =>
            _detections.Writer.TryWrite(new WakeWordDetection(
                DateTimeOffset.UtcNow,
                processingLatency ?? TimeSpan.FromMilliseconds(1)));

        public async IAsyncEnumerable<WakeWordDetection> ListenAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _listenerStartCount);
            try
            {
                await foreach (WakeWordDetection detection in
                    _detections.Reader.ReadAllAsync(cancellationToken))
                {
                    Interlocked.Increment(ref _detectionCount);
                    yield return detection;
                }
            }
            finally
            {
                Interlocked.Increment(ref _listenerStopCount);
            }
        }

        public ValueTask DisposeAsync()
        {
            _detections.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeLanguageModel : ILanguageModel
    {
        private readonly ConcurrentQueue<string> _responses = new();
        private int _initializeCount;
        private int _requestCount;

        public int InitializeCount => Volatile.Read(ref _initializeCount);

        public int RequestCount => Volatile.Read(ref _requestCount);

        public bool BlockInitialization { get; set; }

        public TaskCompletionSource InitializationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource InitializationCancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void QueueResponse(string response) => _responses.Enqueue(response);

        public async ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _initializeCount);
            InitializationStarted.TrySetResult();
            if (!BlockInitialization)
            {
                return;
            }

            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                InitializationCancelled.TrySetResult();
                throw;
            }
        }

        public async IAsyncEnumerable<LanguageModelToken> GenerateAsync(
            LanguageModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requestCount);
            if (!_responses.TryDequeue(out string? response))
            {
                throw new InvalidOperationException("No fake response was queued.");
            }

            await Task.Yield();
            yield return new LanguageModelToken(response);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCapture : IAudioCapture
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>();
        private int _startCount;
        private int _stopCount;

        public AudioFormat Format => AudioFormat.Pcm16Mono16Khz;

        public int StartCount => Volatile.Read(ref _startCount);

        public int StopCount => Volatile.Read(ref _stopCount);

        public void Emit(byte[] data) => _frames.Writer.TryWrite(new AudioFrame(data));

        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _startCount);
            try
            {
                await foreach (AudioFrame frame in _frames.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return frame;
                }
            }
            finally
            {
                Interlocked.Increment(ref _stopCount);
            }
        }

        public ValueTask DisposeAsync()
        {
            _frames.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakePlayback : IAudioPlayback
    {
        public AudioFormat Format => AudioFormat.Pcm16Mono24Khz;

        public ValueTask EnqueueAsync(
            AssistantAudioChunk chunk,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask InterruptAsync(
            long invalidThroughGenerationId,
            CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeVad : IVoiceActivityDetector
    {
        public AudioFormat InputFormat => AudioFormat.Pcm16Mono16Khz;

        public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<VoiceActivityChange> ProcessAsync(
            AudioFrame frame,
            CancellationToken cancellationToken) => ValueTask.FromResult(VoiceActivityChange.None);

        public ValueTask ResetAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRecognizer : ISpeechRecognizer
    {
        private int _initializeCount;
        private int _processCount;

        public AudioFormat InputFormat => AudioFormat.Pcm16Mono16Khz;

        public ConcurrentQueue<string> FinalResults { get; } = new();

        public int InitializeCount => Volatile.Read(ref _initializeCount);

        public int ProcessCount => Volatile.Read(ref _processCount);

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _initializeCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask<SpeechRecognitionUpdate?> ProcessAudioAsync(
            AudioFrame frame,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _processCount);
            return ValueTask.FromResult<SpeechRecognitionUpdate?>(null);
        }

        public ValueTask<SpeechRecognitionResult> CompleteUtteranceAsync(
            CancellationToken cancellationToken) => ValueTask.FromResult(
                new SpeechRecognitionResult(
                    FinalResults.TryDequeue(out string? text) ? text : string.Empty));

        public ValueTask ResetAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSynthesizer : ISpeechSynthesizer
    {
        public AudioFormat OutputFormat => AudioFormat.Pcm16Mono24Khz;

        public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<SynthesizedAudioChunk> SynthesizeAsync(
            SpeechSynthesisRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _ = request;
            await Task.CompletedTask;
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMetrics : IVoiceMetrics
    {
        public ConcurrentQueue<VoiceMetric> Recorded { get; } = new();

        public void Record(VoiceMetric metric) => Recorded.Enqueue(metric);
    }
}
