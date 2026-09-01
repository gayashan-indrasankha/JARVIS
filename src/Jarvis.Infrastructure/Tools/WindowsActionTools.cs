using System.Diagnostics;
using Jarvis.Core.Tools;

namespace Jarvis.Infrastructure.Tools;

internal interface IWindowsActionLauncher
{
    public int? OpenPath(string path);

    public int? Launch(LocalApplicationId application);
}

internal sealed class WindowsActionLauncher : IWindowsActionLauncher
{
    public int? OpenPath(string path)
    {
        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
        return process?.Id;
    }

    public int? Launch(LocalApplicationId application)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrWhiteSpace(windows))
        {
            throw new InvalidOperationException("The Windows directory is unavailable.");
        }

        string executable = application switch
        {
            LocalApplicationId.Notepad => "notepad.exe",
            LocalApplicationId.Calculator => "calc.exe",
            LocalApplicationId.Paint => "mspaint.exe",
            _ => throw new ToolValidationException("application_not_allowed"),
        };
        string path = Path.Combine(windows, "System32", executable);
        if (!File.Exists(path))
        {
            throw new ToolValidationException("application_unavailable");
        }

        using Process? process = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = false,
            CreateNoWindow = false,
        });
        return process?.Id;
    }
}

internal sealed class OpenFileTool(
    ToolPathPolicy pathPolicy,
    IWindowsActionLauncher launcher) : IToolExecutor<OpenFileRequest, OpenFileResponse>
{
    public ValueTask<OpenFileResponse> ExecuteAsync(
        OpenFileRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = pathPolicy.NormalizeOpenableDocument(request.Path);
        if (launcher.OpenPath(path) is null)
        {
            throw new InvalidOperationException("Windows did not confirm that the document was opened.");
        }

        return ValueTask.FromResult(new OpenFileResponse(Opened: true));
    }
}

internal sealed class OpenFolderTool(
    ToolPathPolicy pathPolicy,
    IWindowsActionLauncher launcher) : IToolExecutor<OpenFolderRequest, OpenFolderResponse>
{
    public ValueTask<OpenFolderResponse> ExecuteAsync(
        OpenFolderRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = pathPolicy.NormalizeExistingDirectory(request.Path);
        if (launcher.OpenPath(path) is null)
        {
            throw new InvalidOperationException("Windows did not confirm that the folder was opened.");
        }

        return ValueTask.FromResult(new OpenFolderResponse(Opened: true));
    }
}

internal sealed class LaunchApplicationTool(IWindowsActionLauncher launcher) :
    IToolExecutor<LaunchApplicationRequest, LaunchApplicationResponse>
{
    public ValueTask<LaunchApplicationResponse> ExecuteAsync(
        LaunchApplicationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(request.Application))
        {
            throw new ToolValidationException("application_not_allowed");
        }

        int? processId = launcher.Launch(request.Application);
        if (processId is null)
        {
            throw new InvalidOperationException("Windows did not confirm that the application was launched.");
        }

        return ValueTask.FromResult(new LaunchApplicationResponse(
            Started: true,
            processId,
            request.Application.ToString()));
    }
}
