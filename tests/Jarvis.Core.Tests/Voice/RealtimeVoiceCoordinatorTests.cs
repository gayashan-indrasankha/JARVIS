using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Jarvis.Core.Voice;

namespace Jarvis.Core.Tests.Voice;

public sealed class RealtimeVoiceCoordinatorTests
{
    [Fact]
    public async Task TextTurnStreamsSanitizedSegmentsToSpeechInOrder()
    {
        TestRig rig = new();
        Channel<string> response = rig.LanguageModel.QueueResponse();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        await coordinator.StartAsync(TextConfiguration(speechOutput: true), CancellationToken.None);

        await coordinator.SubmitTextAsync("Give a short status.", CancellationToken.None);
        await response.Writer.WriteAsync("The first response segment is ready.");
        await response.Writer.WriteAsync(" The second response segment follows now.");
        response.Writer.TryComplete();

        _ = await rig.Playback.Enqueued.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        _ = await rig.Playback.Enqueued.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(coordinator, VoiceSessionState.Listening);

        Assert.Equal(
            [
                "The first response segment is ready.",
                "The second response segment follows now.",
            ],
            rig.Synthesizer.Requests.Select(static request => request.Text));
        Assert.Equal(1, rig.LanguageModel.RequestCount);
    }

    [Fact]
    public async Task VadBargeInCancelsOldGenerationAndLateOutputNeverResumes()
    {
        TestRig rig = new();
        rig.LanguageModel.IgnoreCancellation = true;
        Channel<string> firstResponse = rig.LanguageModel.QueueResponse();
        Channel<string> secondResponse = rig.LanguageModel.QueueResponse();
        rig.Recognizer.FinalResults.Enqueue("new spoken question");
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        await coordinator.StartAsync(VoiceConfiguration(), CancellationToken.None);
        await rig.Capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.SubmitTextAsync("old question", CancellationToken.None);
        await firstResponse.Writer.WriteAsync("The old response has started speaking.");
        AssistantAudioChunk oldAudio = await rig.Playback.Enqueued.Reader
            .ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        rig.Vad.Changes.Enqueue(VoiceActivityChange.SpeechStarted);
        rig.Capture.Emit([0, 0, 1, 0]);
        await rig.LanguageModel.FirstCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await firstResponse.Writer.WriteAsync(" This stale sentence must never be synthesized.");
        firstResponse.Writer.TryComplete();
        rig.Vad.Changes.Enqueue(VoiceActivityChange.SpeechEnded);
        rig.Capture.Emit([0, 0, 1, 0]);

        await secondResponse.Writer.WriteAsync("The replacement response is current and valid.");
        secondResponse.Writer.TryComplete();
        AssistantAudioChunk newAudio = await rig.Playback.Enqueued.Reader
            .ReadAsync()
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForStateAsync(coordinator, VoiceSessionState.Listening);

        Assert.NotEqual(oldAudio.GenerationId, newAudio.GenerationId);
        Assert.True(rig.Playback.InvalidThroughGenerationId >= oldAudio.GenerationId);
        Assert.DoesNotContain(
            rig.Synthesizer.Requests,
            static request => request.Text.Contains("stale", StringComparison.OrdinalIgnoreCase));
        Assert.True(rig.Playback.InterruptCount >= 2);
        Assert.Contains(
            rig.Metrics.Recorded,
            static metric => metric.Kind == VoiceMetricKind.BargeInPlaybackStop);
    }

    [Fact]
    public async Task LateSynthesizerCallbackAfterInterruptCannotReachPlayback()
    {
        TestRig rig = new();
        rig.Synthesizer.EmitLateChunkAfterRelease = true;
        Channel<string> response = rig.LanguageModel.QueueResponse();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        await coordinator.StartAsync(TextConfiguration(speechOutput: true), CancellationToken.None);
        await coordinator.SubmitTextAsync("long synthesized answer", CancellationToken.None);
        response.Writer.TryWrite("This response begins speaking before interruption.");
        response.Writer.TryComplete();
        _ = await rig.Playback.Enqueued.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.InterruptAsync(CancellationToken.None);
        rig.Synthesizer.ReleaseLateChunk.TrySetResult();
        await rig.Synthesizer.LateChunkProduced.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(TimeSpan.FromMilliseconds(50));

        Assert.False(rig.Playback.Enqueued.Reader.TryRead(out _));
        Assert.Equal(VoiceSessionState.Listening, coordinator.State);
    }

