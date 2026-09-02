using System.Runtime.InteropServices;
using Jarvis.Core.ProjectLearning;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.Voice.Local;
using Jarvis.Infrastructure.Voice.Local.Llama;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.ProjectLearning;

internal interface IAvailablePhysicalMemoryProvider
{
    public ValueTask<ulong> GetAvailableBytesAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsAvailablePhysicalMemoryProvider : IAvailablePhysicalMemoryProvider
{
    public ValueTask<ulong> GetAvailableBytesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MemoryStatusEx memory = new()
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()),
        };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return ValueTask.FromResult(memory.AvailablePhysical);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}

internal sealed class LocalModelProfileRouter : IModelProfileRouter, IAsyncDisposable
{
    private readonly LocalAiOptions _options;
    private readonly LocalAssetPaths _assets;
    private readonly ILlamaServerSupervisor _supervisor;
    private readonly IAvailablePhysicalMemoryProvider _memory;
    private readonly ILogger<LocalModelProfileRouter> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ModelProfile _activeProfile = ModelProfile.Fast;
    private bool _disposed;

    public LocalModelProfileRouter(
        IOptions<LocalAiOptions> options,
        LocalAssetPaths assets,
        ILlamaServerSupervisor supervisor,
        IAvailablePhysicalMemoryProvider memory,
        ILogger<LocalModelProfileRouter> logger)
    {
        _options = options.Value;
        _assets = assets;
        _supervisor = supervisor;
        _memory = memory;
        _logger = logger;
    }

    public async ValueTask<ModelProfileSelection> BeginSessionAsync(
        ModelProfile requestedProfile,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(requestedProfile))
        {
            throw new ArgumentOutOfRangeException(nameof(requestedProfile));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (requestedProfile == ModelProfile.Fast)
            {
                await EnsureFastAsync(cancellationToken).ConfigureAwait(false);
                return new ModelProfileSelection(
                    requestedProfile,
                    ModelProfile.Fast,
                    FellBack: false,
                    "fast_selected");
            }

            string? unavailable = await GetDeepUnavailableReasonAsync(cancellationToken)
                .ConfigureAwait(false);
            if (unavailable is not null)
            {
                await EnsureFastAsync(cancellationToken).ConfigureAwait(false);
                ProjectLearningLog.ProfileFallback(_logger, unavailable);
                return new ModelProfileSelection(
                    requestedProfile,
                    ModelProfile.Fast,
                    FellBack: true,
                    unavailable);
            }

            if (_activeProfile == ModelProfile.Deep)
            {
                return new ModelProfileSelection(
                    requestedProfile,
                    ModelProfile.Deep,
                    FellBack: false,
                    "deep_already_active");
            }

            try
            {
                await _supervisor.SelectProfileAsync(ModelProfile.Deep, cancellationToken)
                    .ConfigureAwait(false);
                _activeProfile = ModelProfile.Deep;
                ProjectLearningLog.ProfileSelected(_logger, "deep");
                return new ModelProfileSelection(
                    requestedProfile,
                    ModelProfile.Deep,
                    FellBack: false,
                    "deep_selected");
            }
            catch (Exception exception) when (
                exception is LocalComponentUnavailableException or IOException or
                    UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                await EnsureFastAsync(cancellationToken).ConfigureAwait(false);
                ProjectLearningLog.ProfileFallback(_logger, "deep_start_failed");
                return new ModelProfileSelection(
                    requestedProfile,
                    ModelProfile.Fast,
                    FellBack: true,
                    "deep_start_failed");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask EndSessionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeProfile == ModelProfile.Deep)
            {
                await EnsureFastAsync(cancellationToken).ConfigureAwait(false);
                ProjectLearningLog.ProfileSelected(_logger, "fast");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<string?> GetDeepUnavailableReasonAsync(
        CancellationToken cancellationToken)
    {
        if (!_options.Deep.Enabled)
        {
            return "deep_disabled";
        }

        if (_options.RuntimeMode != LocalAiRuntimeMode.Managed)
        {
            return "deep_external_unsupported";
        }

        if (!File.Exists(_assets.DeepLanguageModel))
        {
            return "deep_not_installed";
        }

        ulong available = await _memory.GetAvailableBytesAsync(cancellationToken)
            .ConfigureAwait(false);
        return available < checked((ulong)_options.Deep.MinimumAvailableMemoryBytes)
            ? "deep_memory_insufficient"
            : null;
    }

    private async ValueTask EnsureFastAsync(CancellationToken cancellationToken)
    {
        await _supervisor.SelectProfileAsync(ModelProfile.Fast, cancellationToken)
            .ConfigureAwait(false);
        _activeProfile = ModelProfile.Fast;
    }
}
