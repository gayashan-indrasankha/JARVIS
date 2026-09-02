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

internal enum SafeExecutableId
{
    Dotnet,
    Git,
}

internal interface ISafeExecutableResolver
{
    public string Resolve(SafeExecutableId executable);
}

internal sealed class SafeExecutableResolver : ISafeExecutableResolver
{
    private readonly string _searchPath;

    public SafeExecutableResolver()
        : this(Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
    {
    }

    internal SafeExecutableResolver(string searchPath)
    {
        _searchPath = searchPath;
    }

    public string Resolve(SafeExecutableId executable)
    {
        string fileName = executable switch
        {
            SafeExecutableId.Dotnet => "dotnet.exe",
            SafeExecutableId.Git => "git.exe",
            _ => throw new ToolValidationException("safe_executable_not_allowed"),
        };

        foreach (string entry in _searchPath.Split(
            Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string directory = entry.Trim('"');
            if (!Path.IsPathFullyQualified(directory) ||
                directory.StartsWith("\\\\", StringComparison.Ordinal))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(Path.Combine(directory, fileName));
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (IsDirectLocalExecutable(candidate))
            {
                return candidate;
            }
        }

        throw new ToolValidationException("safe_executable_unavailable");
    }

    internal static bool IsDirectLocalExecutable(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) ||
                path.StartsWith("\\\\", StringComparison.Ordinal) ||
                !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(path))
            {
                return false;
            }

            string root = Path.GetPathRoot(path)!;
            if (new DriveInfo(root).DriveType == DriveType.Network)
            {
                return false;
            }

            string current = root;
            foreach (string segment in path[root.Length..].Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException or
                PathTooLongException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

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
        ValidateRequest(request);
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
        if (string.IsNullOrWhiteSpace(systemDirectory) ||
            string.IsNullOrWhiteSpace(windowsDirectory) ||
            string.IsNullOrWhiteSpace(temporaryDirectory))
        {
            throw new InvalidOperationException("Required process runtime paths are unavailable.");
        }

        startInfo.Environment.Clear();
        string executableDirectory = Path.GetDirectoryName(startInfo.FileName) ??
            throw new InvalidOperationException("The constrained executable path is invalid.");
        startInfo.Environment["PATH"] = string.Join(
            Path.PathSeparator,
            executableDirectory,
            systemDirectory);
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
            if (key is "PATH" or "SystemRoot" or "WINDIR" or "TEMP" or "TMP" ||
                string.IsNullOrWhiteSpace(key) ||
                key.Any(static character => character is '=' or '\0') ||
                value.Contains('\0'))
            {
                throw new InvalidOperationException("The constrained process environment is invalid.");
            }

            startInfo.Environment[key] = value;
        }
    }

    private static void ValidateRequest(BoundedProcessRequest request)
    {
        if (!SafeExecutableResolver.IsDirectLocalExecutable(request.FileName))
        {
            throw new InvalidOperationException("The constrained executable must be an existing direct path.");
        }

        if (request.Arguments.Count > 32 ||
            request.Arguments.Any(static argument =>
                argument.Length > 2_048 || argument.Any(char.IsControl)) ||
            request.MaximumOutputCharacters is < 256 or > 32 * 1024)
        {
            throw new InvalidOperationException("The constrained process request exceeds its limits.");
        }

        if (request.WorkingDirectory is not null &&
            (!Path.IsPathFullyQualified(request.WorkingDirectory) ||
                !Directory.Exists(request.WorkingDirectory)))
        {
            throw new InvalidOperationException("The constrained working directory is invalid.");
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
