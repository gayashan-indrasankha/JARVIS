using System.Collections.Concurrent;
using System.Threading.Channels;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.ProjectIntelligence;

internal sealed class ProjectWatchManager(
    IOptions<ProjectIntelligenceOptions> options,
    ILogger<ProjectWatchManager>? logger = null) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WatchRegistration> _registrations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ProjectIntelligenceOptions _options = options.Value;
    private readonly ILogger<ProjectWatchManager> _logger = logger ??
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ProjectWatchManager>.Instance;
    private int _disposed;

    public void EnsureWatching(
        string repositoryPath,
        string repositoryId,
        Func<CancellationToken, ValueTask> refresh)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(refresh);
        if (_registrations.ContainsKey(repositoryPath) ||
            _registrations.Count >= _options.MaximumWatchedRepositories)
        {
            return;
        }

        WatchRegistration registration = new(
            repositoryPath,
            repositoryId,
            TimeSpan.FromMilliseconds(_options.WatchDebounceMilliseconds),
            refresh,
            _logger);
        if (!_registrations.TryAdd(repositoryPath, registration))
        {
            registration.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        WatchRegistration[] registrations = _registrations.Values.ToArray();
        _registrations.Clear();
        foreach (WatchRegistration registration in registrations)
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed class WatchRegistration : IAsyncDisposable
    {
        private readonly string _repositoryId;
        private readonly TimeSpan _debounce;
        private readonly Func<CancellationToken, ValueTask> _refresh;
        private readonly ILogger _logger;
        private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        private readonly CancellationTokenSource _stopping = new();
        private readonly FileSystemWatcher _watcher;
        private readonly Task _pump;

        public WatchRegistration(
            string repositoryPath,
            string repositoryId,
            TimeSpan debounce,
            Func<CancellationToken, ValueTask> refresh,
            ILogger logger)
        {
            _repositoryId = repositoryId;
            _debounce = debounce;
            _refresh = refresh;
            _logger = logger;
            _watcher = new FileSystemWatcher(repositoryPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                    NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 16 * 1024,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.Renamed += OnRenamed;
            _watcher.Error += OnError;
            _watcher.EnableRaisingEvents = true;
            _pump = PumpAsync(_stopping.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnChanged;
            _watcher.Deleted -= OnChanged;
            _watcher.Renamed -= OnRenamed;
            _watcher.Error -= OnError;
            _watcher.Dispose();
            _stopping.Cancel();
            _signals.Writer.TryComplete();
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _stopping.Dispose();
        }

        private void OnChanged(object sender, FileSystemEventArgs eventArgs)
        {
            if (!ShouldIgnore(eventArgs.FullPath))
            {
                _signals.Writer.TryWrite(true);
            }
        }

        private void OnRenamed(object sender, RenamedEventArgs eventArgs) => OnChanged(sender, eventArgs);

        private void OnError(object sender, ErrorEventArgs eventArgs)
        {
            ProjectIntelligenceLog.WatcherOverflow(_logger, _repositoryId);
            _signals.Writer.TryWrite(true);
        }

        private async Task PumpAsync(CancellationToken cancellationToken)
        {
            await foreach (bool signal in _signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _ = signal;
                await Task.Delay(_debounce, cancellationToken).ConfigureAwait(false);
                while (_signals.Reader.TryRead(out bool _))
                {
                }

                try
                {
                    await _refresh(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (
                    exception is not OperationCanceledException and not OutOfMemoryException and
                        not StackOverflowException and not AccessViolationException and
                        not System.Runtime.InteropServices.SEHException)
                {
                    ProjectIntelligenceLog.RefreshFailed(
                        _logger,
                        _repositoryId,
                        exception switch
                        {
                            ProjectIndexException project => project.Code,
                            SqliteException => "project_index_storage_failed",
                            ToolValidationException validation => validation.Code,
                            InvalidOperationException => "project_refresh_invalid",
                            IOException or UnauthorizedAccessException => "io_failure",
                            _ => "project_refresh_failed",
                        });
                }
            }
        }

        private static bool ShouldIgnore(string path)
        {
            string[] ignored = [".git", "bin", "obj", ".vs", "TestResults"];
            return path.Split(
                    [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                    StringSplitOptions.RemoveEmptyEntries)
                .Any(segment => ignored.Contains(segment, StringComparer.OrdinalIgnoreCase));
        }
    }
}