    [Fact]
    public async Task PushToTalkCapturesOnlyBetweenBeginAndEndAndSubmitsFinalText()
    {
        TestRig rig = new();
        Channel<string> response = rig.LanguageModel.QueueResponse();
        rig.Recognizer.FinalResults.Enqueue("push to talk question");
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        VoiceSessionConfiguration configuration = new(
            VoiceActivationMode.PushToTalk,
            "Test persona /no_think",
            speechInputEnabled: true,
            speechOutputEnabled: false);

        await coordinator.StartAsync(configuration, CancellationToken.None);
        Assert.False(rig.Capture.Started.Task.IsCompleted);

        await coordinator.BeginPushToTalkAsync(CancellationToken.None);
        await rig.Capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        rig.Capture.Emit([0, 0, 1, 0]);
        await rig.Recognizer.AudioProcessed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.EndPushToTalkAsync(CancellationToken.None);
        response.Writer.TryWrite("The push to talk response completed correctly.");
        response.Writer.TryComplete();
        await WaitForStateAsync(coordinator, VoiceSessionState.Listening);

        Assert.Equal("push to talk question", rig.LanguageModel.LastUserMessage);
        Assert.True(rig.Capture.Stopped.Task.IsCompleted);
    }

    [Fact]
    public async Task ExplicitInterruptCancelsGenerationClearsPlaybackAndReturnsToListening()
    {
        TestRig rig = new();
        Channel<string> response = rig.LanguageModel.QueueResponse();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        await coordinator.StartAsync(TextConfiguration(speechOutput: true), CancellationToken.None);
        await coordinator.SubmitTextAsync("long answer", CancellationToken.None);
        await response.Writer.WriteAsync("This answer is long enough to begin speaking.");
        _ = await rig.Playback.Enqueued.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.InterruptAsync(CancellationToken.None);

        await rig.LanguageModel.FirstCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(VoiceSessionState.Listening, coordinator.State);
        Assert.True(rig.Playback.InterruptCount >= 2);
    }

    [Fact]
    public async Task StopCancelsCaptureAndGenerationAndReleasesPlayback()
    {
        TestRig rig = new();
        _ = rig.LanguageModel.QueueResponse();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        await coordinator.StartAsync(VoiceConfiguration(), CancellationToken.None);
        await rig.Capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.SubmitTextAsync("blocked response", CancellationToken.None);

        await coordinator.StopAsync(CancellationToken.None);

        Assert.Equal(VoiceSessionState.Stopped, coordinator.State);
        Assert.True(rig.Capture.Stopped.Task.IsCompleted);
        Assert.Equal(1, rig.Playback.StopCount);
        Assert.True(rig.LanguageModel.FirstCancellation.Task.IsCompleted);
    }

