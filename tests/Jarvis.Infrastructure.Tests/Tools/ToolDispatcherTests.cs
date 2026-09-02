using System.Text.Json;
using Jarvis.Core.Tools;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.ProjectIntelligence;
using Jarvis.Infrastructure.Tools;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Tests.Tools;

public sealed class ToolDispatcherTests
{
    [Fact]
    public async Task UnknownToolIsAuditedWithoutAuthorizationOrExecution()
    {
        using TemporaryDirectory temporary = new();
        ToolRig rig = new(temporary.Path);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("unknown_tool", "{}"),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.InvalidRequest, outcome.Status);
        Assert.Equal("unknown_tool", outcome.ErrorCategory);
        Assert.Equal(0, rig.Authorization.CallCount);
        ToolAuditEvent audit = Assert.Single(rig.Audit.Events);
        Assert.Equal(outcome.InvocationId, audit.InvocationId);
        Assert.False(audit.Succeeded);
    }

    [Fact]
    public async Task ExtraJsonPropertyFailsValidationBeforeAuthorization()
    {
        using TemporaryDirectory temporary = new();
        ToolRig rig = new(temporary.Path);
        string arguments = JsonSerializer.Serialize(new
        {
            path = temporary.Path,
            unexpected = true,
        });

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("list_directory", arguments),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.InvalidRequest, outcome.Status);
        Assert.Equal("malformed_arguments_json", outcome.ErrorCategory);
        Assert.Equal(0, rig.Authorization.CallCount);
        Assert.Single(rig.Audit.Events);
    }

    [Fact]
    public async Task DuplicateJsonPropertyFailsValidationBeforeAuthorization()
    {
        using TemporaryDirectory temporary = new();
        ToolRig rig = new(temporary.Path);
        string arguments = $"{{\"path\":{JsonSerializer.Serialize(temporary.Path)},\"path\":{JsonSerializer.Serialize(temporary.Path)}}}";

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("list_directory", arguments),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.InvalidRequest, outcome.Status);
        Assert.Equal("malformed_arguments_json", outcome.ErrorCategory);
        Assert.Equal(0, rig.Authorization.CallCount);
        Assert.Single(rig.Audit.Events);
    }

    [Fact]
    public async Task OutsideRootAndCredentialPathsAreRejectedBeforeAuthorization()
    {
        using TemporaryDirectory allowed = new();
        using TemporaryDirectory outside = new();
        Directory.CreateDirectory(Path.Combine(allowed.Path, ".ssh"));
        File.WriteAllText(Path.Combine(allowed.Path, ".ssh", "config"), "private");
        ToolRig rig = new(allowed.Path);

        ToolExecutionOutcome outsideOutcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("list_directory", Arguments(new { path = outside.Path })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);
        ToolExecutionOutcome credentialOutcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal(
                "read_text_file",
                Arguments(new { path = Path.Combine(allowed.Path, ".ssh", "config") })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal("path_outside_approved_roots", outsideOutcome.ErrorCategory);
        Assert.Equal("credential_path_denied", credentialOutcome.ErrorCategory);
        Assert.Equal(0, rig.Authorization.CallCount);
        Assert.Equal(2, rig.Audit.Events.Count);
    }

    [Fact]
    public void CredentialDirectoryCannotBeConfiguredAsAnApprovedRoot()
    {
        using TemporaryDirectory temporary = new();
        string credentialRoot = Path.Combine(temporary.Path, ".ssh");
        Directory.CreateDirectory(credentialRoot);
        IOptions<ToolOptions> options = Options.Create(new ToolOptions
        {
            AllowedRoots = [credentialRoot],
        });

        ToolValidationException exception = Assert.Throws<ToolValidationException>(
            () => new ToolPathPolicy(options));

        Assert.Equal("configured_root_sensitive", exception.Code);
    }

    [Fact]
    public async Task SafeDirectoryReadExecutesAfterAuthorizationAndFiltersSensitiveEntries()
    {
        using TemporaryDirectory temporary = new();
        File.WriteAllText(Path.Combine(temporary.Path, "README.md"), "safe");
        File.WriteAllText(Path.Combine(temporary.Path, ".env"), "SECRET=value");
        Directory.CreateDirectory(Path.Combine(temporary.Path, ".git"));
        ToolRig rig = new(temporary.Path);
        Guid userRequestId = Guid.NewGuid();

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("list_directory", Arguments(new { path = temporary.Path })),
            new ToolInvocationContext(userRequestId),
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Contains("README.md", outcome.Observation, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", outcome.Observation, StringComparison.Ordinal);
        Assert.DoesNotContain(".git", outcome.Observation, StringComparison.Ordinal);
        Assert.Equal(1, rig.Authorization.CallCount);
        ToolAuditEvent audit = Assert.Single(rig.Audit.Events);
        Assert.Equal(userRequestId, audit.UserRequestId);
        Assert.Equal(ToolAuthorizationDecision.Allowed, audit.AuthorizationDecision);
        Assert.True(audit.Succeeded);
        Assert.True(audit.EndedAt >= audit.StartedAt);
    }

    [Fact]
    public async Task RepeatedFingerprintIsBlockedBeforeSecondAuthorizationAndExecution()
    {
        using TemporaryDirectory temporary = new();
        CountingListDirectoryExecutor executor = new();
        ToolRig rig = new(temporary.Path, listDirectory: executor);
        ToolCallProposal proposal = new(
            "list_directory",
            Arguments(new { path = temporary.Path }));
        Guid requestId = Guid.NewGuid();

        ToolExecutionOutcome first = await rig.Dispatcher.ExecuteAsync(
            proposal,
            new ToolInvocationContext(requestId),
            CancellationToken.None);
        ToolExecutionOutcome second = await rig.Dispatcher.ExecuteAsync(
            proposal,
            new ToolInvocationContext(
                requestId,
                new HashSet<string>(StringComparer.Ordinal) { first.CanonicalFingerprint! }),
            CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal(ToolExecutionStatus.RepeatedCall, second.Status);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(1, rig.Authorization.CallCount);
        Assert.Equal(2, rig.Audit.Events.Count);
    }

    [Fact]
    public async Task DenialPreventsSafeLocalAction()
    {
        using TemporaryDirectory temporary = new();
        FakeWindowsActionLauncher launcher = new();
        ToolRig rig = new(
            temporary.Path,
            authorization: new RecordingAuthorizationPolicy(ToolAuthorizationDecision.Denied),
            launcher: launcher);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("open_folder", Arguments(new { path = temporary.Path })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Denied, outcome.Status);
        Assert.Equal(0, launcher.OpenCount);
        Assert.False(rig.Audit.Events.Single().Succeeded);
    }

    [Theory]
    [InlineData(ToolAuthorizationDecision.ConfirmationRequired)]
    [InlineData(ToolAuthorizationDecision.StrongConfirmationRequired)]
    public async Task ConfirmationDecisionPreventsExecution(
        ToolAuthorizationDecision decision)
    {
        using TemporaryDirectory temporary = new();
        FakeWindowsActionLauncher launcher = new();
        ToolRig rig = new(
            temporary.Path,
            authorization: new RecordingAuthorizationPolicy(decision),
            launcher: launcher);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("open_folder", Arguments(new { path = temporary.Path })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.ConfirmationRequired, outcome.Status);
        Assert.Equal(decision, outcome.AuthorizationDecision);
        Assert.Equal(0, launcher.OpenCount);
        Assert.False(rig.Audit.Events.Single().Succeeded);
    }

    [Fact]
    public async Task AllowedFolderOpenExecutesOnlyAfterAuthorizationAndIsAudited()
    {
        using TemporaryDirectory temporary = new();
        FakeWindowsActionLauncher launcher = new();
        ToolRig rig = new(temporary.Path, launcher: launcher);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("open_folder", Arguments(new { path = temporary.Path })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, rig.Authorization.CallCount);
        Assert.Equal(1, launcher.OpenCount);
        ToolAuditEvent audit = Assert.Single(rig.Audit.Events);
        Assert.Equal(ToolAuthorizationDecision.Allowed, audit.AuthorizationDecision);
        Assert.True(audit.Succeeded);
    }

    [Fact]
    public async Task UnconfirmedFolderOpenIsFailureAndIsNotReportedAsSuccess()
    {
        using TemporaryDirectory temporary = new();
        FakeWindowsActionLauncher launcher = new() { Confirm = false };
        ToolRig rig = new(temporary.Path, launcher: launcher);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("open_folder", Arguments(new { path = temporary.Path })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.Failed, outcome.Status);
        Assert.False(outcome.Succeeded);
        Assert.Equal(1, launcher.OpenCount);
        Assert.False(rig.Audit.Events.Single().Succeeded);
    }

    [Theory]
    [InlineData("untrusted.cmd")]
    [InlineData("untrusted.py")]
    [InlineData("untrusted.html")]
    [InlineData("untrusted.csproj")]
    public async Task OpenFileRejectsUnapprovedFileTypesBeforeAuthorization(string fileName)
    {
        using TemporaryDirectory temporary = new();
        string executable = Path.Combine(temporary.Path, fileName);
        File.WriteAllText(executable, "echo should-not-run");
        ToolRig rig = new(temporary.Path);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("open_file", Arguments(new { path = executable })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.InvalidRequest, outcome.Status);
        Assert.Equal("file_type_open_denied", outcome.ErrorCategory);
        Assert.Equal(0, rig.Authorization.CallCount);
    }

    [Theory]
    [InlineData("untrusted.cmd.")]
    [InlineData("untrusted.cmd ")]
    public async Task OpenFileRejectsAmbiguousWin32AliasesBeforeAuthorization(string requestedName)
    {
        using TemporaryDirectory temporary = new();
        string executable = Path.Combine(temporary.Path, "untrusted.cmd");
        File.WriteAllText(executable, "echo should-not-run");
        ToolRig rig = new(temporary.Path);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal(
                "open_file",
                Arguments(new { path = Path.Combine(temporary.Path, requestedName) })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.InvalidRequest, outcome.Status);
        Assert.Equal("ambiguous_windows_path_denied", outcome.ErrorCategory);
        Assert.Equal(0, rig.Authorization.CallCount);
        Assert.Equal(0, rig.Launcher.OpenCount);
    }

    [Fact]
    public async Task ReadFileRejectsAlternateDataStreamSyntaxBeforeAuthorization()
    {
        using TemporaryDirectory temporary = new();
        string file = Path.Combine(temporary.Path, "README.md");
        File.WriteAllText(file, "safe");
        ToolRig rig = new(temporary.Path);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal(
                "read_text_file",
                Arguments(new { path = file + ":private" })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.InvalidRequest, outcome.Status);
        Assert.Equal("alternate_data_stream_denied", outcome.ErrorCategory);
        Assert.Equal(0, rig.Authorization.CallCount);
    }

    [Fact]
    public async Task GitMetadataIndirectionIsRejectedBeforeAuthorization()
    {
        using TemporaryDirectory temporary = new();
        File.WriteAllText(Path.Combine(temporary.Path, ".git"), "gitdir: C:\\outside");
        ToolRig rig = new(temporary.Path);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal(
                "get_git_status",
                Arguments(new { repositoryPath = temporary.Path })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.InvalidRequest, outcome.Status);
        Assert.Equal("git_indirection_denied", outcome.ErrorCategory);
        Assert.Equal(0, rig.Authorization.CallCount);
    }

    [Fact]
    public async Task DeadlineCancelsExecutorAndAuditsTimeout()
    {
        using TemporaryDirectory temporary = new();
        BlockingListDirectoryExecutor executor = new();
        ToolRig rig = new(temporary.Path, listDirectory: executor, timeoutSeconds: 1);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("list_directory", Arguments(new { path = temporary.Path })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.TimedOut, outcome.Status);
        Assert.True(executor.CancellationObserved);
        Assert.True(rig.Audit.Events.Single().TimedOut);
    }

    [Fact]
    public async Task CallerCancellationStopsExecutionAndIsAudited()
    {
        using TemporaryDirectory temporary = new();
        BlockingListDirectoryExecutor executor = new();
        ToolRig rig = new(temporary.Path, listDirectory: executor, timeoutSeconds: 2);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("list_directory", Arguments(new { path = temporary.Path })),
            new ToolInvocationContext(Guid.NewGuid()),
            cancellation.Token);

        Assert.Equal(ToolExecutionStatus.Cancelled, outcome.Status);
        Assert.True(executor.CancellationObserved);
        ToolAuditEvent audit = Assert.Single(rig.Audit.Events);
        Assert.True(audit.Cancelled);
        Assert.False(audit.TimedOut);
    }

    [Fact]
    public async Task CentralResultLimitTruncatesOversizedTypedResponse()
    {
        using TemporaryDirectory temporary = new();
        OversizedListDirectoryExecutor executor = new();
        ToolRig rig = new(
            temporary.Path,
            listDirectory: executor,
            maximumResultCharacters: 1024);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("list_directory", Arguments(new { path = temporary.Path })),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Truncated);
        Assert.Equal(1024, outcome.Observation.Length);
        Assert.True(rig.Audit.Events.Single().ResultTruncated);
    }

    [Fact]
    public async Task SystemMetricsIsSafeReadAndProducesTypedObservation()
    {
        using TemporaryDirectory temporary = new();
        ToolRig rig = new(temporary.Path);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal("get_system_metrics", "{}"),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.True(outcome.Succeeded);
        Assert.Contains("cpuUsagePercent", outcome.Observation, StringComparison.Ordinal);
        Assert.Equal(ToolAuthorizationDecision.Allowed, outcome.AuthorizationDecision);
    }

    [Fact]
    public async Task ArbitraryShellCommandIsRejectedBeforeAuthorization()
    {
        using TemporaryDirectory temporary = new();
        ToolRig rig = new(temporary.Path);

        ToolExecutionOutcome outcome = await rig.Dispatcher.ExecuteAsync(
            new ToolCallProposal(
                "execute_safe_command",
                "{\"command\":\"powershell\",\"arguments\":[\"Remove-Item\"]}"),
            new ToolInvocationContext(Guid.NewGuid()),
            CancellationToken.None);

        Assert.Equal(ToolExecutionStatus.InvalidRequest, outcome.Status);
        Assert.Equal(0, rig.Authorization.CallCount);
    }

    [Theory]
    [InlineData(ToolAuthorizationCategory.SafeRead, ToolAuthorizationDecision.Allowed)]
    [InlineData(ToolAuthorizationCategory.SafeLocalAction, ToolAuthorizationDecision.Allowed)]
    [InlineData(ToolAuthorizationCategory.ConfirmRequired, ToolAuthorizationDecision.ConfirmationRequired)]
    [InlineData(ToolAuthorizationCategory.StrongConfirmRequired, ToolAuthorizationDecision.StrongConfirmationRequired)]
    [InlineData(ToolAuthorizationCategory.Denied, ToolAuthorizationDecision.Denied)]
    public async Task DefaultPolicyMapsEveryAuthorizationCategory(
        ToolAuthorizationCategory category,
        ToolAuthorizationDecision expected)
    {
        DefaultToolAuthorizationPolicy policy = new(Options.Create(new ToolOptions()));

        ToolAuthorizationResult result = await policy.AuthorizeAsync(
            new ToolAuthorizationRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "test_tool",
                category,
                "fingerprint"),
            CancellationToken.None);

        Assert.Equal(expected, result.Decision);
    }

    [Fact]
    public async Task DisabledDefaultPolicyDeniesSafeRead()
    {
        DefaultToolAuthorizationPolicy policy = new(Options.Create(new ToolOptions
        {
            Enabled = false,
        }));

        ToolAuthorizationResult result = await policy.AuthorizeAsync(
            new ToolAuthorizationRequest(
                Guid.NewGuid(),
                Guid.NewGuid(),
                "test_tool",
                ToolAuthorizationCategory.SafeRead,
                "fingerprint"),
            CancellationToken.None);

        Assert.Equal(ToolAuthorizationDecision.Denied, result.Decision);
        Assert.Equal("tools_disabled", result.ReasonCode);
    }

    private static string Arguments<T>(T value) => JsonSerializer.Serialize(value);

    private sealed class ToolRig
    {
        public ToolRig(
            string root,
            IToolExecutor<ListDirectoryRequest, ListDirectoryResponse>? listDirectory = null,
            RecordingAuthorizationPolicy? authorization = null,
            FakeWindowsActionLauncher? launcher = null,
            int timeoutSeconds = 2,
            int maximumResultCharacters = 16 * 1024)
        {
            ToolOptions toolOptions = new()
            {
                AllowedRoots = [root],
                DefaultTimeoutSeconds = timeoutSeconds,
                MaximumResultCharacters = maximumResultCharacters,
            };
            IOptions<ToolOptions> options = Options.Create(toolOptions);
            ToolPathPolicy pathPolicy = new(options);
            FakeWindowsActionLauncher actions = launcher ?? new FakeWindowsActionLauncher();
            FakeExecutableResolver executables = new();
            ToolRegistry registry = new(
                options,
                pathPolicy,
                listDirectory ?? new ListDirectoryTool(pathPolicy),
                new FindFilesTool(pathPolicy),
                new GetFileMetadataTool(pathPolicy),
                new OpenFileTool(pathPolicy, actions),
                new OpenFolderTool(pathPolicy, actions),
                new ReadTextFileTool(pathPolicy),
                new LaunchApplicationTool(actions),
                new ListProcessesTool(),
                new GetSystemMetricsTool(new FakeSystemMetricsProvider()),
                new GetGitStatusTool(pathPolicy, executables, new FakeProcessRunner()),
                new ExecuteSafeCommandTool(executables, new FakeProcessRunner()),
                Options.Create(new ProjectIntelligenceOptions()),
                new ProjectToolExecutors());
            Authorization = authorization ?? new RecordingAuthorizationPolicy(
                ToolAuthorizationDecision.Allowed);
            Audit = new RecordingAuditSink();
            Launcher = actions;
            Dispatcher = new ToolDispatcher(registry, Authorization, Audit, TimeProvider.System);
        }

        public ToolDispatcher Dispatcher { get; }

        public RecordingAuthorizationPolicy Authorization { get; }

        public RecordingAuditSink Audit { get; }

        public FakeWindowsActionLauncher Launcher { get; }
    }

    private sealed class RecordingAuthorizationPolicy(ToolAuthorizationDecision decision) :
        IToolAuthorizationPolicy
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<ToolAuthorizationResult> AuthorizeAsync(
            ToolAuthorizationRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(new ToolAuthorizationResult(
                decision,
                decision == ToolAuthorizationDecision.Allowed ? "allowed" : "denied"));
        }
    }

    private sealed class RecordingAuditSink : IToolAuditSink
    {
        public List<ToolAuditEvent> Events { get; } = [];

        public ValueTask RecordAsync(ToolAuditEvent auditEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingListDirectoryExecutor :
        IToolExecutor<ListDirectoryRequest, ListDirectoryResponse>
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public ValueTask<ListDirectoryResponse> ExecuteAsync(
            ListDirectoryRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _callCount);
            return ValueTask.FromResult(new ListDirectoryResponse([], Truncated: false));
        }
    }

    private sealed class BlockingListDirectoryExecutor :
        IToolExecutor<ListDirectoryRequest, ListDirectoryResponse>
    {
        public bool CancellationObserved { get; private set; }

        public async ValueTask<ListDirectoryResponse> ExecuteAsync(
            ListDirectoryRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class OversizedListDirectoryExecutor :
        IToolExecutor<ListDirectoryRequest, ListDirectoryResponse>
    {
        public ValueTask<ListDirectoryResponse> ExecuteAsync(
            ListDirectoryRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            FileSystemEntry entry = new(
                new string('x', 4096),
                "large",
                IsDirectory: false,
                1,
                DateTimeOffset.UnixEpoch);
            return ValueTask.FromResult(new ListDirectoryResponse([entry], Truncated: false));
        }
    }

    private sealed class FakeWindowsActionLauncher : IWindowsActionLauncher
    {
        private int _openCount;

        public int OpenCount => Volatile.Read(ref _openCount);

        public bool Confirm { get; init; } = true;

        public int? OpenPath(string path)
        {
            _ = path;
            Interlocked.Increment(ref _openCount);
            return Confirm ? 42 : null;
        }

        public int? Launch(LocalApplicationId application)
        {
            _ = application;
            return Confirm ? 42 : null;
        }
    }

    private sealed class FakeSystemMetricsProvider : ISystemMetricsProvider
    {
        public ValueTask<GetSystemMetricsResponse> GetAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new GetSystemMetricsResponse(10, 100, 50, 50, 10));
        }
    }

    private sealed class FakeProcessRunner : IBoundedProcessRunner
    {
        public ValueTask<BoundedProcessResult> RunAsync(
            BoundedProcessRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new BoundedProcessResult(0, string.Empty, string.Empty, false));
        }
    }

    private sealed class FakeExecutableResolver : ISafeExecutableResolver
    {
        public string Resolve(SafeExecutableId executable) => executable switch
        {
            SafeExecutableId.Dotnet => "C:\\trusted\\dotnet.exe",
            SafeExecutableId.Git => "C:\\trusted\\git.exe",
            _ => throw new InvalidOperationException(),
        };
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
