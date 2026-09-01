using System.Diagnostics;
using System.Text;

namespace Jarvis.Infrastructure.Voice.Local.Llama;

internal interface IManagedProcessFactory
{
    public IManagedProcess Start(ProcessStartInfo startInfo, Action<string> diagnosticSink);
}

internal interface IManagedProcess : IAsyncDisposable
{
    public bool HasExited { get; }

    public int? ExitCode { get; }

    public Task WaitForExitAsync(CancellationToken cancellationToken);

    public void Kill();
}

internal sealed class SystemManagedProcessFactory : IManagedProcessFactory
{
    public IManagedProcess Start(ProcessStartInfo startInfo, Action<string> diagnosticSink)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(diagnosticSink);
        Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("The local inference process did not start.");
        return new SystemManagedProcess(process, diagnosticSink);
    }
}

internal sealed class SystemManagedProcess : IManagedProcess
{
    private const int MaximumDiagnosticCharacters = 4 * 1024;
    private const int DiagnosticOverlapCharacters = 64;
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);
    private readonly Process _process;
    private readonly Task _stdoutDrain;
    private readonly Task _stderrDrain;
    private int _disposed;

    public SystemManagedProcess(Process process, Action<string> diagnosticSink)
    {
        _process = process;
        _stdoutDrain = DrainAsync(process.StandardOutput, diagnosticSink);
        _stderrDrain = DrainAsync(process.StandardError, diagnosticSink);
    }

    public bool HasExited => _process.HasExited;

    public int? ExitCode => _process.HasExited ? _process.ExitCode : null;

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                Kill();
                using CancellationTokenSource timeout = new(ExitTimeout);
                try
                {
                    await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    // The supervisor reports the bounded cleanup failure. Never wait forever here.
                }
            }
        }
        finally
        {
            _process.Dispose();
        }

        try
        {
            await Task.WhenAll(_stdoutDrain, _stderrDrain)
                .WaitAsync(DrainTimeout)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Process streams are already closed; do not let a diagnostic drain block shutdown.
        }
    }

    internal static async Task DrainAsync(TextReader reader, Action<string> diagnosticSink)
    {
        try
        {
            char[] buffer = new char[1024];
            StringBuilder diagnostic = new(MaximumDiagnosticCharacters);
            while (true)
            {
                int read = await reader.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (int index = 0; index < read; index++)
                {
                    char character = buffer[index];
                    if (character == '\n')
                    {
                        EmitDiagnostic();
                        continue;
                    }

                    if (character != '\r')
                    {
                        diagnostic.Append(character);
                    }

                    if (diagnostic.Length >= MaximumDiagnosticCharacters)
                    {
                        diagnosticSink(diagnostic.ToString());
                        string overlap = diagnostic.ToString(
                            diagnostic.Length - DiagnosticOverlapCharacters,
                            DiagnosticOverlapCharacters);
                        diagnostic.Clear();
                        diagnostic.Append(overlap);
                    }
                }
            }

            EmitDiagnostic();

            void EmitDiagnostic()
            {
                if (diagnostic.Length > 0)
                {
                    diagnosticSink(diagnostic.ToString());
                    diagnostic.Clear();
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Expected if bounded shutdown closes a process stream before the drain completes.
        }
        catch (IOException)
        {
            // A terminated child may close redirected streams without a final line.
        }
    }
}
