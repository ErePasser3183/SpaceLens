using System.ComponentModel;
using System.Diagnostics;

namespace SpaceLens.Tasks;

public sealed record ProcessSample(int Pid, long StartTicks, string Name, long WorkingSet, double? CpuPercent)
{
    public string CpuText => CpuPercent is double cpu ? $"{cpu:F1}%" : "—";
}
public sealed record ProcessSnapshot(IReadOnlyList<ProcessSample> Processes, int Unavailable, DateTimeOffset CapturedAt, SystemMemory? Memory = null);

// One owner calls Sample sequentially on a worker. No process handles survive a sample.
public sealed class ProcessSampler
{
    private sealed record Reading(long Start, long CpuTicks, long Timestamp);
    private Dictionary<int, Reading> previous = [];

    public ProcessSnapshot Sample()
    {
        var samples = new List<ProcessSample>();
        var next = new Dictionary<int, Reading>();
        int unavailable = 0;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    int pid = process.Id;
                    if (pid == 0) continue; // Idle time is not CPU consumption by a process.
                    string name = process.ProcessName;
                    long memory = Math.Max(0, process.WorkingSet64), start = 0;
                    double? cpu = null;
                    try
                    {
                        start = process.StartTime.ToUniversalTime().Ticks;
                        long ticks = process.TotalProcessorTime.Ticks, timestamp = Stopwatch.GetTimestamp();
                        if (previous.TryGetValue(pid, out var before))
                            cpu = CalculateCpu(before.Start, start, before.CpuTicks, ticks,
                                (timestamp - before.Timestamp) / (double)Stopwatch.Frequency, Environment.ProcessorCount);
                        next[pid] = new(start, ticks, timestamp);
                    }
                    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { unavailable++; }
                    samples.Add(new(pid, start, name, memory, cpu));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { unavailable++; }
            }
        }
        previous = next; // Exited processes and PID reuse cannot inherit an old CPU baseline.
        return new(samples, unavailable, DateTimeOffset.Now, SystemMemory.Read());
    }

    public static double? CalculateCpu(long oldStart, long start, long oldTicks, long ticks, double seconds, int processors)
        => oldStart != start || start == 0 || ticks < oldTicks || seconds <= 0 || processors <= 0
            ? null : Math.Clamp((ticks - oldTicks) / (double)TimeSpan.TicksPerSecond / seconds / processors * 100, 0, 100);
}
