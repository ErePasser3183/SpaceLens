using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SpaceLens.Tasks;

public static class ProcessActions
{
    public sealed record EndResult(int Ended, IReadOnlyList<string> Failures);

    // Operate only on the identities captured when the menu was opened.
    public static EndResult EndMany(IReadOnlyList<ProcessSample> samples)
    {
        int ended = 0;
        var failures = new List<string>();
        foreach (var sample in samples.DistinctBy(p => (p.Pid, p.StartTicks)))
        {
            try { End(sample); ended++; }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
            { failures.Add($"{sample.Name}（PID {sample.Pid}）：{ex.Message}"); }
        }
        return new(ended, failures);
    }

    public static string ExecutablePath(ProcessSample sample)
    {
        using var process = OpenVerified(sample);
        return process.MainModule?.FileName ?? throw new InvalidOperationException("无法读取程序路径。");
    }

    public static void End(ProcessSample sample)
    {
        if (sample.Pid <= 4 || sample.Pid == Environment.ProcessId)
            throw new InvalidOperationException("不能结束系统进程或当前程序。");
        using var process = OpenVerified(sample);
        if (!IsProcessCritical(process.Handle, out bool critical) || critical)
            throw new InvalidOperationException("不能结束系统关键进程或无法确认安全状态的进程。");
        process.Kill(entireProcessTree: false);
    }

    private static Process OpenVerified(ProcessSample sample)
    {
        if (sample.StartTicks == 0) throw new InvalidOperationException("无法核对进程身份，请刷新后重试。");
        var process = Process.GetProcessById(sample.Pid);
        try
        {
            _ = process.Handle; // Keep the original process handle alive across identity check and action.
            if (process.StartTime.ToUniversalTime().Ticks != sample.StartTicks)
                throw new InvalidOperationException("原进程已退出，请刷新后重试。");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessCritical(nint handle, [MarshalAs(UnmanagedType.Bool)] out bool critical);
}
