using System.Diagnostics;
using System.Security.Cryptography;
using Jarvis.Core.Voice;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Voice.Local.Llama;

internal sealed record LlamaServerConnection(
    Uri Endpoint,
    string? AuthenticationToken,
    int ContextSize);

internal interface ILlamaServerHealthProbe
{
    public ValueTask<bool> IsReadyAsync(
        LlamaServerConnection connection,
        CancellationToken cancellationToken);
}

internal sealed class LlamaServerHealthProbe : ILlamaServerHealthProbe
{
    private static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(5);
    private readonly ILoopbackHttpClientFactory _httpClientFactory;
    private readonly TimeSpan _probeTimeout;

    public LlamaServerHealthProbe(ILoopbackHttpClientFactory httpClientFactory)
        : this(httpClientFactory, DefaultProbeTimeout)
    {
    }

    internal LlamaServerHealthProbe(
        ILoopbackHttpClientFactory httpClientFactory,
        TimeSpan probeTimeout)
    {
        _httpClientFactory = httpClientFactory;
        _probeTimeout = probeTimeout;
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(probeTimeout, TimeSpan.Zero);
    }

    public async ValueTask<bool> IsReadyAsync(
        LlamaServerConnection connection,
        CancellationToken cancellationToken)
    {
        using HttpClient client = _httpClientFactory.Create(
            connection.Endpoint,
            connection.AuthenticationToken);
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(_probeTimeout);
        try
        {
            using HttpResponseMessage response = await client
                .GetAsync("health", timeout.Token)
                .ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}

internal interface ILlamaServerSupervisor : IAsyncDisposable
{
    public ValueTask<LlamaServerConnection> EnsureReadyAsync(CancellationToken cancellationToken);
}

internal sealed class LlamaServerSupervisor : ILlamaServerSupervisor
{
    private const int FallbackContextSize = 4_096;
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HealthPollInterval = TimeSpan.FromMilliseconds(250);
    private readonly LocalAiOptions _options;
    private readonly LocalAssetPaths _assets;
    private readonly IManagedProcessFactory _processFactory;
    private readonly ILlamaServerHealthProbe _healthProbe;
    private readonly ILogger<LlamaServerSupervisor> _logger;
    private readonly TimeSpan _shutdownTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IManagedProcess? _process;
    private Task? _monitorTask;
    private LlamaServerConnection? _connection;
    private string _lastDiagnosticCode = "none";
    private bool _stopping;
    private bool _disposed;

    public LlamaServerSupervisor(
        IOptions<LocalAiOptions> options,
        LocalAssetPaths assets,
        IManagedProcessFactory processFactory,
        ILlamaServerHealthProbe healthProbe,
        ILogger<LlamaServerSupervisor> logger)
        : this(
            options,
            assets,
            processFactory,
            healthProbe,
            logger,
            DefaultShutdownTimeout)
    {
    }

    internal LlamaServerSupervisor(
        IOptions<LocalAiOptions> options,
        LocalAssetPaths assets,
        IManagedProcessFactory processFactory,
        ILlamaServerHealthProbe healthProbe,
        ILogger<LlamaServerSupervisor> logger,
        TimeSpan shutdownTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            shutdownTimeout,
            TimeSpan.Zero);
        _options = options.Value;
        _assets = assets;
        _processFactory = processFactory;
        _healthProbe = healthProbe;
        _logger = logger;
        _shutdownTimeout = shutdownTimeout;
    }

    public async ValueTask<LlamaServerConnection> EnsureReadyAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_options.Enabled)
        {
            throw new LocalComponentUnavailableException(
                "local_ai_disabled",
                "Local AI is disabled. Set LocalAi:Enabled to true before starting a session.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connection is not null &&
                (_options.RuntimeMode == LocalAiRuntimeMode.External ||
                    _process is { HasExited: false }))
            {
                return _connection;
            }

            Uri endpoint = LoopbackEndpoint.Create(_options.Host, _options.Port);
            if (_options.RuntimeMode == LocalAiRuntimeMode.External)
            {
                LlamaServerConnection external = new(endpoint, null, _options.ContextSize);
                if (!await _healthProbe.IsReadyAsync(external, cancellationToken).ConfigureAwait(false))
                {
                    throw new LocalComponentUnavailableException(
                        "local_llm_unavailable",
                        "The configured local llama server is unavailable on 127.0.0.1.");
                }

                _connection = external;
                LlamaRuntimeLog.ExternalReady(_logger, endpoint.Port);
                return external;
            }

            ValidateManagedAssets();
            int[] contexts = _options.ContextSize > FallbackContextSize
                ? [_options.ContextSize, FallbackContextSize]
                : [_options.ContextSize];

            foreach (int contextSize in contexts)
            {
                LlamaServerConnection connection = new(
                    endpoint,
                    CreateAuthenticationToken(),
                    contextSize);
                await StopProcessAsync().ConfigureAwait(false);
                _lastDiagnosticCode = "none";
                ProcessStartInfo startInfo = CreateStartInfo(connection);
                _stopping = false;
                _process = _processFactory.Start(startInfo, ClassifyDiagnostic);
                _monitorTask = MonitorProcessAsync(_process);
                LlamaRuntimeLog.Started(
                    _logger,
                    endpoint.Port,
                    contextSize,
                    _options.GpuLayers,
                    _options.Threads);

                if (await WaitUntilReadyAsync(connection, cancellationToken).ConfigureAwait(false))
                {
                    _connection = connection;
                    LlamaRuntimeLog.Ready(_logger, contextSize);
                    return connection;
                }

                await StopProcessAsync().ConfigureAwait(false);
                if (contextSize != contexts[^1] &&
                    _lastDiagnosticCode is not "model_load_failed" and not "port_in_use")
                {
                    LlamaRuntimeLog.ContextFallback(_logger, contextSize, FallbackContextSize);
                }
                else if (contextSize != contexts[^1])
                {
                    break;
                }
            }

            throw CreateStartupFailure();
        }
        catch (OperationCanceledException)
        {
            await StopProcessAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await StopProcessAsync().ConfigureAwait(false);
            _connection = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    internal ProcessStartInfo CreateStartInfo(LlamaServerConnection connection)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = _assets.LlamaServerExecutable,
            WorkingDirectory = Path.GetDirectoryName(_assets.LlamaServerExecutable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        AddArgument("--model", _assets.LanguageModel);
        AddArgument("--alias", LocalAssetPaths.SupportedLanguageModelId);
        AddArgument("--host", "127.0.0.1");
        AddArgument("--port", connection.Endpoint.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument("--ctx-size", connection.ContextSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument("--n-gpu-layers", _options.GpuLayers.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument("--threads", _options.Threads.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddArgument("--parallel", "1");
        AddArgument("--reasoning", "off");
        AddArgument("--reasoning-format", "deepseek");
        startInfo.ArgumentList.Add("--offline");
        startInfo.ArgumentList.Add("--no-agent");
        startInfo.ArgumentList.Add("--no-webui-mcp-proxy");
        startInfo.ArgumentList.Add("--no-webui");
        startInfo.ArgumentList.Add("--no-slots");
        startInfo.ArgumentList.Add("--cors-origins");
        startInfo.ArgumentList.Add("localhost");
        startInfo.ArgumentList.Add("--no-cors-credentials");
        ConfigureChildEnvironment(startInfo);
        startInfo.Environment["LLAMA_API_KEY"] = connection.AuthenticationToken;
        return startInfo;

        void AddArgument(string name, string value)
        {
            startInfo.ArgumentList.Add(name);
            startInfo.ArgumentList.Add(value);
        }
    }

    private static void ConfigureChildEnvironment(ProcessStartInfo startInfo)
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string temporaryDirectory = Path.GetTempPath();
        if (string.IsNullOrWhiteSpace(systemDirectory) ||
            string.IsNullOrWhiteSpace(windowsDirectory) ||
            string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            throw new InvalidOperationException(
                "Required Windows runtime directories are unavailable.");
        }

        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = systemDirectory;
        startInfo.Environment["SystemRoot"] = windowsDirectory;
        startInfo.Environment["TEMP"] = temporaryDirectory;
        startInfo.Environment["TMP"] = temporaryDirectory;
        startInfo.Environment["WINDIR"] = windowsDirectory;
    }

    private void ValidateManagedAssets()
    {
        if (!string.Equals(
            _options.ModelId,
            LocalAssetPaths.SupportedLanguageModelId,
            StringComparison.Ordinal))
        {
            throw new LocalComponentUnavailableException(
                "local_llm_model_unsupported",
                "The configured LocalAi:ModelId is not present in the tracked model manifest.");
        }

        if (!File.Exists(_assets.LlamaServerExecutable))
        {
            throw LocalAssetPaths.Missing("llama_runtime");
        }

        if (!File.Exists(_assets.LanguageModel))
        {
            throw LocalAssetPaths.Missing("language_model");
        }
    }

    private async ValueTask<bool> WaitUntilReadyAsync(
        LlamaServerConnection connection,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.StartupTimeoutSeconds));

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                if (_process is null || _process.HasExited)
                {
                    return false;
                }

                if (await _healthProbe.IsReadyAsync(connection, timeout.Token).ConfigureAwait(false))
                {
                    return true;
                }

                await Task.Delay(HealthPollInterval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (
            timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
        }

        cancellationToken.ThrowIfCancellationRequested();
        return false;
    }

    private async Task MonitorProcessAsync(IManagedProcess process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            if (!_stopping)
            {
                LlamaRuntimeLog.UnexpectedExit(
                    _logger,
                    process.ExitCode ?? -1,
                    _lastDiagnosticCode);
            }
        }
        catch (ObjectDisposedException) when (_stopping)
        {
        }
    }

    private async ValueTask StopProcessAsync()
    {
        IManagedProcess? process = _process;
        Task? monitor = _monitorTask;
        _process = null;
        _monitorTask = null;
        _connection = null;
        if (process is null)
        {
            return;
        }

        _stopping = true;
        try
        {
            try
            {
                process.Kill();
                using CancellationTokenSource timeout = new(_shutdownTimeout);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                LlamaRuntimeLog.ShutdownTimedOut(_logger);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                LlamaRuntimeLog.CleanupFailed(_logger, exception.GetType().Name);
            }

            try
            {
                await process.DisposeAsync()
                    .AsTask()
                    .WaitAsync(_shutdownTimeout)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LlamaRuntimeLog.ShutdownTimedOut(_logger);
            }

            if (monitor is not null)
            {
                try
                {
                    await monitor.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    LlamaRuntimeLog.ShutdownTimedOut(_logger);
                }
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            LlamaRuntimeLog.CleanupFailed(_logger, exception.GetType().Name);
        }
    }

    private LocalComponentUnavailableException CreateStartupFailure() =>
        _lastDiagnosticCode switch
        {
            "gpu_out_of_memory" => new LocalComponentUnavailableException(
                "local_llm_gpu_out_of_memory",
                "The local model did not fit in GPU memory. Reduce LocalAi:GpuLayers or use CPU mode."),
            "model_load_failed" => new LocalComponentUnavailableException(
                "local_llm_model_load_failed",
                "The local language model could not be loaded. Re-run local diagnostics and setup."),
            "port_in_use" => new LocalComponentUnavailableException(
                "local_llm_port_in_use",
                "The configured local inference port is already in use."),
            _ => new LocalComponentUnavailableException(
                "local_llm_start_failed",
                "The local llama runtime did not become healthy before the startup deadline."),
        };

    private void ClassifyDiagnostic(string line)
    {
        if (line.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("CUDA error", StringComparison.OrdinalIgnoreCase))
        {
            _lastDiagnosticCode = "gpu_out_of_memory";
        }
        else if (line.Contains("failed to load model", StringComparison.OrdinalIgnoreCase))
        {
            _lastDiagnosticCode = "model_load_failed";
        }
        else if (line.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
        {
            _lastDiagnosticCode = "port_in_use";
        }
    }

    private static string CreateAuthenticationToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}

internal static partial class LlamaRuntimeLog
{
    [LoggerMessage(EventId = 2300, Level = LogLevel.Information,
        Message = "Managed llama runtime started on loopback port {Port}; context {ContextSize}, GPU layers {GpuLayers}, threads {Threads}")]
    public static partial void Started(
        ILogger logger,
        int port,
        int contextSize,
        int gpuLayers,
        int threads);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Information,
        Message = "Local llama runtime is healthy with context {ContextSize}")]
    public static partial void Ready(ILogger logger, int contextSize);

    [LoggerMessage(EventId = 2302, Level = LogLevel.Warning,
        Message = "Local llama runtime exited unexpectedly with code {ExitCode}; diagnostic {DiagnosticCode}")]
    public static partial void UnexpectedExit(ILogger logger, int exitCode, string diagnosticCode);

    [LoggerMessage(EventId = 2303, Level = LogLevel.Warning,
        Message = "Local llama runtime failed at context {RequestedContext}; retrying once at {FallbackContext}")]
    public static partial void ContextFallback(
        ILogger logger,
        int requestedContext,
        int fallbackContext);

    [LoggerMessage(EventId = 2304, Level = LogLevel.Information,
        Message = "External local llama runtime is healthy on loopback port {Port}")]
    public static partial void ExternalReady(ILogger logger, int port);

    [LoggerMessage(EventId = 2305, Level = LogLevel.Warning,
        Message = "Managed llama runtime did not terminate within the shutdown deadline")]
    public static partial void ShutdownTimedOut(ILogger logger);

    [LoggerMessage(EventId = 2306, Level = LogLevel.Warning,
        Message = "Managed llama runtime cleanup reported {ErrorType}")]
    public static partial void CleanupFailed(ILogger logger, string errorType);
}
