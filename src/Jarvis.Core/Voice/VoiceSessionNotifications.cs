namespace Jarvis.Core.Voice;

public enum VoiceSessionState
{
    Stopped,
    Sleeping,
    Activating,
    Listening,
    AwaitingResponse,
    Speaking,
    Interrupted,
    Faulted,
}

public abstract record VoiceSessionNotification;

public sealed record VoiceSessionStateChangedNotification(
    VoiceSessionState State) : VoiceSessionNotification;

public sealed record AssistantTranscriptNotification(
    string Text) : VoiceSessionNotification;

public sealed record UserTranscriptNotification(
    string Text,
    bool IsFinal) : VoiceSessionNotification;

public sealed record VoiceSessionErrorNotification(
    string Code,
    bool IsTransient) : VoiceSessionNotification;

public sealed record WakeWordDetectedNotification(
    string Phrase) : VoiceSessionNotification;

public enum VoiceCaptureState
{
    Off,
    WakeWord,
    Conversation,
    PushToTalk,
}

public sealed record VoiceCaptureStateChangedNotification(
    VoiceCaptureState State) : VoiceSessionNotification;

public enum VoiceMetricKind
{
    EndToEndTurn,
    BargeInPlaybackStop,
    LanguageModelReady,
    PromptProcessing,
    FirstToken,
    TokensPerSecond,
    SpeechRecognitionFinalization,
    TextToSpeechFirstAudio,
    KeywordDetectionLatency,
    FalseActivationCount,
    WakeToListeningLatency,
    WarmLanguageModelFirstToken,
}

public sealed record VoiceMetric
{
    public VoiceMetric(VoiceMetricKind kind, double value)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        Kind = kind;
        Value = value;
    }

    public VoiceMetricKind Kind { get; }

    public double Value { get; }
}

public interface IVoiceMetrics
{
    public void Record(VoiceMetric metric);
}
