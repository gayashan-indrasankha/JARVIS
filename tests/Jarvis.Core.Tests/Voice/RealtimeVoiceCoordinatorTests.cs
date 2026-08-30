using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Jarvis.Core.Voice;

namespace Jarvis.Core.Tests.Voice;

public sealed class RealtimeVoiceCoordinatorTests
{
    [Fact]
    public async Task ServerVadForwardsMicrophoneFramesAndStreamsAssistantAudio()
    {
        FakeSession session = new();
        FakeCapture capture = new();
        FakePlayback playback = new();
        await using RealtimeVoiceCoordinator coordinator = CreateCoordinator(
            session,
            capture,
            playback);

        await coordinator.StartAsync(ServerVadConfiguration(), CancellationToken.None);
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        byte[] microphoneData = [1, 2, 3, 4];
        capture.Emit(microphoneData);
        byte[] sentAudio = await session.AudioReceived.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        AssistantAudioChunk assistantChunk = new([5, 6, 7, 8], "item-1", 0);
        session.Emit(new AssistantAudioDeltaEvent(assistantChunk));
        AssistantAudioChunk playedAudio = await playback.Enqueued.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(microphoneData, sentAudio);
        Assert.Same(assistantChunk, playedAudio);
        Assert.Equal(VoiceSessionState.Speaking, coordinator.State);
    }

    [Fact]
    public async Task ServerSpeechStartStopsPlaybackAndTruncatesAudibleAudio()
    {
        FakeSession session = new();
        FakePlayback playback = new()
        {
            InterruptResult = new PlaybackCursor(
                "item-7",
                0,
                TimeSpan.FromMilliseconds(275)),
        };
        await using RealtimeVoiceCoordinator coordinator = CreateCoordinator(
            session,
            new FakeCapture(),
            playback);

        await coordinator.StartAsync(ServerVadConfiguration(), CancellationToken.None);
        session.Emit(new UserSpeechStartedEvent());

        PlaybackCursor cursor = await session.Truncated.Task
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("item-7", cursor.ItemId);
        Assert.Equal(TimeSpan.FromMilliseconds(275), cursor.PlayedDuration);
        Assert.Equal(1, playback.InterruptCount);
        Assert.Equal(0, session.CancelCount);
        Assert.Equal(VoiceSessionState.Interrupted, coordinator.State);
    }

