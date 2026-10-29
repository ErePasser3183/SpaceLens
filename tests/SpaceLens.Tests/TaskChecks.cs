using System.Diagnostics;
using SpaceLens.Tasks;

internal static class TaskChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        var memoryCommands = new List<int>();
        check(MemoryOptimizer.RunCommands(command => { memoryCommands.Add(command); return 0; }, () => true) == 0 && memoryCommands.SequenceEqual(new[] { 2, 3 }),
            "persistent pressure trims then flushes without purging reusable standby memory");
        foreach (int failedCommand in new[] { 2, 3 })
        {
            memoryCommands.Clear();
            int error = unchecked((int)0xC0000061);
            int result = MemoryOptimizer.RunCommands(command => { memoryCommands.Add(command); return command == failedCommand ? error : 0; }, () => true);
            check(result == error && memoryCommands.SequenceEqual(Enumerable.Range(2, failedCommand - 1)),
                $"memory operation {failedCommand} failure preserves native status and stops later commands");
        }
        memoryCommands.Clear();
        check(MemoryOptimizer.RunCommands(command => { memoryCommands.Add(command); return 0; }, () => false) == MemoryOptimizer.SkippedExitCode && memoryCommands.Count == 0,
            "healthy memory skips all global operations");
        int pressureReads = 0;
        check(MemoryOptimizer.RunCommands(command => { memoryCommands.Add(command); return 0; }, () => ++pressureReads == 1) == 0 && memoryCommands.SequenceEqual(new[] { 2 }),
            "pressure recovery after trimming avoids forced modified-page writes");
        memoryCommands.Clear();
        bool queryError = false;
        try { MemoryOptimizer.RunCommands(command => { memoryCommands.Add(command); return 0; }, () => throw new System.ComponentModel.Win32Exception(6)); }
        catch (System.ComponentModel.Win32Exception) { queryError = true; }
        check(queryError && memoryCommands.Count == 0, "unreadable memory pressure never triggers blind cleanup");
        _ = MemoryOptimizer.IsMemoryLow();
        check(true, "live Windows low-memory notification can be read without modifying memory");
        using (var gate = new Mutex(false, "Local\\SpaceLens.MemoryOptimization"))
        {
            if (!gate.WaitOne(0)) throw new InvalidOperationException("Another memory optimization is active during verification.");
            try
            {
                int helperExit = -1;
                var competingHelper = new Thread(() => helperExit = MemoryOptimizer.RunHelper());
                competingHelper.Start();
                check(competingHelper.Join(5000) && helperExit == 170, "concurrent helper exits before any memory operation");
            }
            finally { gate.ReleaseMutex(); }
        }
        check(new MemoryOptimizationResult(new(1000, 700), new(1000, 500)).AvailableChange == -200 &&
            new MemoryOptimizationResult(new(1000, 500), new(1000, 500)).AvailableChange == 0 &&
            new MemoryOptimizationResult(null, new(1000, 500)).AvailableChange is null,
            "optimization reports signed measured delta and distinguishes unavailable memory");
        using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            bool admin = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            bool invoked = false;
            try
            {
                int result = MemoryOptimizer.WithPrivilege(() => { invoked = true; return 42; });
                check(admin && invoked && result == 42, "native privilege acquisition and restoration work without clearing system memory");
            }
            catch (System.ComponentModel.Win32Exception ex) when (!admin && ex.NativeErrorCode == 5)
            { check(!invoked, "unelevated memory helper refuses before any memory operation"); }
        }
        check(ProcessSampler.CalculateCpu(10, 10, 0, TimeSpan.TicksPerSecond * 2, 1, 8) == 25, "process CPU is normalized to all logical processors");
        check(ProcessSampler.CalculateCpu(10, 11, 0, 100, 1, 8) is null, "reused PID does not inherit CPU baseline");
        check(ProcessSampler.CalculateCpu(10, 10, 100, 10, 1, 8) is null && ProcessSampler.CalculateCpu(10, 10, 0, 1, 0, 8) is null, "invalid CPU deltas do not fabricate activity");
        ProcessSample[] samples = [new(100, 10, "浏览器", 100, 20), new(101, 11, "浏览器", 300, 0), new(102, 12, "编辑器", 600, null)];
        var snapshot = new ProcessSnapshot(samples, 1, DateTimeOffset.Now);
        var builder = new ProcessTreeBuilder();
        var measured = snapshot with { Memory = new SystemMemory(2000, 500) };
        check(measured.Memory.Used == 1500 && measured.Memory.UsedPercent == 75, "system memory percentage uses physical total minus available");
        var measuredTree = builder.Build(measured, ProcessMetric.Memory);
        check(measuredTree.Map.Single(p => p.Name.StartsWith("浏览器")).ValueText!.Contains("20.0%") &&
            measuredTree.Rows.Single(p => p.Id == 100).MemoryText.Contains("5.0%"), "program and process percentages use total physical memory");
        check(builder.Build(measured, ProcessMetric.Memory, "101").Rows.Single().MemoryText.Contains("15.0%"), "filtering does not change the memory percentage denominator");
        check(!builder.Build(snapshot, ProcessMetric.Memory).Rows[0].MemoryText.Contains('%'), "unavailable memory total does not fabricate percentages");
        var memory = builder.Build(snapshot, ProcessMetric.Memory);
        check(memory.Map.Count == 2 && memory.Map.Sum(p => p.Bytes) == 1000, "program groups preserve total working set");
        var browser = memory.Map.Single(p => p.Name.StartsWith("浏览器"));
        check(browser.Children is { Count: 2 } && browser.Children.Sum(p => p.Bytes) == 400, "program group expands into exact per-process weights");
        var cpu = builder.Build(snapshot, ProcessMetric.Cpu);
        check(cpu.Map.Count == 1 && cpu.Map[0].Children is { Count: 1 } && cpu.Rows.Count == 3, "zero and unavailable CPU stay in list without invented treemap area");
        check(cpu.Map[0].ValueText == "20.0%" && cpu.Map[0].Children![0].DetailText!.Contains("PID 100"), "CPU tiles and tooltips use process units and identities");
        var pid = builder.Build(snapshot, ProcessMetric.Memory, "101");
        check(pid.Rows.Single().Id == 101 && pid.Map.Single().Bytes == 300, "PID filtering recalculates parent totals");
        check(builder.Build(snapshot, ProcessMetric.Memory, group: "浏览器").Map.All(p => !p.Directory), "drilldown displays processes without unrelated programs");
        check(builder.Build(snapshot, ProcessMetric.Memory, depth: 1).Map.All(p => p.Children is null), "one-level task view collapses all groups");
        var collapsed = builder.Build(snapshot, ProcessMetric.Memory, collapsed: new HashSet<string> { "浏览器" });
        check(collapsed.Map.Single(p => p.Name.StartsWith("浏览器")).Children is null && collapsed.Map.Single(p => p.Name.StartsWith("编辑器")).Children is { Count: 1 }
            && collapsed.Map.Sum(p => p.Bytes) == 1000, "local program collapse overrides default depth while preserving siblings and weights");
        var changed = builder.Build(new([.. samples, new(103, 13, "AAA", 50, 1)], 1, DateTimeOffset.Now), ProcessMetric.Memory);
        check(changed.Map.Single(p => p.Name.StartsWith("浏览器")).Id == browser.Id, "new programs do not change existing group identities");
        check(builder.Build(new([], 0, DateTimeOffset.Now), ProcessMetric.Memory).Map.Count == 0, "empty process snapshot is safe");
        var sampler = new ProcessSampler();
        var first = await Task.Run(sampler.Sample);
        check(first.Memory is { Total: > 0 } physical && physical.Available <= physical.Total && physical.UsedPercent is >= 0 and <= 100, "live physical memory sample is valid");
        check(first.Processes.Any(p => p.Pid == Environment.ProcessId && p.WorkingSet > 0) && first.Processes.All(p => p.Pid != 0), "live sampler reads this process and excludes idle pseudo-process");
        await Task.Delay(150);
        var second = await Task.Run(sampler.Sample);
        check(second.Processes.Single(p => p.Pid == Environment.ProcessId).CpuPercent is >= 0 and <= 100, "second live sample produces valid CPU utilization");
        var own = second.Processes.Single(p => p.Pid == Environment.ProcessId);
        bool selfRejected = false;
        try { ProcessActions.End(own); } catch (InvalidOperationException) { selfRejected = true; }
        check(selfRejected, "end process refuses the current application");
        check(File.Exists(ProcessActions.ExecutablePath(own)), "process location resolves a verified live executable");
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        try
        {
            var disposable = new ProcessSample(child.Id, child.StartTime.ToUniversalTime().Ticks, "test-owned-child", 0, null);
            bool staleRejected = false;
            try { ProcessActions.End(disposable with { StartTicks = disposable.StartTicks - 1 }); } catch (InvalidOperationException) { staleRejected = true; }
            check(staleRejected && !child.HasExited, "stale process identity is rejected without terminating its replacement");
            using var sibling = Process.Start(start)!;
            try
            {
                var secondDisposable = new ProcessSample(sibling.Id, sibling.StartTime.ToUniversalTime().Ticks, "test-owned-child", 0, null);
                var ended = ProcessActions.EndMany([own, disposable, secondDisposable, disposable]);
                check(ended.Ended == 2 && ended.Failures.Count == 1 && child.WaitForExit(5000) && sibling.WaitForExit(5000),
                    "group termination ends only test-owned targets, deduplicates and reports protected-process failure");
                check(!Process.GetCurrentProcess().HasExited, "group termination preserves current application");
            }
            finally { if (!sibling.HasExited) sibling.Kill(); }
        }
        finally { if (!child.HasExited) child.Kill(); }
    }
}
