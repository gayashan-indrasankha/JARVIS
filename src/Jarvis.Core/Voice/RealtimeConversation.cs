namespace Jarvis.Core.Voice;

/// <summary>
/// Provider-neutral entry point for a persistent realtime conversation.
/// </summary>
public interface IRealtimeConversationProvider
{
    public Task<IRealtimeConversationSession> OpenSessionAsync(
        RealtimeSessionConfiguration configuration,
        CancellationToken cancellationToken);
}

/// <summary>
/// Provider-neutral operations supported by an active realtime conversation.
/// </summary>
public interface IRealtimeConversationSession : IAsyncDisposable
{
    public IAsyncEnumerable<RealtimeConversationEvent> ReadEventsAsync(CancellationToken cancellationToken);

    public ValueTask<bool> SendInputAudioAsync(
        ReadOnlyMemory<byte> audio,
        CancellationToken cancellationToken);

    public ValueTask SubmitTextAsync(string text, CancellationToken cancellationToken);

    public ValueTask CompleteInputTurnAsync(CancellationToken cancellationToken);

    public ValueTask CancelResponseAsync(CancellationToken cancellationToken);

    public ValueTask TruncateResponseAsync(
        PlaybackCursor cursor,
        CancellationToken cancellationToken);
}

public sealed record RealtimeSessionConfiguration
{
    public RealtimeSessionConfiguration(
        VoiceActivationMode activationMode,
        string instructions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instructions);

        if (!Enum.IsDefined(activationMode))
        {
            throw new ArgumentOutOfRangeException(nameof(activationMode));
        }

        if (instructions.Length > VoiceDataLimits.MaximumInstructionsCharacters)
        {
            throw new ArgumentException("Session instructions exceed the size limit.", nameof(instructions));
        }

        ActivationMode = activationMode;
        Instructions = instructions;
    }

    public VoiceActivationMode ActivationMode { get; }

    public string Instructions { get; }
}

public enum VoiceActivationMode
{
    ServerVoiceActivityDetection,
    PushToTalk,
}

public abstract record RealtimeConversationEvent;

public sealed record RealtimeConnectedEvent : RealtimeConversationEvent;

public sealed record RealtimeReconnectingEvent(
    int Attempt,
    string ReasonCode) : RealtimeConversationEvent;

public sealed record RealtimeDisconnectedEvent(
    string ReasonCode) : RealtimeConversationEvent;

public sealed record AssistantAudioDeltaEvent(
    AssistantAudioChunk Chunk) : RealtimeConversationEvent;

public sealed record AssistantTranscriptDeltaEvent(
    string Text) : RealtimeConversationEvent;

public sealed record UserSpeechStartedEvent : RealtimeConversationEvent;

public sealed record UserSpeechStoppedEvent : RealtimeConversationEvent;

public sealed record AssistantResponseCompletedEvent(
    string? ItemId) : RealtimeConversationEvent;

public sealed record RealtimeProviderErrorEvent(
    string Code,
    bool IsTransient) : RealtimeConversationEvent;
