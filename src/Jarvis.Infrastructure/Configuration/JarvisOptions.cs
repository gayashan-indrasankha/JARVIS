using Jarvis.Core.Voice;

namespace Jarvis.Infrastructure.Configuration;

public sealed class JarvisOptions
{
    public const string SectionName = "Jarvis";

    public string InstanceName { get; set; } = "Local";
}

public enum LocalAiRuntimeMode
{
    Managed,
    External,
}

/// <summary>
/// Local language-model settings. Paths are resolved under the JARVIS data root.
/// </summary>
public sealed class LocalAiOptions
{
    public const string SectionName = "LocalAi";

    public bool Enabled { get; set; } = true;

    public LocalAiRuntimeMode RuntimeMode { get; set; } = LocalAiRuntimeMode.Managed;

    public string ModelId { get; set; } = "qwen3-4b-q4-k-m";

    public int ContextSize { get; set; } = 8_192;

    public int GpuLayers { get; set; } = 24;

    public int Threads { get; set; } = 8;

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 18_080;

    public int StartupTimeoutSeconds { get; set; } = 120;

    public int GenerationTimeoutSeconds { get; set; } = 300;

    public int MaximumOutputTokens { get; set; } = 512;

    public DeepModelOptions Deep { get; set; } = new();
}

public sealed class DeepModelOptions
{
    public bool Enabled { get; set; }

    public string ModelId { get; set; } = "qwen3-8b-q4-k-m";

    public int ContextSize { get; set; } = 6_144;

    public int GpuLayers { get; set; } = 16;

    public int Threads { get; set; } = 8;

    public long MinimumAvailableMemoryBytes { get; set; } = 7L * 1024 * 1024 * 1024;
}

public sealed class VoiceOptions
{
    public const string SectionName = "Voice";

    public bool Enabled { get; set; }

    public bool SpeechOutputEnabled { get; set; }

    public bool AutoStart { get; set; }

    public VoiceActivationMode ActivationMode { get; set; } =
        VoiceActivationMode.VoiceActivityDetection;

    public string SpeechRecognitionProfile { get; set; } =
        "zipformer-en-20m-int8";

    public string TtsVoice { get; set; } = "bm_george";

    public float TtsSpeed { get; set; } = 1.0F;

    public string Persona { get; set; } =
        "You are JARVIS, a concise, calculated, professional technical assistant. " +
        "Use natural spoken sentences. Do not expose hidden reasoning, invent capabilities, " +
        "or claim an action occurred without a confirmed tool result. /no_think";

    public AudioDeviceOptions Audio { get; set; } = new();

    public VoiceActivityDetectionOptions VoiceActivityDetection { get; set; } = new();

    public ResponseSegmentationOptions ResponseSegmentation { get; set; } = new();

    public WakeWordOptions WakeWord { get; set; } = new();
}

public sealed class WakeWordOptions
{
    public bool AlwaysListeningEnabled { get; set; }

    public string Phrase { get; set; } = "Jarvis";

    public float KeywordScore { get; set; } = 1.5F;

    public float KeywordThreshold { get; set; } = 0.25F;

    public double CooldownSeconds { get; set; } = 3.0;

    public double ContinuationWindowSeconds { get; set; } = 30.0;

    public string Acknowledgement { get; set; } = "Yes?";
}

public sealed class AudioDeviceOptions
{
    public int InputDeviceNumber { get; set; } = -1;

    public int OutputDeviceNumber { get; set; } = -1;

    public int CaptureBufferMilliseconds { get; set; } = 50;

    public int MaximumPlaybackBufferMilliseconds { get; set; } = 5_000;
}

public sealed class VoiceActivityDetectionOptions
{
    public float Threshold { get; set; } = 0.5F;

    public float MinimumSilenceSeconds { get; set; } = 0.45F;

    public float MinimumSpeechSeconds { get; set; } = 0.20F;

    public float MaximumSpeechSeconds { get; set; } = 30.0F;
}

public sealed class ResponseSegmentationOptions
{
    public int MinimumSentenceCharacters { get; set; } = 24;

    public int MinimumClauseCharacters { get; set; } = 72;

    public int MaximumSegmentCharacters { get; set; } = 240;
}

public sealed class ToolOptions
{
    public const string SectionName = "Tools";

    public bool Enabled { get; set; } = true;

    public bool AllowSafeLocalActions { get; set; } = true;

    public int MaximumToolSteps { get; set; } = 4;

    public int MaximumResultCharacters { get; set; } = 16 * 1024;

    public int DefaultTimeoutSeconds { get; set; } = 10;

    public List<string> AllowedRoots { get; set; } = [];
}

public sealed class ProjectIntelligenceOptions
{
    public const string SectionName = "ProjectIntelligence";

    public bool Enabled { get; set; } = true;

    public int MaximumFiles { get; set; } = 20_000;

    public int MaximumSourceFileBytes { get; set; } = 2 * 1024 * 1024;

    public int MaximumTotalTextBytes { get; set; } = 64 * 1024 * 1024;

    public int MaximumContextCharacters { get; set; } = 8_192;

    public int MaximumExcerptCharacters { get; set; } = 1_500;

    public int WatchDebounceMilliseconds { get; set; } = 750;

    public int MaximumWatchedRepositories { get; set; } = 8;

    public int IndexTimeoutSeconds { get; set; } = 120;

    public int QueryTimeoutSeconds { get; set; } = 15;
}

public sealed class ProjectLearningOptions
{
    public const string SectionName = "ProjectLearning";

    public bool Enabled { get; set; } = true;

    public bool PersistSessions { get; set; } = true;

    public int MaximumContextCharacters { get; set; } = 12_000;

    public int MaximumEvidenceItems { get; set; } = 10;

    public int MaximumRecentTurns { get; set; } = 6;

    public int MinimumInterviewQuestions { get; set; } = 5;

    public int MaximumInterviewQuestions { get; set; } = 20;

    public int MaximumPersistedSessions { get; set; } = 50;

    public int OperationTimeoutSeconds { get; set; } = 120;
}
