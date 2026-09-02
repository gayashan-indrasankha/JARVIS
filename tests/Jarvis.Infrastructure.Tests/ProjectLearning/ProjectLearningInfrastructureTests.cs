using System.Runtime.CompilerServices;
using Jarvis.Core.ProjectIntelligence;
using Jarvis.Core.ProjectLearning;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.ProjectLearning;
using Jarvis.Infrastructure.Voice.Local;
using Jarvis.Infrastructure.Voice.Local.Llama;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Tests.ProjectLearning;

public sealed class ProjectLearningInfrastructureTests
{
    [Fact]
    public async Task SessionStoreRoundTripsCompletedInterviewAndFindsLatest()
    {
        using TemporaryHome temporary = new();
        ProjectLearningOptions options = new() { PersistSessions = true, MaximumPersistedSessions = 5 };
        using SqliteProjectLearningSessionStore store = new(
            JarvisDataPaths.Create(temporary.Path),
            Options.Create(options));
        ProjectLearningSessionSnapshot session = CreateSession(LearningSessionStatus.Completed);

        await store.SaveAsync(session, CancellationToken.None);
        ProjectLearningSessionSnapshot? loaded = await store.LoadAsync(
            session.SessionId,
            CancellationToken.None);
        ProjectLearningSessionSnapshot? latest = await store.LoadLatestCompletedInterviewAsync(
            session.RepositoryPath.ToUpperInvariant(),
            CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(session.SessionId, loaded.SessionId);
        Assert.Equal(session.Status, loaded.Status);
        Assert.Equal(session.Transcript, loaded.Transcript);
        Assert.Equal(session.Report!.RevisionTopics, loaded.Report!.RevisionTopics);
        Assert.Equal(session.SessionId, latest!.SessionId);
        Assert.True(File.Exists(Path.Combine(
            temporary.Path,
            "Data",
            "ProjectLearning",
            "project-learning.db")));
    }

    [Fact]
    public async Task SessionStoreRejectsUnboundedActiveSessionGrowth()
    {
        using TemporaryHome temporary = new();
        using SqliteProjectLearningSessionStore store = new(
            JarvisDataPaths.Create(temporary.Path),
            Options.Create(new ProjectLearningOptions
            {
                PersistSessions = true,
                MaximumPersistedSessions = 1,
            }));
        ProjectLearningSessionSnapshot first = CreateSession(LearningSessionStatus.Active);
        ProjectLearningSessionSnapshot second = CreateSession(LearningSessionStatus.Active);
        await store.SaveAsync(first, CancellationToken.None);

        ProjectLearningException exception = await Assert.ThrowsAsync<ProjectLearningException>(() =>
            store.SaveAsync(second, CancellationToken.None).AsTask());

        Assert.Equal("learning_session_capacity", exception.Code);
        Assert.NotNull(await store.LoadAsync(first.SessionId, CancellationToken.None));
        Assert.Null(await store.LoadAsync(second.SessionId, CancellationToken.None));
    }

    [Fact]
    public async Task DeepProfileUnavailableFallsBackToFastWithoutRetryingDeep()
    {
        using TemporaryHome temporary = new();
        JarvisDataPaths paths = JarvisDataPaths.Create(temporary.Path);
        Directory.CreateDirectory(paths.LlmModels);
        await File.WriteAllTextAsync(
            Path.Combine(paths.LlmModels, "Qwen3-8B-Q4_K_M.gguf"),
            "test",
            CancellationToken.None);
        FakeSupervisor supervisor = new(failDeep: true);
        await using LocalModelProfileRouter router = new(
            Options.Create(new LocalAiOptions
            {
                RuntimeMode = LocalAiRuntimeMode.Managed,
                Deep = new DeepModelOptions
                {
                    Enabled = true,
                    MinimumAvailableMemoryBytes = 1,
                },
            }),
            new LocalAssetPaths(paths),
            supervisor,
            new FakeMemory(ulong.MaxValue),
            NullLogger<LocalModelProfileRouter>.Instance);

        ModelProfileSelection selection = await router.BeginSessionAsync(
            ModelProfile.Deep,
            CancellationToken.None);

        Assert.True(selection.FellBack);
        Assert.Equal(ModelProfile.Fast, selection.Selected);
        Assert.Equal([ModelProfile.Deep, ModelProfile.Fast], supervisor.Selections);
    }

    [Fact]
    public async Task MissingDeepModelFallsBackWithoutStartingDeep()
    {
        using TemporaryHome temporary = new();
        JarvisDataPaths paths = JarvisDataPaths.Create(temporary.Path);
        FakeSupervisor supervisor = new(failDeep: false);
        await using LocalModelProfileRouter router = new(
            Options.Create(new LocalAiOptions
            {
                RuntimeMode = LocalAiRuntimeMode.Managed,
                Deep = new DeepModelOptions
                {
                    Enabled = true,
                    MinimumAvailableMemoryBytes = 1,
                },
            }),
            new LocalAssetPaths(paths),
            supervisor,
            new FakeMemory(ulong.MaxValue),
            NullLogger<LocalModelProfileRouter>.Instance);

        ModelProfileSelection selection = await router.BeginSessionAsync(
            ModelProfile.Deep,
            CancellationToken.None);

        Assert.True(selection.FellBack);
        Assert.Equal("deep_not_installed", selection.ReasonCode);
        Assert.Equal([ModelProfile.Fast], supervisor.Selections);
    }

    [Fact]
    public async Task InsufficientMemoryFallsBackBeforeStartingDeep()
    {
        using TemporaryHome temporary = new();
        JarvisDataPaths paths = JarvisDataPaths.Create(temporary.Path);
        Directory.CreateDirectory(paths.LlmModels);
        await File.WriteAllTextAsync(
            Path.Combine(paths.LlmModels, "Qwen3-8B-Q4_K_M.gguf"),
            "test",
            CancellationToken.None);
        FakeSupervisor supervisor = new(failDeep: false);
        await using LocalModelProfileRouter router = new(
            Options.Create(new LocalAiOptions
            {
                RuntimeMode = LocalAiRuntimeMode.Managed,
                Deep = new DeepModelOptions
                {
                    Enabled = true,
                    MinimumAvailableMemoryBytes = 8_000,
                },
            }),
            new LocalAssetPaths(paths),
            supervisor,
            new FakeMemory(7_999),
            NullLogger<LocalModelProfileRouter>.Instance);

        ModelProfileSelection selection = await router.BeginSessionAsync(
            ModelProfile.Deep,
            CancellationToken.None);

        Assert.True(selection.FellBack);
        Assert.Equal("deep_memory_insufficient", selection.ReasonCode);
        Assert.Equal([ModelProfile.Fast], supervisor.Selections);
    }

    [Fact]
    public async Task DeepProfileStaysSelectedForSessionThenReturnsToFast()
    {
        using TemporaryHome temporary = new();
        JarvisDataPaths paths = JarvisDataPaths.Create(temporary.Path);
        Directory.CreateDirectory(paths.LlmModels);
        await File.WriteAllTextAsync(
            Path.Combine(paths.LlmModels, "Qwen3-8B-Q4_K_M.gguf"),
            "test",
            CancellationToken.None);
        FakeSupervisor supervisor = new(failDeep: false);
        await using LocalModelProfileRouter router = new(
            Options.Create(new LocalAiOptions
            {
                RuntimeMode = LocalAiRuntimeMode.Managed,
                Deep = new DeepModelOptions
                {
                    Enabled = true,
                    MinimumAvailableMemoryBytes = 1,
                },
            }),
            new LocalAssetPaths(paths),
            supervisor,
            new FakeMemory(ulong.MaxValue),
            NullLogger<LocalModelProfileRouter>.Instance);

        ModelProfileSelection first = await router.BeginSessionAsync(
            ModelProfile.Deep,
            CancellationToken.None);
        ModelProfileSelection second = await router.BeginSessionAsync(
            ModelProfile.Deep,
            CancellationToken.None);
        await router.EndSessionAsync(CancellationToken.None);

        Assert.Equal(ModelProfile.Deep, first.Selected);
        Assert.Equal(ModelProfile.Deep, second.Selected);
        Assert.Equal([ModelProfile.Deep, ModelProfile.Fast], supervisor.Selections);
    }

    [Fact]
    public async Task LearningModelRepairsMalformedOutputExactlyOnce()
    {
        FakeLanguageModel languageModel = new(
            "not json",
            """{"explanation":"Grounded explanation","socraticQuestion":null,"expectedConcepts":["boundary"],"evidenceIndexes":[0],"observedStrengths":[],"observedGaps":[],"designAlternatives":[]}""");
        LlamaProjectLearningModel model = new(languageModel);
        ProjectClaim claim = CreateClaim();

        TutorGenerationOutput output = await model.GenerateTutorTurnAsync(
            new TutorGenerationRequest(
                TutorLevel.Architecture,
                TutorInteractionKind.Explain,
                "architecture",
                null,
                false,
                [claim],
                [],
                1_000),
            CancellationToken.None);

        Assert.Equal("Grounded explanation", output.Explanation);
        Assert.Equal(2, languageModel.Requests.Count);
        Assert.Contains(
            "untrusted data",
            languageModel.Requests[0].Messages[0].Text,
            StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectLearningSessionSnapshot CreateSession(LearningSessionStatus status)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ProjectLearningReport report = new(
            Guid.NewGuid(),
            [new LearningReportCategory("Project Knowledge", 2)],
            ["architecture"],
            ["security"],
            ["How is authorization enforced?"],
            ["security"],
            InterviewDifficulty.Junior,
            now);
        return new ProjectLearningSessionSnapshot(
            report.SessionId,
            LearningSessionKind.Interview,
            status,
            Path.Combine(Path.GetTempPath(), "sample-repository"),
            "snapshot-1",
            ModelProfile.Fast,
            ModelProfile.Fast,
            false,
            "selected",
            null,
            false,
            InterviewDifficulty.Internship,
            5,
            [],
            [],
            null,
            [new LearningTranscriptEntry(1, "assistant", "Question", now)],
            ["architecture"],
            ["security"],
            report,
            now,
            now);
    }

    private static ProjectClaim CreateClaim() => new(
        ProjectKnowledgeClassification.ProjectFact,
        "The host composes services.",
        [new ProjectEvidence("src/Program.cs", 10, 12, "Program", "services.Add...", "hash")]);

    private sealed class FakeSupervisor(bool failDeep) : ILlamaServerSupervisor
    {
        public List<ModelProfile> Selections { get; } = [];

        public ValueTask<LlamaServerConnection> EnsureReadyAsync(CancellationToken cancellationToken) =>
            SelectProfileAsync(ModelProfile.Fast, cancellationToken);

        public ValueTask<LlamaServerConnection> SelectProfileAsync(
            ModelProfile profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Selections.Add(profile);
            if (profile == ModelProfile.Deep && failDeep)
            {
                throw new LocalComponentUnavailableException("deep_failed", "Deep failed.");
            }

            return ValueTask.FromResult(new LlamaServerConnection(
                new Uri("http://127.0.0.1:18080/"),
                null,
                4_096,
                profile,
                profile == ModelProfile.Deep
                    ? LocalAssetPaths.SupportedDeepLanguageModelId
                    : LocalAssetPaths.SupportedLanguageModelId));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeMemory(ulong available) : IAvailablePhysicalMemoryProvider
    {
        public ValueTask<ulong> GetAvailableBytesAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(available);
        }
    }

    private sealed class FakeLanguageModel(params string[] outputs) : ILanguageModel
    {
        private readonly Queue<string> _outputs = new(outputs);

        public List<LanguageModelRequest> Requests { get; } = [];

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<LanguageModelToken> GenerateAsync(
            LanguageModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            yield return new LanguageModelToken(_outputs.Dequeue());
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TemporaryHome : IDisposable
    {
        public TemporaryHome()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "jarvis-learning-tests",
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
