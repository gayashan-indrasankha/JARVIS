using System.Text;
using Jarvis.Infrastructure.Configuration;
using Jarvis.Infrastructure.Tools;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.ProjectIntelligence;

internal sealed class SafeRepositoryDiscovery(IOptions<ProjectIntelligenceOptions> options)
{
    private static readonly HashSet<string> ExcludedDirectories = new(
        [
            ".git",
            ".vs",
            ".idea",
            ".vscode",
            "bin",
            "obj",
            "artifacts",
            "build",
            "Debug",
            "Release",
            "packages",
            "node_modules",
            "TestResults",
            "coverage",
            "logs",
            "recordings",
            "generated",
            "Generated",
            "dist",
            "out",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> DocumentationExtensions = new(
        [".md", ".markdown", ".txt"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ConfigurationExtensions = new(
        [".json", ".jsonc", ".xml", ".config", ".props", ".targets", ".yml", ".yaml", ".toml"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly string[] SensitiveConfigurationMarkers =
    [
        "\"apikey\"",
        "\"clientsecret\"",
        "\"connectionstrings\"",
        "\"password\"",
        "\"privatekey\"",
        "\"refreshtoken\"",
        "aws_access_key_id",
        "aws_secret_access_key",
        "api_key:",
        "apikey:",
        "client_secret:",
        "connectionstrings:",
        "password:",
        "private_key:",
        "refresh_token:",
    ];

    private readonly ProjectIntelligenceOptions _options = options.Value;

    public ValueTask<IReadOnlyList<DiscoveredFile>> DiscoverAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        List<DiscoveredFile> files = [];
        long totalBytes = 0;
        Stack<string> directories = new();
        directories.Push(repositoryPath);

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = directories.Pop();
            string[] entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string entryName = Path.GetFileName(entry);
                if (ToolPathPolicy.IsSensitiveEntry(entry) ||
                    entryName.StartsWith(".env", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!ExcludedDirectories.Contains(Path.GetFileName(entry)))
                    {
                        directories.Push(entry);
                    }

                    continue;
                }

                if (!TryClassify(entry, out IndexedFileKind kind))
                {
                    continue;
                }

                FileInfo info;
                try
                {
                    info = new FileInfo(entry);
                    info.Refresh();
                    if (!info.Exists || info.Length < 0 || info.Length > _options.MaximumSourceFileBytes)
                    {
                        continue;
                    }
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or FileNotFoundException)
                {
                    continue;
                }

                if (files.Count >= _options.MaximumFiles)
                {
                    throw new ProjectIndexException("project_file_limit_exceeded");
                }

                totalBytes += info.Length;
                if (totalBytes > _options.MaximumTotalTextBytes)
                {
                    throw new ProjectIndexException("project_content_limit_exceeded");
                }

                files.Add(new DiscoveredFile(
                    info.FullName,
                    NormalizeRelativePath(repositoryPath, info.FullName),
                    kind,
                    info.Length,
                    info.LastWriteTimeUtc.Ticks));
            }
        }

        return ValueTask.FromResult<IReadOnlyList<DiscoveredFile>>(
            files.OrderBy(static file => file.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static async ValueTask<string> ReadTextAsync(
        DiscoveredFile file,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            file.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 16 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 16 * 1024,
            leaveOpen: false);
        try
        {
            char[] buffer = new char[16 * 1024];
            StringBuilder content = new(Math.Min(maximumCharacters, 64 * 1024));
            while (true)
            {
                int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (content.Length + read > maximumCharacters)
                {
                    throw new ProjectIndexException("project_file_size_changed");
                }

                content.Append(buffer, 0, read);
            }

            string value = content.ToString();
            if (value.Contains('\0', StringComparison.Ordinal))
            {
                throw new ProjectIndexException("project_file_not_text");
            }

            return value;
        }
        catch (DecoderFallbackException)
        {
            throw new ProjectIndexException("project_file_encoding_invalid");
        }
    }

    public static bool ContainsLikelySecretConfiguration(DiscoveredFile file, string content) =>
        file.Kind == IndexedFileKind.Configuration &&
        SensitiveConfigurationMarkers.Any(marker =>
            content.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static bool TryClassify(string path, out IndexedFileKind kind)
    {
        string extension = Path.GetExtension(path);
        string name = Path.GetFileName(path);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase) &&
            !name.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase))
        {
            kind = IndexedFileKind.Source;
            return true;
        }

        if (extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            kind = IndexedFileKind.Project;
            return true;
        }

        if (extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            kind = IndexedFileKind.Solution;
            return true;
        }

        if (DocumentationExtensions.Contains(extension))
        {
            kind = IndexedFileKind.Documentation;
            return true;
        }

        if (ConfigurationExtensions.Contains(extension) &&
            !name.Contains("secret", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("credential", StringComparison.OrdinalIgnoreCase))
        {
            kind = IndexedFileKind.Configuration;
            return true;
        }

        kind = default;
        return false;
    }

    private static string NormalizeRelativePath(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
}

internal sealed class ProjectIndexException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
