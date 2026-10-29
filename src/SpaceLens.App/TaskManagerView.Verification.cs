using System.IO;
using System.Text.Json;
using System.Windows;
using SpaceLens.Tasks;

namespace SpaceLens.App;

public partial class TaskManagerView
{
    internal double MapHeight => TaskMap.ActualHeight;
    internal bool IsSampling => active && timer.IsEnabled;
    internal Task WaitForMapAsync() => TaskMap.WaitForRenderAsync();
    internal async Task VerifyTasksAsync(string directory, Action<bool, string> check)
    {
        for (int attempt = 0; attempt < 100 && (snapshot is null || busy); attempt++) await Task.Delay(100);
        check(snapshot?.Processes.Any(p => p.Pid == Environment.ProcessId) == true, "live task view includes current process");
        await Task.Delay(300); await RefreshAsync(); await TaskMap.WaitForRenderAsync();
        check(tree is { Map.Count: > 0 } && TaskMap.LabelledTileCount > 0, "actual process samples populate labelled treemap");
        check(depth == 1 && TaskDepth.Text == "1 层" && TaskMap.ExpandedDirectories.Count == 0, "task manager starts at one program layer without expanded processes");
        check(snapshot!.Processes.Any(p => p.CpuPercent.HasValue), "CPU becomes available after second sample");
        check(snapshot.Memory is { Total: > 0 } && TaskMetricCaption.Text.Contains('%') && TaskStatus.Text.Contains("内存占用"), "live memory utilization appears in caption and status");
        File.WriteAllText(Path.Combine(directory, "live-sample-summary.json"), JsonSerializer.Serialize(new { count = snapshot.Processes.Count, snapshot.Unavailable, snapshot.CapturedAt }));
        check(Equals(TaskOptimizeMemory.Content, "内存优化") && TaskOptimizeMemory.TranslatePoint(new Point(), this).X > TaskLocation.TranslatePoint(new Point(), this).X,
            "memory optimization replaces pause at the right of the location bar");
        var completion = new TaskCompletionSource<MemoryOptimizationResult>();
        int optimizationCalls = 0;
        var optimization = OptimizeMemoryAsync(() => { optimizationCalls++; return completion.Task; });
        check(!TaskOptimizeMemory.IsEnabled && Equals(TaskOptimizeMemory.Content, "优化中…") && IsSampling,
            "optimization disables repeated clicks while keeping automatic sampling active");
        await OptimizeMemoryAsync(() => { optimizationCalls++; return completion.Task; });
        check(optimizationCalls == 1, "duplicate optimization is ignored");
        completion.SetResult(new(new(10000000, 2000000), new(10000000, 3000000)));
        await optimization;
        Present();
        check(TaskOptimizeMemory.IsEnabled && TaskRefreshState.Text.Contains("可用内存增加") && IsSampling,
            "optimization success restores button and retains result across refresh");
        await OptimizeMemoryAsync(() => Task.FromException<MemoryOptimizationResult>(new System.ComponentModel.Win32Exception(1223)));
        check(TaskOptimizeMemory.IsEnabled && TaskRefreshState.Text == "已取消内存优化" && IsSampling,
            "cancelled elevation restores button and leaves sampling active");
        await OptimizeMemoryAsync(() => Task.FromException<MemoryOptimizationResult>(new InvalidOperationException("test failure")));
        check(TaskOptimizeMemory.IsEnabled && TaskRefreshState.Text.Contains("test failure") && IsSampling,
            "optimization failure is visible and permits a later retry");
        await OptimizeMemoryAsync(() => Task.FromResult(new MemoryOptimizationResult(new(1000, 800), new(1000, 800))));
        check(TaskOptimizeMemory.IsEnabled && TaskRefreshState.Text.Contains("可用内存无变化") && IsSampling,
            "completed manual optimization reports no change without claiming a skip or fabricated savings");
        check(FormatMemoryResult(new(new(1000, 700), new(1000, 500))).Contains("减少") &&
            FormatMemoryResult(new(null, null)).Contains("暂无法读取"), "decreased and missing memory measurements are not fabricated savings");
        memoryNotice = null;
        var live = snapshot;
        SetActive(false); // Freeze the fixture through the view lifecycle, not a user pause control.
        for (int attempt = 0; attempt < 100 && busy; attempt++) await Task.Delay(50);
        snapshot = new([new(101, 1, "Browser", 8000000, 25), new(102, 2, "Browser", 2000000, 5), new(103, 3, "Editor", 4000000, 0)], 0, DateTimeOffset.Now, new(20000000, 5000000));
        Present(); await TaskMap.WaitForRenderAsync();
        check(TaskMetricCaption.Text.Contains("75.0%") && tree!.Map.Single(p => p.Name.StartsWith("Browser")).ValueText!.Contains("50.0%"), "system and program memory percentages display their respective values");
        MoreClick(this, new()); await TaskMap.WaitForRenderAsync();
        var initialBrowser = tree!.Nodes.Values.Single(n => n.IsGroup && n.Group == "Browser");
        var header = new Point(TaskMap.BoundsFor(initialBrowser.Id).X + 20, TaskMap.BoundsFor(initialBrowser.Id).Y + 6);
        TaskMap.HandlePrimaryClick(header, 1); await TaskMap.WaitForRenderAsync();
        check(!TaskMap.ExpandedDirectories.Contains(initialBrowser.Id), "program header collapses its process children at two-level depth");
        Present(); await TaskMap.WaitForRenderAsync();
        check(!TaskMap.ExpandedDirectories.Contains(initialBrowser.Id), "program collapse survives snapshot refresh");
        TaskMetric.SelectedIndex = 1; await TaskMap.WaitForRenderAsync();
        check(!TaskMap.ExpandedDirectories.Contains(initialBrowser.Id), "program collapse persists when switching to CPU");
        TaskMetric.SelectedIndex = 0; await TaskMap.WaitForRenderAsync();
        var collapsedBounds = TaskMap.BoundsFor(initialBrowser.Id);
        TaskMap.HandlePrimaryClick(new Point(collapsedBounds.X + collapsedBounds.Width / 2, collapsedBounds.Y + collapsedBounds.Height / 2), 1);
        await TaskMap.WaitForRenderAsync();
        check(TaskMap.ExpandedDirectories.Contains(initialBrowser.Id), "collapsed program tile reopens without changing global depth");
        TaskMap.HandlePrimaryClick(header, 1); await TaskMap.WaitForRenderAsync();
        TaskMap.HandlePrimaryClick(header, 2); await TaskMap.WaitForRenderAsync();
        check(group == "Browser", "double clicking program header still enters original program after collapse");
        RootClick(this, new());
        LessClick(this, new()); await TaskMap.WaitForRenderAsync();
        var browser = tree!.Nodes.Values.Single(n => n.IsGroup && n.Group == "Browser");
        var groupMenu = BuildProcessMenu(browser, TaskMap);
        check(groupMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(p => Equals(p.Header, "结束此组进程（2）…") && p.IsEnabled), "default program tile offers termination of its displayed process group");
        var processMenu = BuildProcessMenu(tree.Nodes[101], TaskMap);
        check(processMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(p => Equals(p.Header, "结束此进程…") && p.IsEnabled), "individual process retains termination action");
        var priorStatus = TaskStatus.Text;
        bool confirmationAsked = false;
        await EndProcessesAsync([new(101, 1, "Browser", 0, 0)], () => { confirmationAsked = true; return false; });
        check(confirmationAsked && TaskStatus.Text == priorStatus, "cancelled confirmation performs no process action or refresh");
        var bounds = TaskMap.BoundsFor(browser.Id);
        var point = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        TaskMap.HandlePrimaryClick(point, 1); await TaskMap.WaitForRenderAsync();
        check(TaskMap.ExpandedDirectories.Contains(browser.Id), "clicking program expands process children");
        TaskMap.HandlePrimaryClick(point, 2); await TaskMap.WaitForRenderAsync();
        check(group == "Browser" && tree.Rows.Count == 2 && TaskUp.IsEnabled, "double click enters original program despite changed child layout");
        RootClick(this, new()); TaskSearch.Text = "102"; searchTimer.Stop(); Present();
        check(tree.Rows.Single().Id == 102, "task search filters by PID");
        var filteredMenu = BuildProcessMenu(tree.Nodes.Values.Single(n => n.IsGroup), TaskMap);
        check(filteredMenu.Items.OfType<System.Windows.Controls.MenuItem>().Any(p => Equals(p.Header, "结束此组进程（1）…")), "group action only targets processes included in filtered tile");
        check(TaskMetricCaption.Text.Contains("75.0%") && tree.Rows.Single().MemoryText.Contains("10.0%"), "filtered memory view retains whole-machine utilization and denominator");
        TaskSearch.Clear(); searchTimer.Stop(); MoreClick(this, new()); TaskMetric.SelectedIndex = 1; Present();
        await TaskMap.WaitForRenderAsync();
        check(tree.Map.Count == 1 && tree.Map[0].ValueText == "30.0%", "CPU mode renders percentage values rather than bytes");
        ListClick(this, new()); UpdateLayout();
        check(TaskListPanel.IsVisible && TaskList.Items.Count == 3, "optional process list retains zero-CPU processes");
        ListClick(this, new());
        check(!TaskListPanel.IsVisible, "list can be hidden to restore full map width");
        TaskMetric.SelectedIndex = 0; snapshot = live; LessClick(this, new()); Present();
        SetActive(true);
        for (int attempt = 0; attempt < 100 && busy; attempt++) await Task.Delay(50);
        check(IsSampling, "returning to active view restarts automatic sampling");
        await Task.Delay(300); await RefreshAsync();
        await TaskMap.WaitForRenderAsync();
    }
}
