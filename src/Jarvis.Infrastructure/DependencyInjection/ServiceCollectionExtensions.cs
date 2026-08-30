using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.Voice;
using Jarvis.Infrastructure.Voice.OpenAi;
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
                options => !string.IsNullOrWhiteSpace(options.InstanceName),
                $"{JarvisOptions.SectionName}:InstanceName must not be empty.")
            .ValidateOnStart();

        services
            .AddOptions<VoiceOptions>()
            .Bind(configuration.GetSection(VoiceOptions.SectionName))
            .Validate(
                options => !options.Enabled ||
                    HasSafeCredential(options.OpenAi.ApiKey),
                "The realtime provider credential is missing. Configure it with user secrets or JARVIS_Voice__OpenAI__ApiKey.")
            .Validate(
                options => IsOfficialOpenAiRealtimeEndpoint(options.OpenAi.Endpoint),
                "Voice:OpenAI:Endpoint must be the official secure OpenAI realtime endpoint.")
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.OpenAi.Model) &&
                    !string.IsNullOrWhiteSpace(options.OpenAi.Voice) &&
                    IsSafeProviderIdentifier(options.OpenAi.Model, 128) &&
                    IsSafeProviderIdentifier(options.OpenAi.Voice, 64) &&
                    Enum.IsDefined(options.ActivationMode) &&
                    !string.IsNullOrWhiteSpace(options.Instructions) &&
                    options.Instructions.Length <= VoiceDataLimits.MaximumInstructionsCharacters,
                "Voice:OpenAI model and voice must not be empty and instructions must be bounded.")
            .Validate(
                options => options.OpenAi.ConnectTimeoutSeconds is >= 1 and <= 120 &&
                    options.OpenAi.MaximumReconnectAttempts is >= 0 and <= 100 &&
                    options.OpenAi.InitialReconnectDelayMilliseconds >= 1 &&
                    options.OpenAi.MaximumReconnectDelayMilliseconds >=
                        options.OpenAi.InitialReconnectDelayMilliseconds,
                "Voice:OpenAI reconnect and timeout settings are invalid.")
            .Validate(
                options => options.Audio.CaptureBufferMilliseconds is >= 10 and <= 500 &&
                    options.Audio.MaximumPlaybackBufferMilliseconds is >= 500 and <= 30_000 &&
                    options.Audio.InputDeviceNumber >= -1 &&
                    options.Audio.OutputDeviceNumber >= -1,
                "Voice audio buffer settings are invalid.")
            .ValidateOnStart();

        services.AddSingleton<IRealtimeTransportFactory, ClientWebSocketRealtimeTransportFactory>();
        services.AddSingleton<IRealtimeConversationProvider, OpenAiRealtimeProvider>();
        services.AddSingleton<IAudioCapture, WindowsMicrophoneCapture>();
        services.AddSingleton<IAudioPlayback, WindowsSpeakerPlayback>();
        services.AddSingleton<IWakeWordDetector, DisabledWakeWordDetector>();
        services.AddSingleton<RealtimeVoiceCoordinator>();

        return services;
    }

    private static bool HasSafeCredential(string? credential) =>
        !string.IsNullOrWhiteSpace(credential) &&
        credential.All(character => !char.IsControl(character));

    private static bool IsOfficialOpenAiRealtimeEndpoint(Uri endpoint) =>
        endpoint.Scheme == Uri.UriSchemeWss &&
        endpoint.IsDefaultPort &&
        string.Equals(endpoint.IdnHost, "api.openai.com", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(endpoint.AbsolutePath, "/v1/realtime", StringComparison.Ordinal) &&
        string.IsNullOrEmpty(endpoint.Query) &&
        string.IsNullOrEmpty(endpoint.Fragment);

    private static bool IsSafeProviderIdentifier(string value, int maximumLength) =>
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');
}
