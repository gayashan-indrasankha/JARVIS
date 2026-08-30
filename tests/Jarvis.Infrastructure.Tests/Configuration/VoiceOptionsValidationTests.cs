using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Tests.Configuration;

public sealed class VoiceOptionsValidationTests
{
    [Fact]
    public void DisabledVoiceDoesNotRequireCredential()
    {
        using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Voice:Enabled"] = "false",
            });

        VoiceOptions options = provider.GetRequiredService<IOptions<VoiceOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.Null(options.OpenAi.ApiKey);
    }

    [Fact]
    public void EnabledVoiceRejectsMissingCredential()
    {
        using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Voice:Enabled"] = "true",
            });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<VoiceOptions>>().Value);

        Assert.Contains("credential is missing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledVoiceRejectsEndpointThatCouldExfiltrateCredential()
    {
        using ServiceProvider provider = BuildProvider(
            new Dictionary<string, string?>
            {
                ["Voice:Enabled"] = "true",
                ["Voice:OpenAI:ApiKey"] = "test-secret",
                ["Voice:OpenAI:Endpoint"] = "wss://attacker.invalid/realtime",
            });

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<VoiceOptions>>().Value);

        Assert.Contains("official secure OpenAI", exception.Message, StringComparison.Ordinal);
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
