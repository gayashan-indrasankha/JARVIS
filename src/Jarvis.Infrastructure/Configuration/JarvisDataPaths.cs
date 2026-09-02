namespace Jarvis.Infrastructure.Configuration;

/// <summary>
/// Resolves all local runtime/model/data locations without embedding machine-specific paths.
/// </summary>
public sealed class JarvisDataPaths
{
    public const string HomeEnvironmentVariable = "JARVIS_HOME";

    private JarvisDataPaths(string root)
    {
        Root = root;
        Models = Path.Combine(root, "Models");
        LlmModels = Path.Combine(Models, "Llm");
        SpeechModels = Path.Combine(Models, "Speech");
        TtsModels = Path.Combine(Models, "Tts");
        VadModels = Path.Combine(Models, "Vad");
        WakeWordModels = Path.Combine(Models, "WakeWord");
        Runtime = Path.Combine(root, "Runtime");
        LlamaCppRuntime = Path.Combine(Runtime, "LlamaCpp");
        Data = Path.Combine(root, "Data");
        ProjectIndexes = Path.Combine(Data, "ProjectIntelligence");
        Logs = Path.Combine(root, "Logs");
        Cache = Path.Combine(root, "Cache");
    }

    public string Root { get; }

    public string Models { get; }

    public string LlmModels { get; }

    public string SpeechModels { get; }

    public string TtsModels { get; }

    public string VadModels { get; }

    public string WakeWordModels { get; }

    public string Runtime { get; }

    public string LlamaCppRuntime { get; }

    public string Data { get; }

    public string ProjectIndexes { get; }

    public string Logs { get; }

    public string Cache { get; }

    public static JarvisDataPaths Create(string? configuredHome = null)
    {
        string? candidate = configuredHome ?? Environment.GetEnvironmentVariable(HomeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(candidate))
        {
            string localApplicationData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new InvalidOperationException("The local application-data directory is unavailable.");
            }

            candidate = Path.Combine(localApplicationData, "JARVIS");
        }

        if (candidate.Any(char.IsControl) || !Path.IsPathFullyQualified(candidate))
        {
            throw new InvalidOperationException(
                "JARVIS_HOME must be a fully qualified local path without control characters.");
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (root.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("JARVIS_HOME must be on a local filesystem volume.");
        }

        string pathRoot = Path.GetPathRoot(root) ?? string.Empty;
        if (string.Equals(root, Path.TrimEndingDirectorySeparator(pathRoot), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("JARVIS_HOME must not be a filesystem root.");
        }

        EnsureNoReparsePoints(root);

        for (DirectoryInfo? directory = new(root); directory is not null; directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")))
            {
                throw new InvalidOperationException(
                    "JARVIS_HOME must be outside a Git working tree.");
            }
        }

        return new JarvisDataPaths(root);
    }

    public static string ResolveUnder(string directory, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath) || relativePath.Any(char.IsControl))
        {
            throw new InvalidOperationException("A JARVIS asset path must be relative and safe.");
        }

        string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string resolved = Path.GetFullPath(Path.Combine(fullDirectory, relativePath));
        string prefix = fullDirectory + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("A JARVIS asset path escaped its configured directory.");
        }


        EnsureNoReparsePoints(resolved);

        return resolved;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (FileSystemInfo? item = File.Exists(path)
                ? new FileInfo(path)
                : new DirectoryInfo(path);
            item is not null;
            item = item switch
            {
                FileInfo file => file.Directory,
                DirectoryInfo directory => directory.Parent,
                _ => null,
            })
        {
            if ((File.Exists(item.FullName) || Directory.Exists(item.FullName)) &&
                (File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "JARVIS data and asset paths must not traverse reparse points.");
            }
        }
    }
}
