using Jarvis.Infrastructure.Voice.Local.Llama;

namespace Jarvis.Infrastructure.Tests.Voice;

public sealed class ManagedProcessTests
{
    [Fact]
    public async Task DiagnosticDrainBoundsNewlineFreeChildOutput()
    {
        string output = new string('x', 5_000) +
            "CUDA error after a long prefix\nshort line\n";
        List<string> diagnostics = [];

        await SystemManagedProcess.DrainAsync(new StringReader(output), diagnostics.Add);

        Assert.All(diagnostics, static diagnostic => Assert.InRange(diagnostic.Length, 1, 4 * 1024));
        Assert.Contains(
            diagnostics,
            static diagnostic => diagnostic.Contains("CUDA error", StringComparison.Ordinal));
        Assert.Equal("short line", diagnostics[^1]);
    }
}
