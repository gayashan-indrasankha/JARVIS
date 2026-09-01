using System.Diagnostics;
using System.Text;

namespace Jarvis.Infrastructure.Tools;

internal sealed record BoundedProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string>? AdditionalEnvironment = null,
    int MaximumOutputCharacters = 24 * 1024);

internal sealed record BoundedProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated);

internal interface IBoundedProcessRunner
{
    public ValueTask<BoundedProcessResult> RunAsync(
        BoundedProcessRequest request,
        CancellationToken cancellationToken);
}

internal sealed class BoundedProcessRunner : IBoundedProcessRunner
{
    public async ValueTask<BoundedProcessResult> RunAsync(
        BoundedProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        ConfigureMinimalEnvironment(startInfo, request.AdditionalEnvironment);
        using Process process = Process.Start(startInfo) ??
            throw new InvalidOperationException("The constrained process did not start.");
        Task<BoundedText> stdout = ReadBoundedAsync(
            process.StandardOutput,
            request.MaximumOutputCharacters);
        Task<BoundedText> stderr = ReadBoundedAsync(
            process.StandardError,
            request.MaximumOutputCharacters);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            throw;
        }

        BoundedText output = await stdout.ConfigureAwait(false);
        BoundedText error = await stderr.ConfigureAwait(false);
        return new BoundedProcessResult(
            process.ExitCode,
            output.Text,
            error.Text,
            output.Truncated || error.Truncated);
    }

    private static void ConfigureMinimalEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string>? additional)
    {
        string systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string temporaryDirectory = Path.GetTempPath();
        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(systemDirectory) ||
            string.IsNullOrWhiteSpace(windowsDirectory) ||
            string.IsNullOrWhiteSpace(temporaryDirectory) ||
            string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Required process runtime paths are unavailable.");
        }

        startInfo.Environment.Clear();
        startInfo.Environment["PATH"] = path;
        startInfo.Environment["SystemRoot"] = windowsDirectory;
        startInfo.Environment["WINDIR"] = windowsDirectory;
        startInfo.Environment["TEMP"] = temporaryDirectory;
        startInfo.Environment["TMP"] = temporaryDirectory;
        if (additional is null)
        {
            return;
        }

        foreach ((string key, string value) in additional)
        {
            startInfo.Environment[key] = value;
        }
    }

    private static async Task<BoundedText> ReadBoundedAsync(
        TextReader reader,
        int maximumCharacters)
    {
        char[] buffer = new char[2 * 1024];
        StringBuilder output = new(Math.Min(maximumCharacters, 4 * 1024));
        bool truncated = false;
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
                if (output.Length >= maximumCharacters)
                {
                    truncated = true;
                    continue;
                }

                output.Append(!char.IsControl(character) || character is '\r' or '\n' or '\t'
                    ? character
                    : '\uFFFD');
            }
        }

        return new BoundedText(output.ToString(), truncated);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private sealed record BoundedText(string Text, bool Truncated);
}
