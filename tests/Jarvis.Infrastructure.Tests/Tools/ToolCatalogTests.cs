using Jarvis.Core.Tools;
using Jarvis.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Infrastructure.Tests.Tools;

public sealed class ToolCatalogTests
{
    [Fact]
    public void TrustedCompositionRegistersExactlyTheInitialClosedToolSet()
    {
        using TemporaryDirectory temporary = new();
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tools:AllowedRoots:0"] = temporary.Path,
            })
            .Build();
        ServiceCollection services = new();
        services.AddJarvisInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();

        IToolCatalog catalog = provider.GetRequiredService<IToolCatalog>();

        Assert.Equal(
            [
                "execute_safe_command",
                "find_files",
                "get_file_metadata",
                "get_git_status",
                "get_system_metrics",
                "launch_application",
                "list_directory",
                "list_processes",
                "open_file",
                "open_folder",
                "read_text_file",
            ],
            catalog.Definitions.Select(static definition => definition.Name)
                .Order(StringComparer.Ordinal));
        Assert.All(catalog.Definitions, static definition =>
        {
            Assert.Contains("\"additionalProperties\":false", definition.ArgumentsJsonSchema, StringComparison.Ordinal);
            Assert.True(definition.Timeout > TimeSpan.Zero);
            Assert.True(definition.MaximumResultCharacters <= ToolDataLimits.MaximumObservationCharacters);
        });
        Assert.Single(catalog.ApprovedRoots);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                Directory.GetCurrentDirectory(),
                ".jarvis-tool-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