    [Fact]
    public async Task RestartClearsConversationHistoryAndResetsSpeechState()
    {
        TestRig rig = new();
        Channel<string> firstResponse = rig.LanguageModel.QueueResponse();
        Channel<string> secondResponse = rig.LanguageModel.QueueResponse();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        VoiceSessionConfiguration configuration = new(
            VoiceActivationMode.VoiceActivityDetection,
            "Test persona /no_think",
            speechInputEnabled: true,
            speechOutputEnabled: false);

        await coordinator.StartAsync(configuration, CancellationToken.None);
        await coordinator.SubmitTextAsync("first private turn", CancellationToken.None);
        firstResponse.Writer.TryWrite("First response completes here.");
        firstResponse.Writer.TryComplete();
        await WaitForStateAsync(coordinator, VoiceSessionState.Listening);
        await coordinator.StopAsync(CancellationToken.None);

        await coordinator.StartAsync(configuration, CancellationToken.None);
        await coordinator.SubmitTextAsync("fresh second turn", CancellationToken.None);
        secondResponse.Writer.TryWrite("Second response completes here.");
        secondResponse.Writer.TryComplete();
        await WaitForStateAsync(coordinator, VoiceSessionState.Listening);

        LanguageModelRequest latest = rig.LanguageModel.Requests.Last();
        Assert.DoesNotContain(
            latest.Messages,
            static message => message.Text.Contains("first private", StringComparison.Ordinal));
        Assert.Contains(
            latest.Messages,
            static message => message.Text.Contains("fresh second", StringComparison.Ordinal));
        Assert.Equal(2, rig.Vad.ResetCount);
        Assert.Equal(2, rig.Recognizer.ResetCount);
    }

    [Fact]
    public async Task LocalModelDisconnectFaultsOnlyTheSessionAndAllowsCleanRestart()
    {
        TestRig rig = new();
        Channel<string> failedResponse = rig.LanguageModel.QueueResponse();
        Channel<string> recoveredResponse = rig.LanguageModel.QueueResponse();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        VoiceSessionConfiguration configuration = TextConfiguration(speechOutput: false);

        await coordinator.StartAsync(configuration, CancellationToken.None);
        await coordinator.SubmitTextAsync("turn during disconnect", CancellationToken.None);
        failedResponse.Writer.TryComplete(new IOException("simulated loopback disconnect"));
        await WaitForStateAsync(coordinator, VoiceSessionState.Faulted);

        await coordinator.StopAsync(CancellationToken.None);
        await coordinator.StartAsync(configuration, CancellationToken.None);
        await coordinator.SubmitTextAsync("turn after restart", CancellationToken.None);
        recoveredResponse.Writer.TryWrite("The restarted local session responds normally.");
        recoveredResponse.Writer.TryComplete();
        await WaitForStateAsync(coordinator, VoiceSessionState.Listening);

        Assert.Equal(VoiceSessionState.Listening, coordinator.State);
        Assert.Equal(2, rig.LanguageModel.RequestCount);
    }

    [Fact]
    public async Task SpeechSynthesisFailureFaultsSessionAndStillAllowsCleanStop()
    {
        TestRig rig = new();
        Channel<string> response = rig.LanguageModel.QueueResponse();
        rig.Synthesizer.Failure = new InvalidOperationException("private synthesis detail");
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        await coordinator.StartAsync(TextConfiguration(speechOutput: true), CancellationToken.None);

        await coordinator.SubmitTextAsync("trigger synthesis", CancellationToken.None);
        response.Writer.TryWrite("This response is long enough to reach speech synthesis.");
        response.Writer.TryComplete();
        await WaitForStateAsync(coordinator, VoiceSessionState.Faulted);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        VoiceSessionErrorNotification? error = null;
        await foreach (VoiceSessionNotification notification in
            coordinator.ReadNotificationsAsync(timeout.Token))
        {
            if (notification is VoiceSessionErrorNotification candidate)
            {
                error = candidate;
                break;
            }
        }

        Assert.Equal("voice_pipeline_failed", error?.Code);
        Assert.False(error?.IsTransient);
        await coordinator.StopAsync(CancellationToken.None);
        Assert.Equal(VoiceSessionState.Stopped, coordinator.State);
    }

