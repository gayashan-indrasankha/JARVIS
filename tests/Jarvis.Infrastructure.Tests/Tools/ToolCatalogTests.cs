using Jarvis.Core.Tools;
using Jarvis.Infrastructure.DependencyInjection;
using Jarvis.Infrastructure.Tools;
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
                "analyze_project",
                "execute_safe_command",
                "explain_architecture",
                "explain_symbol",
                "find_files",
                "find_references",
                "find_symbol",
                "get_file_metadata",
                "get_git_status",
                "get_project_overview",
                "get_system_metrics",
                "launch_application",
                "list_api_endpoints",
                "list_directory",
                "list_processes",
                "list_project_dependencies",
                "open_file",
                "open_folder",
                "read_text_file",
                "search_project",
                "trace_dependency",
                "trace_request_flow",
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
        Assert.Equal(
            ToolAuthorizationCategory.SafeLocalAction,
            Assert.Single(catalog.Definitions, static definition => definition.Name == "analyze_project")
                .AuthorizationCategory);
        Assert.All(
            catalog.Definitions.Where(static definition => definition.Name is
                "get_project_overview" or "search_project" or "find_symbol" or
                "explain_symbol" or "find_references" or "trace_dependency" or
                "trace_request_flow" or "list_api_endpoints" or
                "list_project_dependencies" or "explain_architecture"),
            static definition => Assert.Equal(
                ToolAuthorizationCategory.SafeRead,
                definition.AuthorizationCategory));
    }

    [Fact]
    public void ProjectSchemasRejectUnknownMembersAndInvalidBoundsBeforeExecution()
    {
        using TemporaryDirectory temporary = new();
        Directory.CreateDirectory(Path.Combine(temporary.Path, ".git"));
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tools:AllowedRoots:0"] = temporary.Path,
            })
            .Build();
        ServiceCollection services = new();
        services.AddJarvisInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        ToolRegistry registry = provider.GetRequiredService<ToolRegistry>();
        Assert.True(registry.TryGet("search_project", out IRegisteredTool? search));
        Assert.NotNull(search);

        ToolValidationException unknown = Assert.Throws<ToolValidationException>(() =>
            search.ValidateAndNormalize(
                $$"""{"repositoryPath":"{{Escape(temporary.Path)}}","query":"orders","extra":true}"""));
        ToolValidationException bounds = Assert.Throws<ToolValidationException>(() =>
            search.ValidateAndNormalize(
                $$"""{"repositoryPath":"{{Escape(temporary.Path)}}","query":"orders","maximumResults":257}"""));

        Assert.Equal("malformed_arguments_json", unknown.Code);
        Assert.Equal("MaximumResults_out_of_range", bounds.Code);
    }

    [Fact]
    public void ProjectToolPathInsideRepositoryNormalizesToApprovedGitRoot()
    {
        using TemporaryDirectory temporary = new();
        Directory.CreateDirectory(Path.Combine(temporary.Path, ".git"));
        string nested = Path.Combine(temporary.Path, "src", "Feature");
        Directory.CreateDirectory(nested);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tools:AllowedRoots:0"] = temporary.Path,
            })
            .Build();
        ServiceCollection services = new();
        services.AddJarvisInfrastructure(configuration);
        using ServiceProvider provider = services.BuildServiceProvider();
        ToolRegistry registry = provider.GetRequiredService<ToolRegistry>();
        Assert.True(registry.TryGet("search_project", out IRegisteredTool? search));
        Assert.NotNull(search);

        SearchProjectRequest request = Assert.IsType<SearchProjectRequest>(search.ValidateAndNormalize(
            $$"""{"repositoryPath":"{{Escape(nested)}}","query":"orders","maximumResults":10}"""));

        Assert.Equal(temporary.Path, request.RepositoryPath);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal);

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
