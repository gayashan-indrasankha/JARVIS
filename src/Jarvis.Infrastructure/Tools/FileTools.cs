using System.IO.Enumeration;
using System.Text;
using Jarvis.Core.Tools;
using ToolFileSystemEntry = Jarvis.Core.Tools.FileSystemEntry;

namespace Jarvis.Infrastructure.Tools;

internal sealed class ListDirectoryTool(ToolPathPolicy pathPolicy) :
    IToolExecutor<ListDirectoryRequest, ListDirectoryResponse>
{
    public ValueTask<ListDirectoryResponse> ExecuteAsync(
        ListDirectoryRequest request,
        CancellationToken cancellationToken)
    {
        string path = pathPolicy.NormalizeExistingDirectory(request.Path);
        List<ToolFileSystemEntry> entries = [];
        bool truncated = false;
        EnumerationOptions options = new()
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
        };

        foreach (string entryPath in Directory.EnumerateFileSystemEntries(path, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ToolPathPolicy.IsSensitiveEntry(entryPath))
            {
                continue;
            }

            if (entries.Count >= request.MaximumEntries)
            {
                truncated = true;
                break;
            }

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(entryPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            FileSystemInfo info = isDirectory
                ? new DirectoryInfo(entryPath)
                : new FileInfo(entryPath);
            long? size = isDirectory ? null : TryGetLength((FileInfo)info);
            entries.Add(new ToolFileSystemEntry(
                info.Name,
                Path.GetRelativePath(path, entryPath),
                isDirectory,
                size,
                info.LastWriteTimeUtc));
        }

        entries.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));
        return ValueTask.FromResult(new ListDirectoryResponse(entries, truncated));
    }

    private static long? TryGetLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

internal sealed class FindFilesTool(ToolPathPolicy pathPolicy) :
    IToolExecutor<FindFilesRequest, FindFilesResponse>
{
    private const int MaximumVisitedDirectories = 2_048;

    public ValueTask<FindFilesResponse> ExecuteAsync(
        FindFilesRequest request,
        CancellationToken cancellationToken)
    {
        string root = pathPolicy.NormalizeExistingDirectory(request.Path);
        Queue<string> directories = new([root]);
        List<FoundFile> files = [];
        int visitedDirectories = 0;
        bool truncated = false;

        while (directories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = directories.Dequeue();
            if (++visitedDirectories > MaximumVisitedDirectories)
            {
                truncated = true;
                break;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ToolPathPolicy.IsSensitiveEntry(entry))
                {
                    continue;
                }

                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (request.Recursive)
                    {
                        directories.Enqueue(entry);
                    }

                    continue;
                }

                string name = Path.GetFileName(entry);
                if (!FileSystemName.MatchesSimpleExpression(
                    request.Pattern,
                    name,
                    ignoreCase: true))
                {
                    continue;
                }

                if (files.Count >= request.MaximumResults)
                {
                    truncated = true;
                    directories.Clear();
                    break;
                }

                FileInfo info = new(entry);
                try
                {
                    files.Add(new FoundFile(
                        info.Name,
                        Path.GetRelativePath(root, entry),
                        info.Length,
                        info.LastWriteTimeUtc));
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        files.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return ValueTask.FromResult(new FindFilesResponse(files, truncated));
    }
}

internal sealed class GetFileMetadataTool(ToolPathPolicy pathPolicy) :
    IToolExecutor<GetFileMetadataRequest, GetFileMetadataResponse>
{
    public ValueTask<GetFileMetadataResponse> ExecuteAsync(
        GetFileMetadataRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = pathPolicy.NormalizeExistingPath(request.Path);
        bool isDirectory = Directory.Exists(path);
        FileSystemInfo info = isDirectory
            ? new DirectoryInfo(path)
            : new FileInfo(path);
        return ValueTask.FromResult(new GetFileMetadataResponse(
            info.Name,
            info.FullName,
            isDirectory,
            isDirectory ? null : ((FileInfo)info).Length,
            info.CreationTimeUtc,
            info.LastWriteTimeUtc,
            info.Extension));
    }
}

internal sealed class ReadTextFileTool(ToolPathPolicy pathPolicy) :
    IToolExecutor<ReadTextFileRequest, ReadTextFileResponse>
{
    private const long MaximumReadableFileBytes = 1024 * 1024;

    public async ValueTask<ReadTextFileResponse> ExecuteAsync(
        ReadTextFileRequest request,
        CancellationToken cancellationToken)
    {
        string path = pathPolicy.NormalizeExistingFile(request.Path);
        FileInfo info = new(path);
        if (info.Length > MaximumReadableFileBytes)
        {
            throw new ToolValidationException("text_file_too_large");
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4 * 1024,
            leaveOpen: false);
        char[] buffer = new char[request.MaximumCharacters + 1];
        int read;
        try
        {
            read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DecoderFallbackException)
        {
            throw new ToolValidationException("file_is_not_supported_text");
        }

        bool truncated = read > request.MaximumCharacters;
        int length = Math.Min(read, request.MaximumCharacters);
        ReadOnlySpan<char> text = buffer.AsSpan(0, length);
        if (text.Contains('\0'))
        {
            throw new ToolValidationException("file_is_binary");
        }

        string sanitized = string.Create(
            length,
            text.ToString(),
            static (destination, source) =>
            {
                for (int index = 0; index < source.Length; index++)
                {
                    char character = source[index];
                    destination[index] = !char.IsControl(character) || character is '\r' or '\n' or '\t'
                        ? character
                        : '\uFFFD';
                }
            });
        return new ReadTextFileResponse(sanitized, truncated, info.Length);
    }
}
