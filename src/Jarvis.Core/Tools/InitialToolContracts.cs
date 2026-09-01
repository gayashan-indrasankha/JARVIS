namespace Jarvis.Core.Tools;

public sealed record ListDirectoryRequest(string Path, int MaximumEntries = 100) : IToolRequest;

public sealed record FileSystemEntry(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long? SizeBytes,
    DateTimeOffset LastWriteTimeUtc);

public sealed record ListDirectoryResponse(
    IReadOnlyList<FileSystemEntry> Entries,
    bool Truncated) : IToolResponse;

public sealed record FindFilesRequest(
    string Path,
    string Pattern,
    bool Recursive = true,
    int MaximumResults = 100) : IToolRequest;

public sealed record FoundFile(
    string Name,
    string RelativePath,
    long SizeBytes,
    DateTimeOffset LastWriteTimeUtc);

public sealed record FindFilesResponse(
    IReadOnlyList<FoundFile> Files,
    bool Truncated) : IToolResponse;

public sealed record GetFileMetadataRequest(string Path) : IToolRequest;

public sealed record GetFileMetadataResponse(
    string Name,
    string FullPath,
    bool IsDirectory,
    long? SizeBytes,
    DateTimeOffset CreationTimeUtc,
    DateTimeOffset LastWriteTimeUtc,
    string Extension) : IToolResponse;

public sealed record OpenFileRequest(string Path) : IToolRequest;

public sealed record OpenFileResponse(bool Opened) : IToolResponse;

public sealed record OpenFolderRequest(string Path) : IToolRequest;

public sealed record OpenFolderResponse(bool Opened) : IToolResponse;

public sealed record ReadTextFileRequest(
    string Path,
    int MaximumCharacters = 16 * 1024) : IToolRequest;

public sealed record ReadTextFileResponse(
    string Text,
    bool Truncated,
    long FileSizeBytes) : IToolResponse;

public enum LocalApplicationId
{
    Notepad,
    Calculator,
    Paint,
}

public sealed record LaunchApplicationRequest(LocalApplicationId Application) : IToolRequest;

public sealed record LaunchApplicationResponse(
    bool Started,
    int? ProcessId,
    string Application) : IToolResponse;

public sealed record ListProcessesRequest(int MaximumResults = 100) : IToolRequest;

public sealed record ProcessSummary(
    int ProcessId,
    string Name,
    long? WorkingSetBytes);

public sealed record ListProcessesResponse(
    IReadOnlyList<ProcessSummary> Processes,
    bool Truncated) : IToolResponse;

public sealed record GetSystemMetricsRequest : IToolRequest;

public sealed record GetSystemMetricsResponse(
    double CpuUsagePercent,
    ulong TotalPhysicalMemoryBytes,
    ulong AvailablePhysicalMemoryBytes,
    ulong UsedPhysicalMemoryBytes,
    long JarvisWorkingSetBytes) : IToolResponse;

public sealed record GetGitStatusRequest(string RepositoryPath) : IToolRequest;

public sealed record GetGitStatusResponse(
    string BranchSummary,
    IReadOnlyList<string> Changes,
    bool Truncated) : IToolResponse;

public enum SafeCommandId
{
    DotnetInfo,
    DotnetVersion,
    GitVersion,
}

public sealed record ExecuteSafeCommandRequest(SafeCommandId Command) : IToolRequest;

public sealed record ExecuteSafeCommandResponse(
    string Command,
    int ExitCode,
    string Output,
    bool Truncated) : IToolResponse;
