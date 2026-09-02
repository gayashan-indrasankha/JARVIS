using System.Collections.Concurrent;
using System.Diagnostics;
using Jarvis.Core.ProjectLearning;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.Voice.Local;
using Jarvis.Infrastructure.Voice.Local.Llama;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Tests.Voice;

public sealed class LlamaServerSupervisorTests
{
    [Fact]
    public async Task ManagedRuntimeUsesLoopbackEphemeralAuthAndStopsProcessTree()
    {
        const string unrelatedSecretName = "JARVIS_TEST_UNRELATED_SECRET";
        string? previousSecret = Environment.GetEnvironmentVariable(unrelatedSecretName);
        Environment.SetEnvironmentVariable(unrelatedSecretName, "must-not-reach-child");
        using TemporaryAssets temporary = new(includeModel: true);
        FakeProcessFactory processes = new();
        LlamaServerConnection connection;
        await using (LlamaServerSupervisor supervisor = CreateSupervisor(
            temporary,
            processes,
            new FakeHealthProbe(isReady: true)))
        {
            try
            {
                connection = await supervisor.EnsureReadyAsync(CancellationToken.None);
            }
            finally
            {
                Environment.SetEnvironmentVariable(unrelatedSecretName, previousSecret);
            }

            ProcessStartInfo start = Assert.Single(processes.Starts);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.True(start.RedirectStandardOutput);
            Assert.True(start.RedirectStandardError);
            Assert.Equal("127.0.0.1", ValueAfter(start, "--host"));
            Assert.Equal("18080", ValueAfter(start, "--port"));
            Assert.Equal("8192", ValueAfter(start, "--ctx-size"));
            Assert.Equal("24", ValueAfter(start, "--n-gpu-layers"));
            Assert.Equal("8", ValueAfter(start, "--threads"));
            Assert.Equal("1", ValueAfter(start, "--parallel"));
            Assert.Equal("off", ValueAfter(start, "--reasoning"));
            Assert.Contains("--offline", start.ArgumentList);
            Assert.Contains("--no-agent", start.ArgumentList);
            Assert.Contains("--no-webui-mcp-proxy", start.ArgumentList);
            Assert.Contains("--no-webui", start.ArgumentList);
            Assert.Contains("--no-slots", start.ArgumentList);
            Assert.Contains("--no-cors-credentials", start.ArgumentList);
            Assert.DoesNotContain(unrelatedSecretName, start.Environment.Keys);
            Assert.DoesNotContain("LLAMA_ARG_TOOLS", start.Environment.Keys);
            Assert.DoesNotContain("HTTPS_PROXY", start.Environment.Keys);
            Assert.Subset(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "PATH",
                    "SystemRoot",
                    "TEMP",
                    "TMP",
                    "WINDIR",
                    "LLAMA_API_KEY",
                },
                start.Environment.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(64, start.Environment["LLAMA_API_KEY"]!.Length);
            Assert.Equal(start.Environment["LLAMA_API_KEY"], connection.AuthenticationToken);
            Assert.DoesNotContain(connection.AuthenticationToken, start.ArgumentList);

            await supervisor.DisposeAsync();
        }

        Assert.True(Assert.Single(processes.Processes).WasKilled);
    }

    [Fact]
    public async Task FailedInitialProcessRetriesOnceAtFourThousandNinetySixContext()
    {
        using TemporaryAssets temporary = new(includeModel: true);
        FakeProcessFactory processes = new(initialExitStates: [true, false]);
        await using LlamaServerSupervisor supervisor = CreateSupervisor(
            temporary,
            processes,
            new FakeHealthProbe(isReady: true));

        LlamaServerConnection connection = await supervisor.EnsureReadyAsync(CancellationToken.None);

        Assert.Equal(4096, connection.ContextSize);
        Assert.Equal(2, processes.Starts.Count);
        Assert.Equal("8192", ValueAfter(processes.Starts[0], "--ctx-size"));
        Assert.Equal("4096", ValueAfter(processes.Starts[1], "--ctx-size"));
    }

    [Fact]
    public async Task CancellationKillsAStartingManagedProcess()
    {
        using TemporaryAssets temporary = new(includeModel: true);
        FakeProcessFactory processes = new();
        await using LlamaServerSupervisor supervisor = CreateSupervisor(
            temporary,
            processes,
            new FakeHealthProbe(isReady: false));
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await supervisor.EnsureReadyAsync(cancellation.Token));

