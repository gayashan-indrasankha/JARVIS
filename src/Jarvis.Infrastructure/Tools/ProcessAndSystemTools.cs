using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Jarvis.Core.Tools;

namespace Jarvis.Infrastructure.Tools;

internal sealed class ListProcessesTool :
    IToolExecutor<ListProcessesRequest, ListProcessesResponse>
{
    public ValueTask<ListProcessesResponse> ExecuteAsync(
        ListProcessesRequest request,
        CancellationToken cancellationToken)
    {
        List<ProcessSummary> summaries = [];
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (Process process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (summaries.Count >= request.MaximumResults)
                {
                    break;
                }

                try
                {
                    summaries.Add(new ProcessSummary(
                        process.Id,
                        SanitizeProcessName(process.ProcessName),
                        TryGetWorkingSet(process)));
                }
                catch (Exception exception) when (
                    exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        summaries.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));

        return ValueTask.FromResult(new ListProcessesResponse(
            summaries,
            Truncated: processes.Length > summaries.Count));
    }

    private static long? TryGetWorkingSet(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string SanitizeProcessName(string name) =>
        name.Length <= 128 && name.All(static character => !char.IsControl(character))
            ? name
            : "unavailable";
}

internal interface ISystemMetricsProvider
{
    public ValueTask<GetSystemMetricsResponse> GetAsync(CancellationToken cancellationToken);
}

internal sealed class WindowsSystemMetricsProvider : ISystemMetricsProvider
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(200);

    public async ValueTask<GetSystemMetricsResponse> GetAsync(CancellationToken cancellationToken)
    {
        SystemTimes first = ReadTimes();
        await Task.Delay(SampleInterval, cancellationToken).ConfigureAwait(false);
        SystemTimes second = ReadTimes();
        ulong idle = second.Idle - first.Idle;
        ulong kernel = second.Kernel - first.Kernel;
        ulong user = second.User - first.User;
        ulong total = kernel + user;
        double cpu = total == 0
            ? 0
            : Math.Clamp((double)(total - Math.Min(idle, total)) / total * 100, 0, 100);

        MemoryStatusEx memory = new()
        {
            Length = checked((uint)Marshal.SizeOf<MemoryStatusEx>()),
        };
        if (!GlobalMemoryStatusEx(ref memory))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        using Process current = Process.GetCurrentProcess();
        return new GetSystemMetricsResponse(
            Math.Round(cpu, 1),
            memory.TotalPhysical,
            memory.AvailablePhysical,
            memory.TotalPhysical - memory.AvailablePhysical,
            current.WorkingSet64);
    }

    private static SystemTimes ReadTimes()
    {
        if (!GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return new SystemTimes(ToUInt64(idle), ToUInt64(kernel), ToUInt64(user));
    }

    private static ulong ToUInt64(FILETIME time) =>
        ((ulong)(uint)time.dwHighDateTime << 32) | (uint)time.dwLowDateTime;

    private readonly record struct SystemTimes(ulong Idle, ulong Kernel, ulong User);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(
        out FILETIME idleTime,
        out FILETIME kernelTime,
        out FILETIME userTime);
}

internal sealed class GetSystemMetricsTool(ISystemMetricsProvider provider) :
    IToolExecutor<GetSystemMetricsRequest, GetSystemMetricsResponse>
{
    public ValueTask<GetSystemMetricsResponse> ExecuteAsync(
        GetSystemMetricsRequest request,
        CancellationToken cancellationToken)
    {
        _ = request;
        return provider.GetAsync(cancellationToken);
    }
}
