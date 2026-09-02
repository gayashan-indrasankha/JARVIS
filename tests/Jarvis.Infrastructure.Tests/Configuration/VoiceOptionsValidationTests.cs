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
        ToolOptions tools = provider.GetRequiredService<IOptions<ToolOptions>>().Value;
        ProjectIntelligenceOptions projects = provider
            .GetRequiredService<IOptions<ProjectIntelligenceOptions>>().Value;
        ProjectLearningOptions learning = provider
            .GetRequiredService<IOptions<ProjectLearningOptions>>().Value;

        Assert.True(localAi.Enabled);
        Assert.Equal("127.0.0.1", localAi.Host);
        Assert.False(voice.Enabled);
        Assert.False(voice.WakeWord.AlwaysListeningEnabled);
        Assert.Equal("Jarvis", voice.WakeWord.Phrase);
        Assert.True(tools.Enabled);
        Assert.Empty(tools.AllowedRoots);
        Assert.True(projects.Enabled);
        Assert.Equal(8_192, projects.MaximumContextCharacters);
        Assert.True(learning.Enabled);
        Assert.True(learning.PersistSessions);
        Assert.False(localAi.Deep.Enabled);
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
    [InlineData("LocalAi:GenerationTimeoutSeconds", "0")]
    [InlineData("LocalAi:Deep:ContextSize", "2048")]
    [InlineData("LocalAi:Deep:MinimumAvailableMemoryBytes", "1")]
    [InlineData("Tools:MaximumToolSteps", "9")]
    [InlineData("Tools:MaximumResultCharacters", "100")]
    [InlineData("Tools:DefaultTimeoutSeconds", "0")]
    [InlineData("Tools:AllowedRoots:0", "relative/path")]
    [InlineData("ProjectIntelligence:MaximumFiles", "0")]
    [InlineData("ProjectIntelligence:MaximumTotalTextBytes", "100")]
    [InlineData("ProjectIntelligence:MaximumContextCharacters", "100")]
    [InlineData("ProjectIntelligence:WatchDebounceMilliseconds", "10")]
    [InlineData("ProjectIntelligence:IndexTimeoutSeconds", "121")]
    [InlineData("ProjectLearning:MaximumEvidenceItems", "0")]
    [InlineData("ProjectLearning:MinimumInterviewQuestions", "0")]
    [InlineData("ProjectLearning:MaximumInterviewQuestions", "21")]
    [InlineData("ProjectLearning:OperationTimeoutSeconds", "121")]
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
            else if (key.StartsWith("Tools:", StringComparison.Ordinal))
            {
                _ = provider.GetRequiredService<IOptions<ToolOptions>>().Value;
            }
            else if (key.StartsWith("ProjectIntelligence:", StringComparison.Ordinal))
            {
                _ = provider.GetRequiredService<IOptions<ProjectIntelligenceOptions>>().Value;
            }
            else if (key.StartsWith("ProjectLearning:", StringComparison.Ordinal))
            {
                _ = provider.GetRequiredService<IOptions<ProjectLearningOptions>>().Value;
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

    [Fact]
    public void ToolRootsRejectNetworkShares()
    {
        using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Tools:AllowedRoots:0"] = "\\\\server\\share\\folder",
            });

        Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<ToolOptions>>().Value);
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
