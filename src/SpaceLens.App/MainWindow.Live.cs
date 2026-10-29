using System.IO;
using System.Windows;
using SpaceLens.Core;

namespace SpaceLens.App;

public partial class MainWindow
{
    private void StartLive(string path)
    {
        if (!liveEnabled || closing || liveChanges is not null) return;
        try { liveChanges = new LiveChanges(path); liveTimer.Start(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        { Notify("实时监控不可用：" + ex.Message); }
    }
    private void StopLive()
    {
        liveTimer.Stop(); liveControl?.Cancel(); liveChanges?.Dispose(); liveChanges = null;
    }
    private async Task UpdateLiveAsync(bool force = false)
    {
        if (closing || starting || scanControl is not null || liveBusy || liveChanges is null || result is null) return;
        var monitor = liveChanges; var original = result;
        if (monitor.Failure is { } failure)
        { StopLive(); StateMetric.Text = "实时监控中断"; Notify("实时监控中断，可重新扫描恢复：" + failure); return; }
        var batch = monitor.TakeReady(force);
        if (batch is null) return;
        liveBusy = true;
        using var control = new ScanControl(); liveControl = control;
        bool lowImpact = LowImpactCheck.IsChecked == true;
        try
        {
            if (batch.Rescan) StateMetric.Text = "正在校准";
            var patch = await Task.Run(() => LiveUpdate.Read(original.Store, batch, control, lowImpact), control.Token);
            if (closing || monitor != liveChanges || result != original || control.Token.IsCancellationRequested) return;
            // Remember paths at application time: the user may have navigated while disk metadata was read.
            var store = original.Store;
            string currentPath = store.FullPath(current);
            string? selectedPath = store.IsActive(selected) ? store.FullPath(selected) : null;
            string[] back = backHistory.Where(store.IsActive).Select(store.FullPath).ToArray();
            string[] forward = forwardHistory.Where(store.IsActive).Select(store.FullPath).ToArray();
            var expandedPaths = expandedDepths.Where(pair => store.IsActive(pair.Key)).Select(pair => (Path: store.FullPath(pair.Key), pair.Value)).ToArray();
            bool changed;
            bool viewPending = viewCancellation is not null;
            viewCancellation?.Cancel(); viewVersion++;
            if (patch.Replacement is { Cancelled: false } replacement)
            { result = replacement; changed = true; }
            else
            {
                changed = patch.Apply(store);
                result = original with { IssueCount = original.IssueCount + patch.IssueCount, SkippedLinks = store.LinkCount,
                    Issues = original.Issues.Concat(patch.Issues).Take(200).ToArray() };
                if (store.RemovedCount >= 8192 && store.RemovedCount > store.Count / 4)
                {
                    // Compact occasionally, retaining UI paths rather than recycling IDs under navigation history.
                    var compacted = await Task.Run(() => { lock (store.SyncRoot) return store.Compact(); });
                    if (closing || monitor != liveChanges || control.Token.IsCancellationRequested) return;
                    result = result with { Store = compacted };
                    changed = true;
                }
            }
            store = result.Store;
            if (store != original.Store)
            {
                expandedDepths = expandedPaths.Select(pair => (Id: store.FindPath(pair.Path), pair.Value)).Where(pair => pair.Id >= 0).ToDictionary(pair => pair.Id, pair => pair.Value);
                expansionVersion++; RefreshPreviewSettings();
            }
            int restored;
            while ((restored = store.FindPath(currentPath)) < 0 && Path.GetDirectoryName(currentPath) is { } parent) currentPath = parent;
            current = Math.Max(0, restored);
            selected = selectedPath is null ? -1 : store.FindPath(selectedPath);
            Restore(backHistory, back); Restore(forwardHistory, forward);
            BackButton.IsEnabled = backHistory.Count > 0; ForwardButton.IsEnabled = forwardHistory.Count > 0;
            if (selected >= 0) SelectNode(selected);
            SizeMetric.Text = DisplayFormat.Size(store[0].Bytes); FilesMetric.Text = store[0].Files.ToString("N0");
            FoldersMetric.Text = $"{store[0].Folders:N0} 个文件夹";
            StateMetric.Text = patch.IssueCount > 0 ? "实时 · 有跳过" : "实时更新";
            IssuesButton.Content = $"{result.IssueCount:N0} 项异常";
            if (changed || viewPending)
            {
                TimeMetric.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
                await UpdateViewAsync();
            }
            void Restore(Stack<int> history, IEnumerable<string> paths)
            {
                history.Clear();
                foreach (string path in paths.Reverse()) { int id = store.FindPath(path); if (id >= 0 && store[id].IsDirectory) history.Push(id); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        {
            if (!closing && monitor == liveChanges)
            {
                StateMetric.Text = "实时更新失败"; Notify("实时更新暂未完成：" + ex.Message);
                // Do not hammer an inaccessible/disconnected root with an endless full-scan retry.
                if (batch.Rescan) StopLive();
            }
        }
        finally { if (liveControl == control) liveControl = null; liveBusy = false; }
    }
}
