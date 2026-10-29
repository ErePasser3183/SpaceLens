using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

[assembly: InternalsVisibleTo("SpaceLens.Tests")]

namespace SpaceLens.Tasks;

public sealed record MemoryOptimizationResult(SystemMemory? Before, SystemMemory? After, bool Skipped = false)
{
    public long? AvailableChange => Before is not null && After is not null
        ? checked((long)After.Available - (long)Before.Available) : null;
}

/// <summary>
/// Memory-list operations adapted from PCL Snapshot 2.99.0.0, PageOtherTest.MemoryOptimizeInternal,
/// by 龙腾猫跃 / PCL contributors. See docs/第三方参考.md.
/// Only act under Windows low-memory pressure; retain reusable standby pages.
/// </summary>
public static class MemoryOptimizer
{
    public const string HelperArgument = "--optimize-memory";
    internal const int SkippedExitCode = 10;
    private const int BusyExitCode = 170;

    public static async Task<MemoryOptimizationResult> OptimizeAsync()
    {
        var before = SystemMemory.Read();
        // No elevation or global trimming when Windows does not report low physical memory.
        if (!IsMemoryLow()) return new(before, before, Skipped: true);
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位 SpaceLens 程序。");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("请从 SpaceLens.exe 启动后使用内存优化。");
        // Only the short-lived helper is elevated. It accepts no paths or shell commands.
        using var helper = await Task.Run(() => Process.Start(new ProcessStartInfo(executable)
        {
            Arguments = HelperArgument, UseShellExecute = true, Verb = "runas",
            WorkingDirectory = AppContext.BaseDirectory, WindowStyle = ProcessWindowStyle.Hidden
        }) ?? throw new InvalidOperationException("无法启动内存优化。"));
        await helper.WaitForExitAsync();
        if (helper.ExitCode == SkippedExitCode) return new(before, SystemMemory.Read(), Skipped: true);
        if (helper.ExitCode == BusyExitCode) throw new InvalidOperationException("另一个窗口正在优化内存，请稍后再试。");
        if (helper.ExitCode != 0)
            throw new InvalidOperationException($"内存优化未完成（错误码 0x{helper.ExitCode:X8}）；部分步骤可能已执行。");
        return new(before, SystemMemory.Read());
    }

    // Return the original Win32 HRESULT / NTSTATUS to the normal process. No writable result file.
    public static int RunHelper()
    {
        try
        {
            using var gate = new Mutex(false, "Local\\SpaceLens.MemoryOptimization");
            bool acquired;
            try { acquired = gate.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return BusyExitCode;
            try
            {
                // Recheck after the UAC prompt, since memory may have recovered while waiting.
                if (!IsMemoryLow()) return SkippedExitCode;
                return WithPrivilege(() => RunCommands(command => NtSetSystemInformation(80, ref command, sizeof(int)), IsMemoryLow));
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or InvalidOperationException or System.Security.SecurityException)
        { return ex is Win32Exception native ? HResultFromWin32(native.NativeErrorCode) : ex.HResult; }
    }

    internal static int RunCommands(Func<int, int> execute, Func<bool> isMemoryLow)
    {
        if (!isMemoryLow()) return SkippedExitCode;
        // Keep standby pages cached: they already count as available physical memory.
        // Flush modified pages only if working-set trimming did not relieve pressure.
        for (int command = 2; command <= 3; command++)
        {
            int status = execute(command);
            if (status != 0) return status;
            if (command == 2 && !isMemoryLow()) return 0;
        }
        return 0;
    }

    internal static bool IsMemoryLow()
    {
        using var notification = CreateMemoryResourceNotification(0); // LowMemoryResourceNotification.
        if (notification.IsInvalid || !QueryMemoryResourceNotification(notification, out bool low))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取系统内存状态。");
        return low;
    }

    internal static int WithPrivilege(Func<int> action)
    {
        using var roleIdentity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(roleIdentity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new Win32Exception(5, "内存优化需要管理员权限。");
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.AdjustPrivileges);
        if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out long luid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
        if (!AdjustTokenPrivileges(identity.Token, false, ref state, Marshal.SizeOf<TokenPrivileges>(), out var previous, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        int error = Marshal.GetLastWin32Error(); // TRUE may still mean ERROR_NOT_ALL_ASSIGNED.
        try
        {
            if (error != 0) throw new Win32Exception(error);
            return action();
        }
        finally
        {
            if (previous.Count > 0)
                AdjustTokenPrivileges(identity.Token, false, ref previous, Marshal.SizeOf<TokenPrivileges>(), out _, out _);
        }
    }

    private static int HResultFromWin32(int error) => error <= 0 ? error : unchecked((int)(0x80070000u | (uint)(error & 0xFFFF)));

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct TokenPrivileges { public int Count; public long Luid; public int Attributes; }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges state, int length, out TokenPrivileges previous, out int returnedLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetSystemInformation(int informationClass, ref int command, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeWaitHandle CreateMemoryResourceNotification(int type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryMemoryResourceNotification(SafeWaitHandle notification, [MarshalAs(UnmanagedType.Bool)] out bool low);
}