    [Fact]
    public async Task SegmentationExpansionCannotOverflowConversationHistory()
    {
        TestRig rig = new();
        Channel<string> firstResponse = rig.LanguageModel.QueueResponse();
        Channel<string> secondResponse = rig.LanguageModel.QueueResponse();
        await using RealtimeVoiceCoordinator coordinator = rig.CreateCoordinator();
        await coordinator.StartAsync(TextConfiguration(speechOutput: false), CancellationToken.None);

        await coordinator.SubmitTextAsync("first turn", CancellationToken.None);
        string sentence = "This sentence is long enough.";
        string response = string.Concat(Enumerable.Repeat(
            sentence,
            VoiceDataLimits.MaximumTextCharacters / sentence.Length));
        firstResponse.Writer.TryWrite(response);
        firstResponse.Writer.TryComplete();
        await WaitForStateAsync(coordinator, VoiceSessionState.Listening);

        await coordinator.SubmitTextAsync("second turn", CancellationToken.None);
        secondResponse.Writer.TryWrite("The second turn completes normally.");
        secondResponse.Writer.TryComplete();
        await WaitForStateAsync(coordinator, VoiceSessionState.Listening);

        Assert.Equal(VoiceSessionState.Listening, coordinator.State);
        Assert.Equal(2, rig.LanguageModel.RequestCount);
    }

    private static VoiceSessionConfiguration TextConfiguration(bool speechOutput) =>
        new(
            VoiceActivationMode.VoiceActivityDetection,
            "Test persona /no_think",
            speechInputEnabled: false,
            speechOutputEnabled: speechOutput);

    private static VoiceSessionConfiguration VoiceConfiguration() =>
        new(
            VoiceActivationMode.VoiceActivityDetection,
            "Test persona /no_think",
            speechInputEnabled: true,
            speechOutputEnabled: true);

    private static async Task WaitForStateAsync(
        RealtimeVoiceCoordinator coordinator,
        VoiceSessionState expected)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        while (coordinator.State != expected)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private sealed class TestRig
    {
        public FakeLanguageModel LanguageModel { get; } = new();

        public FakeCapture Capture { get; } = new();

        public FakePlayback Playback { get; } = new();

        public FakeVad Vad { get; } = new();

        public FakeRecognizer Recognizer { get; } = new();

        public FakeSynthesizer Synthesizer { get; } = new();

        public FakeMetrics Metrics { get; } = new();

        public RealtimeVoiceCoordinator CreateCoordinator() =>
            new(LanguageModel, Capture, Playback, Vad, Recognizer, Synthesizer, Metrics);
    }

    private sealed class FakeLanguageModel : ILanguageModel
    {
        private readonly ConcurrentQueue<Channel<string>> _responses = new();
        private int _requestCount;

        public int RequestCount => Volatile.Read(ref _requestCount);

        public string? LastUserMessage { get; private set; }

