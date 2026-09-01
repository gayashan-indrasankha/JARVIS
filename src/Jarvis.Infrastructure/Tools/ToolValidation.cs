using Jarvis.Core.Tools;
using Jarvis.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Jarvis.Infrastructure.Tools;

internal sealed class ToolValidationException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

internal sealed class ToolPathPolicy
{
    private static readonly HashSet<string> SensitiveSegments = new(
        [
            ".ssh",
            ".gnupg",
            ".aws",
            ".azure",
            ".kube",
            ".docker",
            ".password-store",
            ".git",
            "appdata",
            "credentials",
            "credential",
            "secrets",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SensitiveFileNames = new(
        [
            ".env",
            ".git-credentials",
            ".npmrc",
            ".pypirc",
            ".netrc",
            "_netrc",
            "credentials.json",
            "secrets.json",
            "nuget.config",
            "settings.xml",
            "gradle.properties",
            "id_rsa",
            "id_ed25519",
            "login data",
            "cookies",
            "web data",
            "local state",
            "key4.db",
            "logins.json",
            "ntuser.dat",
        ],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SensitiveExtensions = new(
        [".pfx", ".p12", ".pem", ".key", ".ppk", ".kdbx"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ExecutableDocumentExtensions = new(
        [
            ".exe",
            ".com",
            ".bat",
            ".cmd",
            ".ps1",
            ".psm1",
            ".vbs",
            ".vbe",
            ".js",
            ".jse",
            ".wsf",
            ".wsh",
            ".hta",
            ".msi",
            ".msp",
            ".scr",
            ".cpl",
            ".lnk",
            ".url",
            ".reg",
            ".jar",
            ".appref-ms",
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly string[] _roots;

    public ToolPathPolicy(IOptions<ToolOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _roots = options.Value.AllowedRoots
            .Select(NormalizeConfiguredRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public IReadOnlyList<string> ApprovedRoots => _roots;

    public string NormalizeExistingFile(string path) => Normalize(path, expectDirectory: false);

    public string NormalizeExistingPath(string path)
    {
        try
        {
            return NormalizeExistingFile(path);
        }
        catch (ToolValidationException exception) when (exception.Code == "file_not_found")
        {
            return NormalizeExistingDirectory(path);
        }
    }

    public string NormalizeOpenableDocument(string path)
    {
        string normalized = NormalizeExistingFile(path);
        if (ExecutableDocumentExtensions.Contains(Path.GetExtension(normalized)))
        {
            throw new ToolValidationException("executable_file_open_denied");
        }

        return normalized;
    }

    public string NormalizeExistingDirectory(string path) => Normalize(path, expectDirectory: true);

    public static bool IsSensitiveEntry(string path)
    {
        string name = Path.GetFileName(path);
        return SensitiveFileNames.Contains(name) ||
            SensitiveExtensions.Contains(Path.GetExtension(name)) ||
            SensitiveSegments.Contains(name);
    }

    private string Normalize(string path, bool expectDirectory)
    {
        ValidatePathText(path);
        if (_roots.Length == 0)
        {
            throw new ToolValidationException("path_root_not_configured");
        }

        string fullPath;
        try
        {
            if (Path.IsPathFullyQualified(path))
            {
                fullPath = Path.GetFullPath(path);
            }
            else if (_roots.Length == 1)
            {
                fullPath = Path.GetFullPath(path, _roots[0]);
            }
            else
            {
                throw new ToolValidationException("path_root_ambiguous");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolValidationException("path_invalid");
        }

        string? containingRoot = _roots.FirstOrDefault(root => IsWithinRoot(root, fullPath));
        if (containingRoot is null)
        {
            throw new ToolValidationException("path_outside_approved_roots");
        }

        if (ContainsSensitiveComponent(containingRoot, fullPath))
        {
            throw new ToolValidationException("credential_path_denied");
        }

        if (expectDirectory ? !Directory.Exists(fullPath) : !File.Exists(fullPath))
        {
            throw new ToolValidationException(expectDirectory ? "directory_not_found" : "file_not_found");
        }

        EnsureNoReparsePoints(containingRoot, fullPath);
        return fullPath;
    }

    private static string NormalizeConfiguredRoot(string root)
    {
        ValidatePathText(root);
        if (!Path.IsPathFullyQualified(root))
        {
            throw new ToolValidationException("configured_root_not_absolute");
        }

        string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string? pathRoot = Path.GetPathRoot(fullPath);
        if (pathRoot is null ||
            string.Equals(fullPath, Path.TrimEndingDirectorySeparator(pathRoot), StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(fullPath))
        {
            throw new ToolValidationException("configured_root_invalid");
        }

        if (ContainsSensitiveAbsoluteComponent(fullPath))
        {
            throw new ToolValidationException("configured_root_sensitive");
        }

        EnsureNoReparsePoints(fullPath, fullPath);
        return fullPath;
    }

    private static void ValidatePathText(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > ToolDataLimits.MaximumPathCharacters ||
            path.Any(char.IsControl))
        {
            throw new ToolValidationException("path_invalid");
        }
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        if (string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsSensitiveComponent(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (SensitiveSegments.Contains(segment) ||
                SensitiveFileNames.Contains(segment) ||
                SensitiveExtensions.Contains(Path.GetExtension(segment)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsSensitiveAbsoluteComponent(string path)
    {
        foreach (string segment in path.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (SensitiveSegments.Contains(segment) ||
                SensitiveFileNames.Contains(segment) ||
                SensitiveExtensions.Contains(Path.GetExtension(segment)))
            {
                return true;
            }
        }

        return false;
    }

    private static void EnsureNoReparsePoints(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        string current = root;
        Check(current);
        foreach (string segment in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            Check(current);
        }

        static void Check(string path)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                throw new ToolValidationException("path_unavailable");
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ToolValidationException("reparse_point_denied");
            }
        }
    }
}
