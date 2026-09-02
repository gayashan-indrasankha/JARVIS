namespace Jarvis.Core.Tests.Architecture;

public sealed class CoreDependencyTests
{
    [Fact]
    public void CoreDoesNotReferenceOuterApplicationLayers()
    {
        string[] referencedAssemblies = typeof(AssemblyMarker)
            .Assembly
            .GetReferencedAssemblies()
            .Select(static assembly => assembly.Name ?? string.Empty)
            .ToArray();

        string[] forbiddenReferences =
        [
            "Jarvis.Host",
            "Jarvis.Infrastructure",
        ];

        Assert.Empty(referencedAssemblies.Intersect(forbiddenReferences, StringComparer.Ordinal));
        Assert.DoesNotContain(
            referencedAssemblies,
            static reference =>
                reference.StartsWith("Microsoft.Extensions", StringComparison.Ordinal) ||
                reference.StartsWith("NAudio", StringComparison.Ordinal) ||
                reference.StartsWith("OpenAI", StringComparison.Ordinal) ||
                reference.StartsWith("SherpaOnnx", StringComparison.Ordinal) ||
                reference.StartsWith("System.Net.Http", StringComparison.Ordinal) ||
                reference.StartsWith("Microsoft.Data.Sqlite", StringComparison.Ordinal));
        Assert.DoesNotContain(
            referencedAssemblies,
            static reference => reference.StartsWith("Microsoft.CodeAnalysis", StringComparison.Ordinal));
    }
}