        public TaskCompletionSource FirstCancellation { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IgnoreCancellation { get; set; }

        public ConcurrentQueue<LanguageModelRequest> Requests { get; } = new();

        public Channel<string> QueueResponse()
        {
            Channel<string> response = Channel.CreateUnbounded<string>();
            _responses.Enqueue(response);
            return response;
        }

        public ValueTask InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<LanguageModelToken> GenerateAsync(
            LanguageModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            Requests.Enqueue(request);
            LastUserMessage = request.Messages.Last(
                static message => message.Role == ConversationRole.User).Text;
            if (!_responses.TryDequeue(out Channel<string>? response))
            {
                throw new InvalidOperationException("No fake response was queued.");
            }

            try
            {
                using CancellationTokenRegistration registration = cancellationToken.Register(
                    () => FirstCancellation.TrySetResult());
                CancellationToken enumerationToken = IgnoreCancellation
                    ? CancellationToken.None
                    : cancellationToken;
                await foreach (string token in response.Reader.ReadAllAsync(enumerationToken))
                {
                    yield return new LanguageModelToken(token);
                }
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    FirstCancellation.TrySetResult();
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCapture : IAudioCapture
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>();

        public AudioFormat Format => AudioFormat.Pcm16Mono16Khz;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(byte[] data) =>
            Assert.True(_frames.Writer.TryWrite(new AudioFrame(data)));

        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await foreach (AudioFrame frame in _frames.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return frame;
                }
            }
            finally
            {
                Stopped.TrySetResult();
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
        private int _interruptCount;
        private int _stopCount;
        private long _invalidThroughGenerationId;

        public AudioFormat Format => AudioFormat.Pcm16Mono24Khz;

        public Channel<AssistantAudioChunk> Enqueued { get; } =
            Channel.CreateUnbounded<AssistantAudioChunk>();

        public int InterruptCount => Volatile.Read(ref _interruptCount);

        public int StopCount => Volatile.Read(ref _stopCount);

        public long InvalidThroughGenerationId =>
            Volatile.Read(ref _invalidThroughGenerationId);

        public ValueTask EnqueueAsync(
            AssistantAudioChunk chunk,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.GenerationId <= InvalidThroughGenerationId)
            {
                return ValueTask.CompletedTask;
            }

            Assert.True(Enqueued.Writer.TryWrite(chunk));
            return ValueTask.CompletedTask;
        }

        public ValueTask InterruptAsync(
            long invalidThroughGenerationId,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(invalidThroughGenerationId);
            cancellationToken.ThrowIfCancellationRequested();
            long observed;
            do
            {
                observed = Volatile.Read(ref _invalidThroughGenerationId);
                if (observed >= invalidThroughGenerationId)
                {
                    break;
                }
            }
            while (Interlocked.CompareExchange(
                ref _invalidThroughGenerationId,
                invalidThroughGenerationId,
                observed) != observed);
            Interlocked.Increment(ref _interruptCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _stopCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Enqueued.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeVad : IVoiceActivityDetector
    {
        private int _resetCount;

        public AudioFormat InputFormat => AudioFormat.Pcm16Mono16Khz;

        public ConcurrentQueue<VoiceActivityChange> Changes { get; } = new();

        public int ResetCount => Volatile.Read(ref _resetCount);

        public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<VoiceActivityChange> ProcessAsync(
            AudioFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                Changes.TryDequeue(out VoiceActivityChange change)
                    ? change
                    : VoiceActivityChange.None);
        }

        public ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resetCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRecognizer : ISpeechRecognizer
    {
        private int _processed;
        private int _resetCount;

        public AudioFormat InputFormat => AudioFormat.Pcm16Mono16Khz;

        public ConcurrentQueue<string> FinalResults { get; } = new();

        public int ResetCount => Volatile.Read(ref _resetCount);

        public TaskCompletionSource AudioProcessed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask<SpeechRecognitionUpdate?> ProcessAudioAsync(
            AudioFrame frame,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _processed);
            AudioProcessed.TrySetResult();
            return ValueTask.FromResult<SpeechRecognitionUpdate?>(null);
        }

        public ValueTask<SpeechRecognitionResult> CompleteUtteranceAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SpeechRecognitionResult(
                FinalResults.TryDequeue(out string? text) ? text : string.Empty));
        }

        public ValueTask ResetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _resetCount);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSynthesizer : ISpeechSynthesizer
    {
        public AudioFormat OutputFormat => AudioFormat.Pcm16Mono24Khz;

        public ConcurrentQueue<SpeechSynthesisRequest> Requests { get; } = new();

        public bool EmitLateChunkAfterRelease { get; set; }

        public Exception? Failure { get; set; }

        public TaskCompletionSource ReleaseLateChunk { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource LateChunkProduced { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask InitializeAsync(CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<SynthesizedAudioChunk> SynthesizeAsync(
            SpeechSynthesisRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Requests.Enqueue(request);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            yield return new SynthesizedAudioChunk([checked((byte)request.GenerationId), 0]);
            if (EmitLateChunkAfterRelease)
            {
                await ReleaseLateChunk.Task;
                LateChunkProduced.TrySetResult();
                yield return new SynthesizedAudioChunk([checked((byte)request.GenerationId), 1]);
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMetrics : IVoiceMetrics
    {
        public ConcurrentQueue<VoiceMetric> Recorded { get; } = new();

        public void Record(VoiceMetric metric) => Recorded.Enqueue(metric);
    }
}
