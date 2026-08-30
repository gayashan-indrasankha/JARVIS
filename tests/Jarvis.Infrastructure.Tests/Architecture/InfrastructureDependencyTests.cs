using Jarvis.Infrastructure.Configuration;

namespace Jarvis.Infrastructure.Tests.Architecture;

public sealed class InfrastructureDependencyTests
{
    [Fact]
    public void InfrastructureReferencesCoreButNotHost()
    {
        string[] referencedAssemblies = typeof(VoiceOptions)
            .Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("Jarvis.Core", referencedAssemblies, StringComparer.Ordinal);
        Assert.DoesNotContain("Jarvis.Host", referencedAssemblies, StringComparer.Ordinal);
    }
}
