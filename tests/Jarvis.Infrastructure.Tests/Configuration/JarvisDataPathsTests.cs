using Jarvis.Infrastructure.Configuration;

namespace Jarvis.Infrastructure.Tests.Configuration;

public sealed class JarvisDataPathsTests
{
    [Fact]
    public void ExplicitHomeProducesTheRequiredDirectoryLayout()
    {
        string home = Path.Combine(Path.GetTempPath(), $"jarvis-paths-{Guid.NewGuid():N}");

        JarvisDataPaths paths = JarvisDataPaths.Create(home);

        Assert.Equal(Path.GetFullPath(home), paths.Root);
        Assert.Equal(Path.Combine(home, "Models", "Llm"), paths.LlmModels);
        Assert.Equal(Path.Combine(home, "Models", "Speech"), paths.SpeechModels);
        Assert.Equal(Path.Combine(home, "Models", "Tts"), paths.TtsModels);
        Assert.Equal(Path.Combine(home, "Models", "Vad"), paths.VadModels);
        Assert.Equal(Path.Combine(home, "Models", "WakeWord"), paths.WakeWordModels);
        Assert.Equal(Path.Combine(home, "Runtime", "LlamaCpp"), paths.LlamaCppRuntime);
        Assert.Equal(Path.Combine(home, "Data"), paths.Data);
        Assert.Equal(Path.Combine(home, "Logs"), paths.Logs);
        Assert.Equal(Path.Combine(home, "Cache"), paths.Cache);
    }

    [Theory]
    [InlineData("relative-path")]
    [InlineData("..\\relative-path")]
    public void HomeMustBeFullyQualified(string home) =>
        Assert.Throws<InvalidOperationException>(() => JarvisDataPaths.Create(home));

    [Fact]
    public void HomeCannotBeTheFilesystemRoot()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;

        Assert.Throws<InvalidOperationException>(() => JarvisDataPaths.Create(root));
    }

    [Fact]
    public void HomeCannotBeInsideTheRepository()
    {
        string repositoryRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        Assert.Throws<InvalidOperationException>(() =>
            JarvisDataPaths.Create(Path.Combine(repositoryRoot, "local-models")));
    }

    [Theory]
    [InlineData("..\\escaped.bin")]
    [InlineData("sub\\..\\..\\escaped.bin")]
    public void AssetResolutionRejectsTraversal(string relativePath)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"jarvis-paths-{Guid.NewGuid():N}");

        Assert.Throws<InvalidOperationException>(() =>
            JarvisDataPaths.ResolveUnder(directory, relativePath));
    }
}
