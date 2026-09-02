using Jarvis.Infrastructure.Tools;

namespace Jarvis.Infrastructure.ProjectIntelligence;

internal interface IGitRepositoryMetadataReader
{
    public ValueTask<GitRepositoryMetadata> ReadAsync(
        string repositoryPath,
        CancellationToken cancellationToken);
}

internal sealed class GitRepositoryMetadataReader(
    ISafeExecutableResolver executableResolver,
    IBoundedProcessRunner processRunner) : IGitRepositoryMetadataReader
{
    public async ValueTask<GitRepositoryMetadata> ReadAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        string git = executableResolver.Resolve(SafeExecutableId.Git);
        BoundedProcessResult result = await processRunner.RunAsync(
            new BoundedProcessRequest(
                git,
                [
                    "--git-dir",
                    Path.Combine(repositoryPath, ".git"),
                    "--work-tree",
                    repositoryPath,
                    "-c",
                    "core.fsmonitor=false",
                    "-c",
                    "core.untrackedCache=false",
                    "status",
                    "--short",
                    "--branch",
                    "--untracked-files=normal",
                    "--ignore-submodules=all",
                ],
                repositoryPath,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GIT_TERMINAL_PROMPT"] = "0",
                    ["GIT_OPTIONAL_LOCKS"] = "0",
                    ["GIT_CONFIG_GLOBAL"] = "NUL",
                    ["GIT_CONFIG_SYSTEM"] = "NUL",
                },
                MaximumOutputCharacters: 8 * 1024),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ProjectIndexException("git_metadata_failed");
        }

        string[] lines = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? branch = lines.FirstOrDefault(static line => line.StartsWith("## ", StringComparison.Ordinal));
        if (branch is not null)
        {
            branch = branch[3..].Split("...", StringSplitOptions.None)[0];
        }

        return new GitRepositoryMetadata(branch, result.StandardOutput.Trim());
    }
}
