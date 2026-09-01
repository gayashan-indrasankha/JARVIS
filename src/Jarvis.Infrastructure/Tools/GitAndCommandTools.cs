using Jarvis.Core.Tools;

namespace Jarvis.Infrastructure.Tools;

internal sealed class GetGitStatusTool(
    ToolPathPolicy pathPolicy,
    IBoundedProcessRunner processRunner) :
    IToolExecutor<GetGitStatusRequest, GetGitStatusResponse>
{
    private const int MaximumChanges = 200;

    public async ValueTask<GetGitStatusResponse> ExecuteAsync(
        GetGitStatusRequest request,
        CancellationToken cancellationToken)
    {
        string repository = pathPolicy.NormalizeExistingDirectory(request.RepositoryPath);
        if (!Directory.Exists(Path.Combine(repository, ".git")) &&
            !File.Exists(Path.Combine(repository, ".git")))
        {
            throw new ToolValidationException("not_git_repository");
        }

        BoundedProcessResult result = await processRunner.RunAsync(
            new BoundedProcessRequest(
                "git",
                [
                    "-c",
                    "core.fsmonitor=false",
                    "-c",
                    "core.untrackedCache=false",
                    "-C",
                    repository,
                    "status",
                    "--short",
                    "--branch",
                    "--untracked-files=normal",
                ],
                AdditionalEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GIT_TERMINAL_PROMPT"] = "0",
                    ["GIT_OPTIONAL_LOCKS"] = "0",
                    ["GIT_CONFIG_GLOBAL"] = "NUL",
                    ["GIT_CONFIG_SYSTEM"] = "NUL",
                }),
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ToolValidationException("git_status_failed");
        }

        string[] lines = result.StandardOutput.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string branch = lines.FirstOrDefault(static line => line.StartsWith("## ", StringComparison.Ordinal))
            ?? "branch unavailable";
        string[] changes = lines
            .Where(static line => !line.StartsWith("## ", StringComparison.Ordinal))
            .Take(MaximumChanges)
            .ToArray();
        return new GetGitStatusResponse(
            branch,
            changes,
            result.OutputTruncated || lines.Length - 1 > changes.Length);
    }
}

internal sealed class ExecuteSafeCommandTool(IBoundedProcessRunner processRunner) :
    IToolExecutor<ExecuteSafeCommandRequest, ExecuteSafeCommandResponse>
{
    public async ValueTask<ExecuteSafeCommandResponse> ExecuteAsync(
        ExecuteSafeCommandRequest request,
        CancellationToken cancellationToken)
    {
        (string executable, string[] arguments, string name) = request.Command switch
        {
            SafeCommandId.DotnetInfo => ("dotnet", new[] { "--info" }, "dotnet_info"),
            SafeCommandId.DotnetVersion => ("dotnet", new[] { "--version" }, "dotnet_version"),
            SafeCommandId.GitVersion => ("git", new[] { "--version" }, "git_version"),
            _ => throw new ToolValidationException("safe_command_not_allowed"),
        };
        BoundedProcessResult result = await processRunner.RunAsync(
            new BoundedProcessRequest(
                executable,
                arguments,
                AdditionalEnvironment: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
                    ["DOTNET_NOLOGO"] = "1",
                    ["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1",
                    ["GIT_TERMINAL_PROMPT"] = "0",
                    ["GIT_CONFIG_GLOBAL"] = "NUL",
                    ["GIT_CONFIG_SYSTEM"] = "NUL",
                }),
            cancellationToken).ConfigureAwait(false);
        string output = string.IsNullOrWhiteSpace(result.StandardOutput)
            ? result.StandardError
            : result.StandardOutput;
        return new ExecuteSafeCommandResponse(
            name,
            result.ExitCode,
            output.Trim(),
            result.OutputTruncated);
    }
}