    [Fact]
    public async Task ExplicitInterruptCancelsGenerationAndTruncatesPlayback()
    {
        FakeSession session = new();
        FakePlayback playback = new()
        {
            InterruptResult = new PlaybackCursor(
                "item-9",
                1,
                TimeSpan.FromMilliseconds(100)),
        };
        await using RealtimeVoiceCoordinator coordinator = CreateCoordinator(
            session,
            new FakeCapture(),
            playback);

        await coordinator.StartAsync(ServerVadConfiguration(), CancellationToken.None);
        session.Emit(new AssistantAudioDeltaEvent(
            new AssistantAudioChunk([1, 2], "item-9", 1)));
        _ = await playback.Enqueued.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await coordinator.InterruptAsync(CancellationToken.None);

        PlaybackCursor truncated = await session.Truncated.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, session.CancelCount);
        Assert.Equal("item-9", truncated.ItemId);
        Assert.Equal(VoiceSessionState.Interrupted, coordinator.State);
    }

    [Fact]
    public async Task PushToTalkCapturesOnlyWhilePressedAndCommitsOnRelease()
    {
        FakeSession session = new();
        FakeCapture capture = new();
        await using RealtimeVoiceCoordinator coordinator = CreateCoordinator(
            session,
            capture,
            new FakePlayback());
        RealtimeSessionConfiguration configuration = new(
            VoiceActivationMode.PushToTalk,
            "Test instructions");

        await coordinator.StartAsync(configuration, CancellationToken.None);
        Assert.False(capture.Started.Task.IsCompleted);

        await coordinator.BeginPushToTalkAsync(CancellationToken.None);
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        capture.Emit([9, 8, 7, 6]);
        _ = await session.AudioReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.EndPushToTalkAsync(CancellationToken.None);

        Assert.Equal(1, session.CompleteInputCount);
        Assert.Equal(VoiceSessionState.AwaitingResponse, coordinator.State);
    }

    [Fact]
    public async Task PushToTalkRejectsTextUntilCaptureIsFinished()
    {
        FakeSession session = new();
        await using RealtimeVoiceCoordinator coordinator = CreateCoordinator(
            session,
            new FakeCapture(),
            new FakePlayback());
        RealtimeSessionConfiguration configuration = new(
            VoiceActivationMode.PushToTalk,
            "Test instructions");

        await coordinator.StartAsync(configuration, CancellationToken.None);
        await coordinator.BeginPushToTalkAsync(CancellationToken.None);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.SubmitTextAsync("text turn", CancellationToken.None));

        Assert.Contains("push-to-talk", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReconnectingEventReportsRecoveryAndSessionCanResume()
    {
        FakeSession session = new();
        await using RealtimeVoiceCoordinator coordinator = CreateCoordinator(
            session,
            new FakeCapture(),
            new FakePlayback());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        await coordinator.StartAsync(ServerVadConfiguration(), timeout.Token);
        session.Emit(new RealtimeReconnectingEvent(1, "network_failure"));
        await WaitForStateAsync(coordinator, VoiceSessionState.Recovering, timeout.Token);

        session.Emit(new RealtimeConnectedEvent());
        await WaitForStateAsync(coordinator, VoiceSessionState.Listening, timeout.Token);

        Assert.Equal(VoiceSessionState.Listening, coordinator.State);
    }

    [Fact]
    public async Task PermanentDisconnectStopsCaptureAndReportsFault()
    {
        FakeSession session = new();
        FakeCapture capture = new();
        await using RealtimeVoiceCoordinator coordinator = CreateCoordinator(
            session,
            capture,
            new FakePlayback());
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        await coordinator.StartAsync(ServerVadConfiguration(), timeout.Token);
        await capture.Started.Task.WaitAsync(timeout.Token);
        session.Emit(new RealtimeDisconnectedEvent("reconnect_limit"));

        await WaitForStateAsync(coordinator, VoiceSessionState.Faulted, timeout.Token);
        await capture.Stopped.Task.WaitAsync(timeout.Token);

        Assert.Equal(VoiceSessionState.Faulted, coordinator.State);
    }

    [Fact]
    public async Task StopCancelsCaptureDisposesSessionAndStopsPlayback()
    {
        FakeSession session = new();
        FakeCapture capture = new();
        FakePlayback playback = new();
        await using RealtimeVoiceCoordinator coordinator = CreateCoordinator(
            session,
            capture,
            playback);

        await coordinator.StartAsync(ServerVadConfiguration(), CancellationToken.None);
        await capture.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await coordinator.StopAsync(CancellationToken.None);

        Assert.True(session.IsDisposed);
        Assert.Equal(1, playback.StopCount);
        Assert.Equal(VoiceSessionState.Stopped, coordinator.State);
    }

    private static RealtimeVoiceCoordinator CreateCoordinator(
        FakeSession session,
        FakeCapture capture,
        FakePlayback playback) =>
        new(new FakeProvider(session), capture, playback);

    private static RealtimeSessionConfiguration ServerVadConfiguration() =>
        new(VoiceActivationMode.ServerVoiceActivityDetection, "Test instructions");

    private static async Task WaitForStateAsync(
        RealtimeVoiceCoordinator coordinator,
        VoiceSessionState expectedState,
        CancellationToken cancellationToken)
    {
        await foreach (VoiceSessionNotification notification in
            coordinator.ReadNotificationsAsync(cancellationToken))
        {
            if (notification is VoiceSessionStateChangedNotification state &&
                state.State == expectedState)
            {
                return;
            }
        }

        throw new InvalidOperationException("The notification stream ended unexpectedly.");
    }

    private sealed class FakeProvider(FakeSession session) : IRealtimeConversationProvider
    {
        public Task<IRealtimeConversationSession> OpenSessionAsync(
            RealtimeSessionConfiguration configuration,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IRealtimeConversationSession>(session);
        }
    }

    private sealed class FakeSession : IRealtimeConversationSession
    {
        private readonly Channel<RealtimeConversationEvent> _events =
            Channel.CreateUnbounded<RealtimeConversationEvent>();

        public TaskCompletionSource<byte[]> AudioReceived { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<PlaybackCursor> Truncated { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CancelCount { get; private set; }

        public int CompleteInputCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public void Emit(RealtimeConversationEvent conversationEvent) =>
            Assert.True(_events.Writer.TryWrite(conversationEvent));

        public IAsyncEnumerable<RealtimeConversationEvent> ReadEventsAsync(
            CancellationToken cancellationToken) =>
            _events.Reader.ReadAllAsync(cancellationToken);

        public ValueTask<bool> SendInputAudioAsync(
            ReadOnlyMemory<byte> audio,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AudioReceived.TrySetResult(audio.ToArray());
            return ValueTask.FromResult(true);
        }

        public ValueTask SubmitTextAsync(string text, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;

        public ValueTask CompleteInputTurnAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompleteInputCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask CancelResponseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CancelCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask TruncateResponseAsync(
            PlaybackCursor cursor,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Truncated.TrySetResult(cursor);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            _events.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeCapture : IAudioCapture
    {
        private readonly Channel<AudioFrame> _frames = Channel.CreateUnbounded<AudioFrame>();

        public AudioFormat Format => AudioFormat.Pcm16Mono24Khz;

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Emit(byte[] data) => Assert.True(_frames.Writer.TryWrite(new AudioFrame(data)));

        public async IAsyncEnumerable<AudioFrame> CaptureAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await foreach (AudioFrame frame in
                    _frames.Reader.ReadAllAsync(cancellationToken))
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
        public AudioFormat Format => AudioFormat.Pcm16Mono24Khz;

        public TaskCompletionSource<AssistantAudioChunk> Enqueued { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public PlaybackCursor? InterruptResult { get; init; }

        public int InterruptCount { get; private set; }

        public int StopCount { get; private set; }

        public ValueTask EnqueueAsync(
            AssistantAudioChunk chunk,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Enqueued.TrySetResult(chunk);
            return ValueTask.CompletedTask;
        }

        public ValueTask<PlaybackCursor?> InterruptAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InterruptCount++;
            return ValueTask.FromResult(InterruptResult);
        }

        public ValueTask StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
