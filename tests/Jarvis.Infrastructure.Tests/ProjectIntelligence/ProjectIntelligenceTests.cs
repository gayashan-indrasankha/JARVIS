using System.Text.Json;
using Jarvis.Core.ProjectIntelligence;
using Jarvis.Core.Tools;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.ProjectIntelligence;
using Jarvis.Infrastructure.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Tests.ProjectIntelligence;

public sealed class ProjectIntelligenceTests
{
    [Fact]
    public async Task InitialIndexDiscoversProjectsSymbolsRelationshipsEndpointsAndDependencies()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();

        ProjectIndexReport report = await rig.Service.AnalyzeAsync(
            rig.RepositoryPath,
            CancellationToken.None);
        GroundedProjectAnswer symbols = await rig.Service.FindSymbolAsync(
            rig.RepositoryPath,
            "IOrderService",
            10,
            CancellationToken.None);
        GroundedProjectAnswer namespaces = await rig.Service.FindSymbolAsync(
            rig.RepositoryPath,
            "Sample.Api.Controllers",
            10,
            CancellationToken.None);
        GroundedProjectAnswer references = await rig.Service.FindReferencesAsync(
            rig.RepositoryPath,
            "IOrderService",
            20,
            CancellationToken.None);
        GroundedProjectAnswer explanation = await rig.Service.ExplainSymbolAsync(
            rig.RepositoryPath,
            "OrdersController.Create",
            CancellationToken.None);
        GroundedProjectAnswer dependencyTrace = await rig.Service.TraceDependencyAsync(
            rig.RepositoryPath,
            "OrdersController.Create",
            "IOrderService.CreateAsync",
            4,
            CancellationToken.None);
        GroundedProjectAnswer endpoints = await rig.Service.ListApiEndpointsAsync(
            rig.RepositoryPath,
            100,
            CancellationToken.None);
        GroundedProjectAnswer dependencies = await rig.Service.ListDependenciesAsync(
            rig.RepositoryPath,
            CancellationToken.None);
        GroundedProjectAnswer overview = await rig.Service.GetOverviewAsync(
            rig.RepositoryPath,
            CancellationToken.None);
        GroundedProjectAnswer architecture = await rig.Service.ExplainArchitectureAsync(
            rig.RepositoryPath,
            CancellationToken.None);
        GroundedProjectAnswer flow = await rig.Service.TraceRequestFlowAsync(
            rig.RepositoryPath,
            "POST /api/orders",
            6,
            CancellationToken.None);
        GroundedProjectAnswer controllerReferences = await rig.Service.FindReferencesAsync(
            rig.RepositoryPath,
            "OrdersController.Create",
            20,
            CancellationToken.None);

