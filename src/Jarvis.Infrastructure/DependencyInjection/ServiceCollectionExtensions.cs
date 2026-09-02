using Jarvis.Core.ProjectIntelligence;
using Jarvis.Core.ProjectLearning;
using Jarvis.Core.Tools;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.ProjectIntelligence;
using Jarvis.Infrastructure.ProjectLearning;
using Jarvis.Infrastructure.Tools;
using Jarvis.Infrastructure.Voice;
using Jarvis.Infrastructure.Voice.Local;
using Jarvis.Infrastructure.Voice.Local.Llama;
using Jarvis.Infrastructure.Voice.Local.Sherpa;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddJarvisInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<JarvisOptions>()
            .Bind(configuration.GetSection(JarvisOptions.SectionName))
            .Validate(
                options => IsSafeIdentifier(options.InstanceName, 64),
                $"{JarvisOptions.SectionName}:InstanceName must be a safe identifier.")
            .ValidateOnStart();

        services
            .AddOptions<VoiceOptions>()
            .Bind(configuration.GetSection(VoiceOptions.SectionName))
            .Validate(
                options => IsSafeIdentifier(options.SpeechRecognitionProfile, 64) &&
                    IsSafeIdentifier(options.TtsVoice, 64) &&
                    Enum.IsDefined(options.ActivationMode) &&
                    !string.IsNullOrWhiteSpace(options.Persona) &&
                    options.Persona.Length <= VoiceDataLimits.MaximumInstructionsCharacters &&
                    !options.Persona.Any(static character => character == '\0'),
                "Voice profiles, activation mode, and persona are invalid.")
            .Validate(
                options => options.TtsSpeed is >= 0.5F and <= 2.0F &&
                    options.Audio.CaptureBufferMilliseconds is >= 10 and <= 500 &&
                    options.Audio.MaximumPlaybackBufferMilliseconds is >= 500 and <= 30_000 &&
                    options.Audio.InputDeviceNumber >= -1 &&
                    options.Audio.OutputDeviceNumber >= -1,
                "Voice speed or audio buffer settings are invalid.")
            .Validate(
                options => options.VoiceActivityDetection.Threshold is >= 0.1F and <= 0.95F &&
                    options.VoiceActivityDetection.MinimumSilenceSeconds is >= 0.1F and <= 3.0F &&
                    options.VoiceActivityDetection.MinimumSpeechSeconds is >= 0.05F and <= 2.0F &&
                    options.VoiceActivityDetection.MaximumSpeechSeconds is >= 2.0F and <= 60.0F,
                "Voice activity-detection settings are invalid.")
            .Validate(
                options => options.ResponseSegmentation.MinimumSentenceCharacters is >= 8 and <= 120 &&
                    options.ResponseSegmentation.MinimumClauseCharacters >=
                        options.ResponseSegmentation.MinimumSentenceCharacters &&
                    options.ResponseSegmentation.MinimumClauseCharacters <= 240 &&
                    options.ResponseSegmentation.MaximumSegmentCharacters >=
                        options.ResponseSegmentation.MinimumClauseCharacters &&
                    options.ResponseSegmentation.MaximumSegmentCharacters <=
                        VoiceDataLimits.MaximumSpeechSegmentCharacters,
                "Voice response-segmentation settings are invalid.")
            .Validate(
                options => !options.WakeWord.AlwaysListeningEnabled || options.Enabled,
                "Voice must be enabled when always-listening wake-word detection is enabled.")
            .Validate(
                options => string.Equals(
                        options.WakeWord.Phrase,
                        "Jarvis",
                        StringComparison.OrdinalIgnoreCase) &&
                    float.IsFinite(options.WakeWord.KeywordScore) &&
                    options.WakeWord.KeywordScore is >= 0.1F and <= 10.0F &&
                    float.IsFinite(options.WakeWord.KeywordThreshold) &&
                    options.WakeWord.KeywordThreshold is >= 0.01F and <= 0.99F &&
                    double.IsFinite(options.WakeWord.CooldownSeconds) &&
                    options.WakeWord.CooldownSeconds is >= 0.5 and <= 60.0 &&
                    double.IsFinite(options.WakeWord.ContinuationWindowSeconds) &&
                    options.WakeWord.ContinuationWindowSeconds is >= 2.0 and <= 600.0 &&
                    options.WakeWord.Acknowledgement is not null &&
                    options.WakeWord.Acknowledgement.Length <= 80 &&
                    !options.WakeWord.Acknowledgement.Any(char.IsControl),
                "Voice wake-word settings are invalid. This release supports the phrase Jarvis.")
            .ValidateOnStart();

        services
            .AddOptions<LocalAiOptions>()
            .Bind(configuration.GetSection(LocalAiOptions.SectionName))
            .Validate(
                options => Enum.IsDefined(options.RuntimeMode) &&
                    string.Equals(options.Host, "127.0.0.1", StringComparison.Ordinal) &&
                    IsSafeIdentifier(options.ModelId, 64),
                "LocalAi must use a supported runtime/model and bind exactly to 127.0.0.1.")
            .Validate(
                options => options.Port is >= 1 and <= 65_535 &&
                    options.ContextSize is >= 4_096 and <= 32_768 &&
                    options.GpuLayers is >= 0 and <= 99 &&
                    options.Threads is >= 1 and <= 64 &&
                    options.StartupTimeoutSeconds is >= 5 and <= 600 &&
                    options.GenerationTimeoutSeconds is >= 10 and <= 900 &&
                    options.MaximumOutputTokens is >= 1 and <= 4_096,
                "LocalAi resource or timeout settings are invalid.")
            .Validate(
                options => options.Deep is not null &&
                    IsSafeIdentifier(options.Deep.ModelId, 64) &&
                    options.Deep.ContextSize is >= 4_096 and <= 16_384 &&
                    options.Deep.GpuLayers is >= 0 and <= 99 &&
                    options.Deep.Threads is >= 1 and <= 64 &&
                    options.Deep.MinimumAvailableMemoryBytes is >= 2L * 1024 * 1024 * 1024 and
                        <= 256L * 1024 * 1024 * 1024,
                "LocalAi DEEP profile resource settings are invalid.")
            .ValidateOnStart();

        services
            .AddOptions<ProjectLearningOptions>()
            .Bind(configuration.GetSection(ProjectLearningOptions.SectionName))
            .Validate(
                options => options.MaximumContextCharacters is >= 4_096 and <= 24_000 &&
                    options.MaximumEvidenceItems is >= 1 and <= ProjectLearningLimits.MaximumEvidenceItems &&
                    options.MaximumRecentTurns is >= 1 and <= 12 &&
                    options.MinimumInterviewQuestions is >= 1 and <= 20 &&
                    options.MaximumInterviewQuestions >= options.MinimumInterviewQuestions &&
                    options.MaximumInterviewQuestions <= 20,
                "Project learning context and interview limits are invalid.")
            .Validate(
                options => options.MaximumPersistedSessions is >= 1 and <= 1_000 &&
                    options.OperationTimeoutSeconds is >= 10 and <= 120,
                "Project learning retention or timeout settings are invalid.")
            .ValidateOnStart();

        services
            .AddOptions<ToolOptions>()
            .Bind(configuration.GetSection(ToolOptions.SectionName))
            .Validate(
                options => options.MaximumToolSteps is >= 1 and <= 8 &&
                    options.MaximumResultCharacters is >= 1_024 and <=
                        ToolDataLimits.MaximumObservationCharacters &&
                    options.DefaultTimeoutSeconds is >= 1 and <= 60,
                "Tool loop and result limits are invalid.")
            .Validate(
                options => options.AllowedRoots is not null &&
                    options.AllowedRoots.All(IsSafeConfiguredRoot),
                "Every configured tool root must be an absolute, non-root path without control characters.")
            .ValidateOnStart();

        services
            .AddOptions<ProjectIntelligenceOptions>()
            .Bind(configuration.GetSection(ProjectIntelligenceOptions.SectionName))
            .Validate(
                options => options.MaximumFiles is >= 1 and <= 100_000 &&
                    options.MaximumSourceFileBytes is >= 4_096 and <= 16 * 1024 * 1024 &&
                    options.MaximumTotalTextBytes is >= 1024 * 1024 and <= 512 * 1024 * 1024 &&
                    options.MaximumContextCharacters is >= 4_096 and <=
                        ToolDataLimits.MaximumObservationCharacters &&
                    options.MaximumExcerptCharacters is >= 256 and <= 4_096,
                "Project intelligence file and context limits are invalid.")
            .Validate(
                options => options.WatchDebounceMilliseconds is >= 100 and <= 30_000 &&
                    options.MaximumWatchedRepositories is >= 1 and <= 32 &&
                    options.IndexTimeoutSeconds is >= 10 and <= 120 &&
                    options.QueryTimeoutSeconds is >= 1 and <= 60,
                "Project intelligence watcher or timeout settings are invalid.")
            .ValidateOnStart();

        services.AddSingleton(static _ => JarvisDataPaths.Create());
        services.AddSingleton<LocalAssetPaths>();
        services.AddSingleton<ILoopbackHttpClientFactory, LoopbackHttpClientFactory>();
        services.AddSingleton<IManagedProcessFactory, SystemManagedProcessFactory>();
        services.AddSingleton<ILlamaServerHealthProbe, LlamaServerHealthProbe>();
        services.AddSingleton<ILlamaServerSupervisor, LlamaServerSupervisor>();
        services.AddSingleton<IVoiceMetrics, StructuredVoiceMetrics>();
        services.AddSingleton<ILanguageModel, LlamaCppLocalLanguageModel>();
        services.AddSingleton<IAgentPlanner, LlamaCppAgentPlanner>();
        services.AddSingleton<ToolPathPolicy>();
        services.AddSingleton<IWindowsActionLauncher, WindowsActionLauncher>();
        services.AddSingleton<ISafeExecutableResolver, SafeExecutableResolver>();
        services.AddSingleton<IBoundedProcessRunner, BoundedProcessRunner>();
        services.AddSingleton<ISystemMetricsProvider, WindowsSystemMetricsProvider>();
        services.AddSingleton<IToolExecutor<ListDirectoryRequest, ListDirectoryResponse>, ListDirectoryTool>();
        services.AddSingleton<IToolExecutor<FindFilesRequest, FindFilesResponse>, FindFilesTool>();
        services.AddSingleton<
            IToolExecutor<GetFileMetadataRequest, GetFileMetadataResponse>,
            GetFileMetadataTool>();
        services.AddSingleton<IToolExecutor<OpenFileRequest, OpenFileResponse>, OpenFileTool>();
        services.AddSingleton<IToolExecutor<OpenFolderRequest, OpenFolderResponse>, OpenFolderTool>();
        services.AddSingleton<IToolExecutor<ReadTextFileRequest, ReadTextFileResponse>, ReadTextFileTool>();
        services.AddSingleton<
            IToolExecutor<LaunchApplicationRequest, LaunchApplicationResponse>,
            LaunchApplicationTool>();
        services.AddSingleton<IToolExecutor<ListProcessesRequest, ListProcessesResponse>, ListProcessesTool>();
        services.AddSingleton<
            IToolExecutor<GetSystemMetricsRequest, GetSystemMetricsResponse>,
            GetSystemMetricsTool>();
        services.AddSingleton<IToolExecutor<GetGitStatusRequest, GetGitStatusResponse>, GetGitStatusTool>();
        services.AddSingleton<
            IToolExecutor<ExecuteSafeCommandRequest, ExecuteSafeCommandResponse>,
            ExecuteSafeCommandTool>();
        services.AddSingleton<SafeRepositoryDiscovery>();
        services.AddSingleton<RoslynProjectAnalyzer>();
        services.AddSingleton<SqliteProjectIndexStore>();
        services.AddSingleton<IGitRepositoryMetadataReader, GitRepositoryMetadataReader>();
        services.AddSingleton<ProjectWatchManager>();
        services.AddSingleton<ProjectIntelligenceService>();
        services.AddSingleton<IProjectIntelligenceService>(static provider =>
            provider.GetRequiredService<ProjectIntelligenceService>());
        services.AddSingleton(static provider => new ProjectToolExecutors(
            new Lazy<IProjectIntelligenceService>(
                () => provider.GetRequiredService<IProjectIntelligenceService>())));
        services.AddSingleton<IAvailablePhysicalMemoryProvider, WindowsAvailablePhysicalMemoryProvider>();
        services.AddSingleton<LocalModelProfileRouter>();
        services.AddSingleton<IModelProfileRouter>(static provider =>
            provider.GetRequiredService<LocalModelProfileRouter>());
        services.AddSingleton<IProjectLearningSessionStore, SqliteProjectLearningSessionStore>();
        services.AddSingleton<IProjectLearningEvidenceSource, ProjectLearningEvidenceSource>();
        services.AddSingleton<IProjectLearningModel, LlamaProjectLearningModel>();
        services.AddSingleton(static provider =>
        {
            ProjectLearningOptions options = provider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<ProjectLearningOptions>>().Value;
            return new ProjectLearningConfiguration(
                options.MaximumContextCharacters,
                options.MaximumEvidenceItems,
                options.MaximumRecentTurns,
                options.MinimumInterviewQuestions,
                options.MaximumInterviewQuestions);
        });
        services.AddSingleton<ProjectLearningService>();
        services.AddSingleton<IProjectLearningService>(static provider =>
            provider.GetRequiredService<ProjectLearningService>());
        services.AddSingleton(static provider => new ProjectLearningToolExecutors(
            new Lazy<IProjectLearningService>(
                () => provider.GetRequiredService<IProjectLearningService>())));
        services.AddSingleton<ToolRegistry>();
        services.AddSingleton<IToolCatalog>(static provider =>
            provider.GetRequiredService<ToolRegistry>());
        services.AddSingleton<IToolAuthorizationPolicy, DefaultToolAuthorizationPolicy>();
        services.AddSingleton<IToolAuditSink, StructuredToolAuditSink>();
        services.AddSingleton<IToolDispatcher, ToolDispatcher>();
        services.AddSingleton(static provider =>
        {
            ToolOptions options = provider.GetRequiredService<
                Microsoft.Extensions.Options.IOptions<ToolOptions>>().Value;
            return new ToolAgentConfiguration(options.Enabled, options.MaximumToolSteps);
        });
        services.AddSingleton<IAgentRuntime, ToolEnabledAgentRuntime>();
        services.AddSingleton<IVoiceActivityDetector, SherpaOnnxVoiceActivityDetector>();
        services.AddSingleton<ISpeechRecognizer, SherpaOnnxSpeechRecognizer>();
        services.AddSingleton<ISpeechSynthesizer, SherpaOnnxKokoroSpeechSynthesizer>();
        services.AddSingleton<IAudioCapture, WindowsMicrophoneCapture>();
        services.AddSingleton<IAudioPlayback, WindowsSpeakerPlayback>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IWakeWordDetector, SherpaOnnxKeywordSpotter>();
        services.AddSingleton<RealtimeVoiceCoordinator>();

        return services;
    }

    private static bool IsSafeIdentifier(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static bool IsSafeConfiguredRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root) ||
            root.Length > ToolDataLimits.MaximumPathCharacters ||
            root.Any(char.IsControl) ||
            root.StartsWith("\\\\", StringComparison.Ordinal) ||
            !Path.IsPathFullyQualified(root))
        {
            return false;
        }

        try
        {
            string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string? pathRoot = Path.GetPathRoot(fullPath);
            return pathRoot is not null &&
                !string.Equals(
                    fullPath,
                    Path.TrimEndingDirectorySeparator(pathRoot),
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
