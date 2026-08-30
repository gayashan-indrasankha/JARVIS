namespace Jarvis.Core.Voice;

public enum VoiceSessionState
{
    Stopped,
    Activating,
    Listening,
    AwaitingResponse,
    Speaking,
    Interrupted,
    Recovering,
    Faulted,
}

public abstract record VoiceSessionNotification;

public sealed record VoiceSessionStateChangedNotification(
    VoiceSessionState State) : VoiceSessionNotification;

public sealed record AssistantTranscriptNotification(
    string Text) : VoiceSessionNotification;

public sealed record VoiceSessionErrorNotification(
    string Code,
    bool IsTransient) : VoiceSessionNotification;