        Assert.Equal(4, report.ProjectCount);
        Assert.Equal(7, report.SourceFileCount);
        Assert.True(report.SymbolCount >= 10);
        Assert.True(report.RelationshipCount >= 2);
        Assert.Equal("main", report.Branch);
        Assert.Equal("## main", report.GitStatusSummary);
        ProjectEvidence symbolEvidence = Assert.Single(
            symbols.Claims.SelectMany(static claim => claim.Evidence),
            static evidence => evidence.RelativePath.EndsWith("IOrderService.cs", StringComparison.Ordinal) &&
                evidence.StartLine == 3);
        Assert.Equal(3, symbolEvidence.StartLine);
        Assert.Contains(namespaces.Claims, claim => claim.Evidence.Any(evidence =>
            evidence.RelativePath.EndsWith("OrdersController.cs", StringComparison.Ordinal) &&
            evidence.StartLine == 5));
        Assert.Contains(references.Claims, claim =>
            claim.Statement.Contains("implements", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(explanation.Claims, claim =>
            claim.Statement.Contains("Create", StringComparison.Ordinal));
        Assert.Contains(dependencyTrace.Claims, claim =>
            claim.Statement.Contains("CreateAsync", StringComparison.Ordinal));
        Assert.Contains(endpoints.Claims, claim =>
            claim.Statement.Contains("POST /api/orders", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dependencies.Claims, claim =>
            claim.Statement.Contains("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal));
        Assert.Contains(dependencies.Claims, claim =>
            claim.Statement.Contains("Sample.Domain.csproj", StringComparison.Ordinal) &&
            claim.Evidence.Any(static evidence =>
                evidence.RelativePath.EndsWith("Sample.Infrastructure.csproj", StringComparison.Ordinal) &&
                evidence.StartLine == 6));
        Assert.Contains(dependencies.Claims, claim =>
            claim.Statement.Contains("Microsoft.EntityFrameworkCore.Sqlite", StringComparison.Ordinal) &&
            claim.Evidence.Any(static evidence => evidence.StartLine == 7));
        Assert.Contains(overview.Claims, claim =>
            claim.Statement.Contains("orders API", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(overview.Claims, claim =>
            claim.Statement.Contains("UseSqlite", StringComparison.Ordinal));
        Assert.Contains(architecture.Claims, claim =>
            claim.Statement.Contains("AddScoped", StringComparison.Ordinal));
        Assert.Contains(architecture.Claims, claim =>
            claim.Statement.Contains("Sample.Api.Tests", StringComparison.Ordinal));
        Assert.Contains(controllerReferences.Claims, claim =>
            claim.Statement.Contains("CreateAsync", StringComparison.Ordinal));
        Assert.Contains(flow.Claims, claim =>
            claim.Statement.Contains("CreateAsync", StringComparison.Ordinal));
        Assert.All(
            symbols.Claims.Concat(references.Claims).Concat(endpoints.Claims).Concat(dependencies.Claims),
            static claim => Assert.Equal(ProjectKnowledgeClassification.ProjectFact, claim.Classification));
    }

    [Fact]
    public async Task SearchUsesFtsAndReturnsTheActualSupportingLine()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();
        await rig.Service.AnalyzeAsync(rig.RepositoryPath, CancellationToken.None);

        GroundedProjectAnswer answer = await rig.Service.SearchAsync(
            rig.RepositoryPath,
            "JWT bearer middleware",
            10,
            CancellationToken.None);

        ProjectEvidence evidence = Assert.Single(
            answer.Claims.SelectMany(static claim => claim.Evidence),
            static item => item.RelativePath == "README.md" && item.StartLine == 3);
        Assert.Equal(3, evidence.StartLine);
        Assert.Contains("Authentication uses JWT bearer", evidence.Excerpt, StringComparison.Ordinal);
        Assert.True(answer.Metrics.ContextBudget.UsedCharacters <=
            answer.Metrics.ContextBudget.MaximumCharacters);
        string serialized = JsonSerializer.Serialize(new ProjectAnswerResponse(answer), ToolJson.Options);
        Assert.True(serialized.Length <= 16 * 1024, $"Serialized tool observation was {serialized.Length} characters.");
    }

    [Fact]
    public async Task ReindexReusesUnchangedFilesAndUpdatesOneChangedFile()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();
        ProjectIndexReport initial = await rig.Service.AnalyzeAsync(
            rig.RepositoryPath,
            CancellationToken.None);
        ProjectIndexReport unchanged = await rig.Service.AnalyzeAsync(
            rig.RepositoryPath,
            CancellationToken.None);
        string servicePath = Path.Combine(
            rig.RepositoryPath,
            "src",
            "Sample.Infrastructure",
            "OrderService.cs");
        await File.AppendAllTextAsync(servicePath, "\n// changed for incremental test\n");

        ProjectIndexReport changed = await rig.Service.AnalyzeAsync(
            rig.RepositoryPath,
            CancellationToken.None);

        Assert.False(initial.Incremental);
        Assert.True(unchanged.Incremental);
        Assert.Equal(0, unchanged.ChangedFiles);
        Assert.Equal(initial.SourceFileCount + initial.ProjectCount + 2, unchanged.UnchangedFiles);
        Assert.True(changed.Incremental);
        Assert.Equal(1, changed.ChangedFiles);
        Assert.NotEqual(initial.SnapshotId, changed.SnapshotId);
    }

    [Fact]
    public async Task DiscoveryExcludesBuildGeneratedAndCredentialFiles()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();
        Directory.CreateDirectory(Path.Combine(rig.RepositoryPath, "bin"));
        Directory.CreateDirectory(Path.Combine(rig.RepositoryPath, "build"));
        Directory.CreateDirectory(Path.Combine(rig.RepositoryPath, "generated"));
        await File.WriteAllTextAsync(Path.Combine(rig.RepositoryPath, "bin", "ignored.cs"), "SHOULD_NOT_INDEX");
        await File.WriteAllTextAsync(Path.Combine(rig.RepositoryPath, "build", "ignored.cs"), "SHOULD_NOT_INDEX");
        await File.WriteAllTextAsync(Path.Combine(rig.RepositoryPath, "generated", "ignored.cs"), "SHOULD_NOT_INDEX");
        await File.WriteAllTextAsync(Path.Combine(rig.RepositoryPath, ".env.test"), "SHOULD_NOT_INDEX");
        await File.WriteAllTextAsync(
            Path.Combine(rig.RepositoryPath, "appsettings.Local.json"),
            "{\"ConnectionStrings\":{\"Default\":\"Password=SHOULD_NOT_INDEX\"}}");
        string userSecrets = Path.Combine(rig.RepositoryPath, "UserSecrets");
        Directory.CreateDirectory(userSecrets);
        await File.WriteAllTextAsync(Path.Combine(userSecrets, "settings.json"), "SHOULD_NOT_INDEX");

        ProjectIndexReport report = await rig.Service.AnalyzeAsync(
            rig.RepositoryPath,
            CancellationToken.None);
        GroundedProjectAnswer search = await rig.Service.SearchAsync(
            rig.RepositoryPath,
            "SHOULD_NOT_INDEX",
            20,
            CancellationToken.None);

        Assert.Equal(7, report.SourceFileCount);
        Assert.Empty(search.Claims);
    }

    [Fact]
    public async Task DiscoveryIndexesClassicSolutionFilesAlongsideSlnx()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();
        await File.WriteAllTextAsync(
            Path.Combine(rig.RepositoryPath, "Legacy.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\n");

        await rig.Service.AnalyzeAsync(rig.RepositoryPath, CancellationToken.None);
        GroundedProjectAnswer answer = await rig.Service.SearchAsync(
            rig.RepositoryPath,
            "Legacy.sln",
            10,
            CancellationToken.None);

        Assert.Contains(answer.Claims, claim => claim.Evidence.Any(static evidence =>
            evidence.RelativePath == "Legacy.sln" && evidence.StartLine == 1));
    }

    [Fact]
    public async Task PartialDeclarationsProduceDistinctStableEvidence()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();
        string partialDirectory = Path.Combine(rig.RepositoryPath, "src", "Sample.Domain");
        await File.WriteAllTextAsync(
            Path.Combine(partialDirectory, "PartialA.cs"),
            "namespace Sample.Domain; public partial class PartialThing { public void Run() { } }");
        await File.WriteAllTextAsync(
            Path.Combine(partialDirectory, "PartialB.cs"),
            "namespace Sample.Domain; public partial class PartialThing { public void Stop() { } }");

        await rig.Service.AnalyzeAsync(rig.RepositoryPath, CancellationToken.None);
        GroundedProjectAnswer answer = await rig.Service.FindSymbolAsync(
            rig.RepositoryPath,
            "PartialThing",
            10,
            CancellationToken.None);

        ProjectClaim[] typeClaims = answer.Claims
            .Where(static claim => claim.Statement.Contains("PartialThing is declared in", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, typeClaims.Length);
        Assert.Equal(
            2,
            typeClaims.SelectMany(static claim => claim.Evidence)
                .Select(static evidence => evidence.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Fact]
    public async Task StaticProjectLoadingDoesNotExecuteMsBuildTargets()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();
        string marker = Path.Combine(rig.RootPath, "executed.txt");
        string projectPath = Path.Combine(
            rig.RepositoryPath,
            "src",
            "Sample.Domain",
            "Sample.Domain.csproj");
        string project = await File.ReadAllTextAsync(projectPath);
        project = project.Replace(
            "</Project>",
            $"<Target Name=\"Untrusted\" BeforeTargets=\"Build\"><Exec Command=\"cmd /c echo unsafe &gt; &quot;{marker}&quot;\" /></Target></Project>",
            StringComparison.Ordinal);
        await File.WriteAllTextAsync(projectPath, project);

        await rig.Service.AnalyzeAsync(rig.RepositoryPath, CancellationToken.None);

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task DtdInProjectFileFailsClosedWithoutResolvingExternalEntity()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();
        string projectPath = Path.Combine(
            rig.RepositoryPath,
            "src",
            "Sample.Domain",
            "Sample.Domain.csproj");
        await File.WriteAllTextAsync(
            projectPath,
            "<!DOCTYPE Project [<!ENTITY x SYSTEM 'file:///C:/Windows/win.ini'>]><Project><PropertyGroup><TargetFramework>&x;</TargetFramework></PropertyGroup></Project>");

        ProjectIndexException exception = await Assert.ThrowsAsync<ProjectIndexException>(async () =>
            await rig.Service.AnalyzeAsync(rig.RepositoryPath, CancellationToken.None));

        Assert.Equal("project_xml_invalid", exception.Code);
    }

    [Fact]
    public async Task CancellationStopsIndexingBeforeRepositoryAccessCompletes()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await rig.Service.AnalyzeAsync(rig.RepositoryPath, cancellation.Token));
    }

    [Fact]
    public async Task TextReadFailsIfFileGrowsBeyondTheValidatedBound()
    {
        string root = Path.Combine(
            Directory.GetCurrentDirectory(),
            ".jarvis-project-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "growing.cs");
        await File.WriteAllTextAsync(path, new string('x', 512));
        try
        {
            FileInfo info = new(path);
            DiscoveredFile discovered = new(
                path,
                "growing.cs",
                IndexedFileKind.Source,
                info.Length,
                info.LastWriteTimeUtc.Ticks);

            ProjectIndexException exception = await Assert.ThrowsAsync<ProjectIndexException>(async () =>
                await SafeRepositoryDiscovery.ReadTextAsync(
                    discovered,
                    maximumCharacters: 128,
                    CancellationToken.None));

            Assert.Equal("project_file_size_changed", exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task QueryBeforeAnalysisFailsWithStableCategory()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();

        ProjectIndexException exception = await Assert.ThrowsAsync<ProjectIndexException>(async () =>
            await rig.Service.GetOverviewAsync(rig.RepositoryPath, CancellationToken.None));

        Assert.Equal("project_not_indexed", exception.Code);
    }

    [Fact]
    public async Task QueryRejectsEvidenceAfterIndexedFileChanges()
    {
        await using ProjectRig rig = await ProjectRig.CreateAsync();
        await rig.Service.AnalyzeAsync(rig.RepositoryPath, CancellationToken.None);
        string sourcePath = Path.Combine(
            rig.RepositoryPath,
            "src",
            "Sample.Api",
            "Controllers",
            "OrdersController.cs");
        await File.AppendAllTextAsync(sourcePath, "\n// changed after indexing\n");

        ProjectIndexException exception = await Assert.ThrowsAsync<ProjectIndexException>(async () =>
            await rig.Service.FindSymbolAsync(
                rig.RepositoryPath,
                "OrdersController",
                10,
                CancellationToken.None));

        Assert.Equal("project_index_stale", exception.Code);
    }

    [Fact]
    public async Task WatcherDebouncesBurstIntoOneRefresh()
    {
        string root = Path.Combine(Path.GetTempPath(), "JarvisWatcherTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        TaskCompletionSource refresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshCount = 0;
        ProjectWatchManager manager = new(
            Options.Create(new ProjectIntelligenceOptions
            {
                WatchDebounceMilliseconds = 150,
                MaximumWatchedRepositories = 1,
            }),
            NullLogger<ProjectWatchManager>.Instance);
        try
        {
            manager.EnsureWatching(
                root,
                "TEST",
                _ =>
                {
                    Interlocked.Increment(ref refreshCount);
                    refresh.TrySetResult();
                    return ValueTask.CompletedTask;
                });
            string path = Path.Combine(root, "changed.cs");
            await File.WriteAllTextAsync(path, "one");
            await File.WriteAllTextAsync(path, "two");
            await File.WriteAllTextAsync(path, "three");

            await refresh.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(350);

            Assert.Equal(1, Volatile.Read(ref refreshCount));
        }
        finally
        {
            await manager.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ProjectRig : IAsyncDisposable
    {
        private readonly SqliteProjectIndexStore _store;

        private ProjectRig(
            string rootPath,
            string dataPath,
            string repositoryPath,
            SqliteProjectIndexStore store,
            ProjectIntelligenceService service)
        {
            RootPath = rootPath;
            DataPath = dataPath;
            RepositoryPath = repositoryPath;
            _store = store;
            Service = service;
        }

        public string RootPath { get; }

        public string DataPath { get; }

        public string RepositoryPath { get; }

        public ProjectIntelligenceService Service { get; }

        public static async ValueTask<ProjectRig> CreateAsync()
        {
            string root = Path.Combine(
                Directory.GetCurrentDirectory(),
                ".jarvis-project-tests",
                Guid.NewGuid().ToString("N"));
            string repository = Path.Combine(root, "repository");
            string data = Path.Combine(
                Path.GetTempPath(),
                "JarvisProjectIndexData",
                Guid.NewGuid().ToString("N"));
            string fixture = Path.Combine(
                AppContext.BaseDirectory,
                "TestData",
                "ProjectIntelligence",
                "SampleRepository");
            CopyDirectory(fixture, repository);
            Directory.CreateDirectory(Path.Combine(repository, ".git"));
            Directory.CreateDirectory(data);

            IOptions<ToolOptions> toolOptions = Options.Create(new ToolOptions
            {
                AllowedRoots = [repository],
            });
            IOptions<ProjectIntelligenceOptions> projectOptions = Options.Create(
                new ProjectIntelligenceOptions
                {
                    WatchDebounceMilliseconds = 30_000,
                    MaximumContextCharacters = 8_192,
                    MaximumExcerptCharacters = 1_024,
                });
            ToolPathPolicy pathPolicy = new(toolOptions);
            JarvisDataPaths paths = JarvisDataPaths.Create(data);
            SqliteProjectIndexStore store = new(paths);
            ProjectWatchManager watcher = new(
                projectOptions,
                NullLogger<ProjectWatchManager>.Instance);
            ProjectIntelligenceService service = new(
                pathPolicy,
                new SafeRepositoryDiscovery(projectOptions),
                new RoslynProjectAnalyzer(),
                store,
                new FakeGitMetadataReader(),
                watcher,
                projectOptions,
                TimeProvider.System,
                NullLogger<ProjectIntelligenceService>.Instance);
            await Task.CompletedTask;
            return new ProjectRig(root, data, repository, store, service);
        }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            _store.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }

            if (Directory.Exists(DataPath))
            {
                Directory.Delete(DataPath, recursive: true);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }

    private sealed class FakeGitMetadataReader : IGitRepositoryMetadataReader
    {
        public ValueTask<GitRepositoryMetadata> ReadAsync(
            string repositoryPath,
            CancellationToken cancellationToken)
        {
            _ = repositoryPath;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new GitRepositoryMetadata("main", "## main"));
        }
    }
}