        Assert.True(Assert.Single(processes.Processes).WasKilled);
    }

    [Fact]
    public async Task StartupTimeoutIsBoundedAndReturnsAStableFailure()
    {
        using TemporaryAssets temporary = new(includeModel: true);
        FakeProcessFactory processes = new();
        await using LlamaServerSupervisor supervisor = CreateSupervisor(
            temporary,
            processes,
            new FakeHealthProbe(isReady: false),
            contextSize: 4096,
            startupTimeoutSeconds: 1);

        LocalComponentUnavailableException exception = await Assert.ThrowsAsync<
            LocalComponentUnavailableException>(async () =>
                await supervisor.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal("local_llm_start_failed", exception.Code);
        Assert.True(Assert.Single(processes.Processes).WasKilled);
    }

    [Fact]
    public async Task UnexpectedExitCausesNextReadinessCheckToStartAFreshProcess()
    {
        using TemporaryAssets temporary = new(includeModel: true);
        FakeProcessFactory processes = new();
        await using LlamaServerSupervisor supervisor = CreateSupervisor(
            temporary,
            processes,
            new FakeHealthProbe(isReady: true));
        _ = await supervisor.EnsureReadyAsync(CancellationToken.None);

        Assert.Single(processes.Processes).ExitUnexpectedly();
        _ = await supervisor.EnsureReadyAsync(CancellationToken.None);

        Assert.Equal(2, processes.Starts.Count);
        Assert.False(processes.Processes[1].HasExited);
    }

    [Fact]
    public async Task MissingModelFailsWithActionableComponentCodeWithoutStartingProcess()
    {
        using TemporaryAssets temporary = new(includeModel: false);
        FakeProcessFactory processes = new();
        await using LlamaServerSupervisor supervisor = CreateSupervisor(
            temporary,
            processes,
            new FakeHealthProbe(isReady: true));

        LocalComponentUnavailableException exception = await Assert.ThrowsAsync<
            LocalComponentUnavailableException>(async () =>
                await supervisor.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal("language_model_not_installed", exception.Code);
        Assert.Contains("setup-local-ai.ps1", exception.Message, StringComparison.Ordinal);
        Assert.Empty(processes.Starts);
    }

    [Theory]
    [InlineData("failed to load model", "local_llm_model_load_failed")]
    [InlineData("address already in use", "local_llm_port_in_use")]
    public async Task NonResourceStartupFailuresReturnActionableCodeWithoutContextRetry(
        string diagnostic,
        string expectedCode)
    {
        using TemporaryAssets temporary = new(includeModel: true);
        FakeProcessFactory processes = new(
            initialExitStates: [true],
            startupDiagnostics: [diagnostic]);
        await using LlamaServerSupervisor supervisor = CreateSupervisor(
            temporary,
            processes,
            new FakeHealthProbe(isReady: true));

        LocalComponentUnavailableException exception = await Assert.ThrowsAsync<
            LocalComponentUnavailableException>(async () =>
                await supervisor.EnsureReadyAsync(CancellationToken.None));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Single(processes.Starts);
    }

    [Fact]
    public async Task ShutdownRemainsBoundedWhenProcessDoesNotAcknowledgeKill()
    {
        using TemporaryAssets temporary = new(includeModel: true);
        FakeProcessFactory processes = new(ignoreTermination: true);
        LlamaServerSupervisor supervisor = CreateSupervisor(
            temporary,
            processes,
            new FakeHealthProbe(isReady: true),
            shutdownTimeout: TimeSpan.FromMilliseconds(25));
        _ = await supervisor.EnsureReadyAsync(CancellationToken.None);

        await supervisor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(Assert.Single(processes.Processes).WasKilled);
    }

    [Fact]
    public async Task ProfileSwitchStopsFastAndStartsConfiguredDeepModelOnce()
    {
        using TemporaryAssets temporary = new(includeModel: true, includeDeepModel: true);
        FakeProcessFactory processes = new(initialExitStates: [false, false]);
        await using LlamaServerSupervisor supervisor = CreateSupervisor(
            temporary,
            processes,
            new FakeHealthProbe(isReady: true),
            deepEnabled: true);
        _ = await supervisor.SelectProfileAsync(ModelProfile.Fast, CancellationToken.None);

        LlamaServerConnection deep = await supervisor.SelectProfileAsync(
            ModelProfile.Deep,
            CancellationToken.None);
        LlamaServerConnection reused = await supervisor.SelectProfileAsync(
            ModelProfile.Deep,
            CancellationToken.None);

        Assert.Equal(ModelProfile.Deep, deep.Profile);
        Assert.Equal(deep, reused);
        Assert.Equal(2, processes.Starts.Count);
        Assert.True(processes.Processes[0].WasKilled);
        ProcessStartInfo start = processes.Starts[1];
        Assert.EndsWith("Qwen3-8B-Q4_K_M.gguf", ValueAfter(start, "--model"), StringComparison.Ordinal);
        Assert.Equal("6144", ValueAfter(start, "--ctx-size"));
        Assert.Equal("16", ValueAfter(start, "--n-gpu-layers"));
        Assert.Equal("8", ValueAfter(start, "--threads"));
        Assert.Equal("127.0.0.1", ValueAfter(start, "--host"));
    }

    private static LlamaServerSupervisor CreateSupervisor(
        TemporaryAssets temporary,
        FakeProcessFactory processes,
        ILlamaServerHealthProbe healthProbe,
        int contextSize = 8192,
        int startupTimeoutSeconds = 5,
        TimeSpan? shutdownTimeout = null,
        bool deepEnabled = false) =>
        new(
            Options.Create(new LocalAiOptions
            {
                ContextSize = contextSize,
                StartupTimeoutSeconds = startupTimeoutSeconds,
                Deep = new DeepModelOptions { Enabled = deepEnabled },
            }),
            new LocalAssetPaths(temporary.Paths),
            processes,
            healthProbe,
            NullLogger<LlamaServerSupervisor>.Instance,
            shutdownTimeout ?? TimeSpan.FromSeconds(1));

    private static string ValueAfter(ProcessStartInfo start, string option)
    {
        int index = start.ArgumentList.IndexOf(option);
        Assert.InRange(index, 0, start.ArgumentList.Count - 2);
        return start.ArgumentList[index + 1];
    }

    private sealed class FakeHealthProbe(bool isReady) : ILlamaServerHealthProbe
    {
        public ValueTask<bool> IsReadyAsync(
            LlamaServerConnection connection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(isReady);
        }
    }

    private sealed class FakeProcessFactory : IManagedProcessFactory
    {
        private readonly ConcurrentQueue<bool> _initialExitStates;
        private readonly ConcurrentQueue<string?> _startupDiagnostics;
        private readonly bool _ignoreTermination;

        public FakeProcessFactory(
            IEnumerable<bool>? initialExitStates = null,
            IEnumerable<string?>? startupDiagnostics = null,
            bool ignoreTermination = false)
        {
            _initialExitStates = new ConcurrentQueue<bool>(initialExitStates ?? [false]);
            _startupDiagnostics = new ConcurrentQueue<string?>(startupDiagnostics ?? [null]);
            _ignoreTermination = ignoreTermination;
        }

        public List<ProcessStartInfo> Starts { get; } = [];

        public List<FakeProcess> Processes { get; } = [];

        public IManagedProcess Start(ProcessStartInfo startInfo, Action<string> diagnosticSink)
        {
            Starts.Add(startInfo);
            _initialExitStates.TryDequeue(out bool initiallyExited);
            if (_startupDiagnostics.TryDequeue(out string? diagnostic) && diagnostic is not null)
            {
                diagnosticSink(diagnostic);
            }

            FakeProcess process = new(initiallyExited, _ignoreTermination);
            Processes.Add(process);
            return process;
        }
    }

    private sealed class FakeProcess : IManagedProcess
    {
        private readonly TaskCompletionSource _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly bool _ignoreTermination;

        public FakeProcess(bool initiallyExited, bool ignoreTermination)
        {
            _ignoreTermination = ignoreTermination;
            if (initiallyExited)
            {
                _exit.TrySetResult();
            }
        }

        public bool HasExited => _exit.Task.IsCompleted;

        public int? ExitCode => HasExited ? 0 : null;

        public bool WasKilled { get; private set; }

        public Task WaitForExitAsync(CancellationToken cancellationToken) =>
            _exit.Task.WaitAsync(cancellationToken);

        public void Kill()
        {
            if (!_exit.Task.IsCompleted)
            {
                WasKilled = true;
            }

            if (!_ignoreTermination)
            {
                _exit.TrySetResult();
            }
        }

        public void ExitUnexpectedly() => _exit.TrySetResult();

        public ValueTask DisposeAsync()
        {
            if (!_ignoreTermination)
            {
                _exit.TrySetResult();
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryAssets : IDisposable
    {
        public TemporaryAssets(bool includeModel, bool includeDeepModel = false)
        {
            string home = Path.Combine(
                Path.GetTempPath(),
                $"jarvis-llama-tests-{Guid.NewGuid():N}");
            Paths = JarvisDataPaths.Create(home);
            Directory.CreateDirectory(Paths.LlamaCppRuntime);
            Directory.CreateDirectory(Paths.LlmModels);
            File.WriteAllBytes(
                Path.Combine(Paths.LlamaCppRuntime, "llama-server.exe"),
                [0]);
            if (includeModel)
            {
                File.WriteAllBytes(
                    Path.Combine(Paths.LlmModels, "Qwen3-4B-Q4_K_M.gguf"),
                    [0]);
            }

            if (includeDeepModel)
            {
                File.WriteAllBytes(
                    Path.Combine(Paths.LlmModels, "Qwen3-8B-Q4_K_M.gguf"),
                    [0]);
            }
        }

        public JarvisDataPaths Paths { get; }

        public void Dispose()
        {
            if (Directory.Exists(Paths.Root))
            {
                Directory.Delete(Paths.Root, recursive: true);
            }
        }
    }
}
