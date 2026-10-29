using System.Runtime.InteropServices;

namespace SpaceLens.Tasks;

public sealed record SystemMemory(ulong Total, ulong Available)
{
    public ulong Used => Total - Math.Min(Available, Total);
    public double UsedPercent => Total > 0 ? Used * 100d / Total : 0;

    internal static SystemMemory? Read()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status) && status.TotalPhysical > 0
            ? new(status.TotalPhysical, status.AvailablePhysical) : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
        public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
