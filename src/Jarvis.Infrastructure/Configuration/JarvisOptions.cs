namespace Jarvis.Infrastructure.Configuration;

using Jarvis.Core.Voice;

/// <summary>
/// Non-secret settings for the local JARVIS process.
/// </summary>
public sealed class JarvisOptions
{
    public const string SectionName = "Jarvis";

    public string InstanceName { get; set; } = "Local";
}

/// <summary>
/// Realtime voice settings. The API key is supplied only by user secrets or the environment.
/// </summary>
public sealed class VoiceOptions
{
    public const string SectionName = "Voice";

    public bool Enabled { get; set; }

    public bool AutoStart { get; set; }

    public VoiceActivationMode ActivationMode { get; set; } =
        VoiceActivationMode.ServerVoiceActivityDetection;

    public string Instructions { get; set; } =
        "You are JARVIS, a concise and helpful personal computing assistant.";

    public OpenAiRealtimeOptions OpenAi { get; set; } = new();

    public AudioDeviceOptions Audio { get; set; } = new();
}

public sealed class OpenAiRealtimeOptions
{
    public string? ApiKey { get; set; }

    public Uri Endpoint { get; set; } = new("wss://api.openai.com/v1/realtime");

    public string Model { get; set; } = "gpt-realtime-2.1";

    public string Voice { get; set; } = "marin";

    public int ConnectTimeoutSeconds { get; set; } = 20;

    public int MaximumReconnectAttempts { get; set; } = 8;

    public int InitialReconnectDelayMilliseconds { get; set; } = 250;

    public int MaximumReconnectDelayMilliseconds { get; set; } = 5_000;
}

public sealed class AudioDeviceOptions
{
    public int InputDeviceNumber { get; set; } = -1;

    public int OutputDeviceNumber { get; set; } = -1;

    public int CaptureBufferMilliseconds { get; set; } = 50;

    public int MaximumPlaybackBufferMilliseconds { get; set; } = 5_000;
}
