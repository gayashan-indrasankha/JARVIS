using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Tests.Configuration;

public sealed class VoiceOptionsValidationTests
{
    [Fact]
    public void DefaultConfigurationNeedsNoCloudCredential()
    {
        using ServiceProvider provider = BuildProvider([]);

        LocalAiOptions localAi = provider.GetRequiredService<IOptions<LocalAiOptions>>().Value;
        VoiceOptions voice = provider.GetRequiredService<IOptions<VoiceOptions>>().Value;

        Assert.True(localAi.Enabled);
        Assert.Equal("127.0.0.1", localAi.Host);
        Assert.False(voice.Enabled);
        Assert.False(voice.WakeWord.AlwaysListeningEnabled);
        Assert.Equal("Jarvis", voice.WakeWord.Phrase);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("0.0.0.0")]
    [InlineData("192.168.1.20")]
    [InlineData("example.com")]
    public void LocalAiRejectsAnythingExceptExactIpv4Loopback(string host)
    {
        using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?> { ["LocalAi:Host"] = host });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<LocalAiOptions>>().Value);

        Assert.Contains("127.0.0.1", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Voice:TtsSpeed", "0.1")]
    [InlineData("Voice:Audio:CaptureBufferMilliseconds", "1")]
    [InlineData("Voice:VoiceActivityDetection:Threshold", "1")]
    [InlineData("Voice:ResponseSegmentation:MaximumSegmentCharacters", "20")]
    [InlineData("Voice:WakeWord:Phrase", "Computer")]
    [InlineData("Voice:WakeWord:KeywordThreshold", "2")]
    [InlineData("Voice:WakeWord:CooldownSeconds", "0.1")]
    [InlineData("Voice:WakeWord:ContinuationWindowSeconds", "1")]
    [InlineData("LocalAi:ContextSize", "2048")]
    [InlineData("LocalAi:Threads", "0")]
    [InlineData("Jarvis:InstanceName", "unsafe instance\nname")]
    public void InvalidResourceConfigurationFailsValidation(string key, string value)
    {
        using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?> { [key] = value });

        Assert.Throws<OptionsValidationException>(() =>
        {
            if (key.StartsWith("Jarvis:", StringComparison.Ordinal))
            {
                _ = provider.GetRequiredService<IOptions<JarvisOptions>>().Value;
            }
            else if (key.StartsWith("LocalAi:", StringComparison.Ordinal))
            {
                _ = provider.GetRequiredService<IOptions<LocalAiOptions>>().Value;
            }
            else
            {
                _ = provider.GetRequiredService<IOptions<VoiceOptions>>().Value;
            }
        });
    }

    [Fact]
    public void AlwaysListeningRequiresVoiceInputToBeEnabled()
    {
        using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Voice:Enabled"] = "false",
                ["Voice:WakeWord:AlwaysListeningEnabled"] = "true",
            });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<VoiceOptions>>().Value);

        Assert.Contains("Voice must be enabled", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?> settings)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        ServiceCollection services = new();
        services.AddJarvisInfrastructure(configuration);
        return services.BuildServiceProvider();
    }
}
