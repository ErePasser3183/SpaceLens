using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SpaceLens.Core;
using SpaceLens.Tasks;

namespace SpaceLens.App;

public partial class TaskManagerView : UserControl
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly ProcessTreeBuilder builder = new();
    private ProcessSampler sampler = new();
    private ProcessSnapshot? snapshot;
    private ProcessTree? tree;
    private bool ready, active, busy, listVisible;
    private int generation, context, depth = 1, selected = -1;
    private string? group;
    private readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> collapsed = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stack<string?> back = new(), forward = new();
    private ProcessMetric Metric => TaskMetric.SelectedIndex == 1 ? ProcessMetric.Cpu : ProcessMetric.Memory;

    public TaskManagerView()
    {
        InitializeComponent();
        ready = true;
        timer.Tick += async (_, _) => await RefreshAsync();
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); context++; Present(); };
        TaskMap.Selected += id => { selected = id; TaskMap.Select(id); };
        TaskMap.Entered += id => { if (tree?.Nodes.GetValueOrDefault(id) is { IsGroup: true } node) Navigate(node.Group); };
        TaskMap.ExpandRequested += (id, detail) =>
        {
            if (tree?.Nodes.GetValueOrDefault(id) is not { IsGroup: true } node) return;
            if (detail == 1) { expanded.Remove(node.Group); collapsed.Add(node.Group); }
            else { collapsed.Remove(node.Group); expanded.Add(node.Group); }
            context++; Present();
        };
        TaskMap.ContextRequested += (id, _) => ShowProcessMenu(id, TaskMap);
        Unloaded += (_, _) => SetActive(false);
    }

    public void SetActive(bool value)
    {
        if (active == value) return;
        active = value; generation++;
        timer.Stop(); searchTimer.Stop();
        if (value)
        {
            sampler = new(); // A return to this view starts a fresh CPU measurement interval.
            TaskMap.SetDark(ThemeManager.IsDark);
            timer.Start(); _ = RefreshAsync();
        }
    }
    public void SetDark(bool dark) => TaskMap.SetDark(dark);
    public async Task RefreshAsync()
    {
        if (!active || busy) return;
        int version = generation;
        var source = sampler;
        busy = true;
        try
        {
            var next = await Task.Run(source.Sample);
            if (!active || version != generation) return;
            snapshot = next; Present();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        { if (active && version == generation) TaskStatus.Text = "进程读取暂不可用：" + ex.Message; }
        finally { busy = false; }
    }
    private void Present()
    {
        if (!ready || snapshot is null) return;
        tree = builder.Build(snapshot, Metric, TaskSearch.Text.Trim(), group, depth, expanded, collapsed);
        TaskMap.SetItems(tree.Map, HashCode.Combine(context, Metric, group, depth));
        if (tree.Nodes.ContainsKey(selected)) TaskMap.Select(selected); else selected = -1;
        TaskList.ItemsSource = tree.Rows;
        TaskList.SelectedItem = tree.Rows.FirstOrDefault(p => p.Id == selected);
        TaskLocation.Text = group is null ? "本机进程" : "本机进程  ›  " + group;
        TaskUp.IsEnabled = group is not null;
        TaskBack.IsEnabled = back.Count > 0; TaskForward.IsEnabled = forward.Count > 0;
        TaskMapCaption.Text = group is null ? "全部程序" : group;
        string memoryStatus = snapshot.Memory is { } memory
            ? $"内存占用 {memory.UsedPercent:F1}%（{DisplayFormat.Size((long)memory.Used)} / {DisplayFormat.Size((long)memory.Total)}）"
            : "内存占用 —";
        TaskMetricCaption.Text = Metric == ProcessMetric.Memory ? memoryStatus : "CPU · 相对占用面积";
        TaskEmpty.Visibility = tree.Map.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        bool hasCpuSample = tree.Rows.Any(p => p.CpuPercent.HasValue);
        TaskEmpty.Text = tree.Rows.Count == 0 ? "没有匹配进程" : Metric == ProcessMetric.Cpu
            ? hasCpuSample ? "本次采样没有可显示的 CPU 活动；0% 与暂不可读的进程可在列表查看" : "正在等待有效 CPU 采样…"
            : "本次采样没有可显示的内存数据";
        int programs = tree.Rows.Select(p => p.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        string cpuStatus = hasCpuSample ? $"已知 CPU {tree.Rows.Sum(p => p.CpuPercent ?? 0):F1}%" : "CPU 等待有效采样";
        TaskStatus.Text = $"{tree.Rows.Count} 进程 · {programs} 程序    {memoryStatus}    {cpuStatus}";
        if (snapshot.Unavailable > 0) TaskStatus.Text += $"    {snapshot.Unavailable} 项受限 / 已退出";
        TaskStatus.ToolTip = "内存占用为整机当前已用内存比例；方块百分比为程序内存占整机总内存的比例。共享内存可能被多个进程重复计入，程序相加不等于整机占用。CPU 为采样区间内占整机算力的比例；— 表示暂时无法读取。";
        UpdateRefreshState();
    }
    private void Navigate(string? target, bool remember = true)
    {
        if (remember && !string.Equals(group, target, StringComparison.OrdinalIgnoreCase)) { back.Push(group); forward.Clear(); }
        group = target; selected = -1; context++; expanded.Clear(); collapsed.Clear(); TaskSearch.Clear(); Present();
    }
    private void BackClick(object sender, RoutedEventArgs e) { if (back.TryPop(out var target)) { forward.Push(group); Navigate(target, false); } }
    private void ForwardClick(object sender, RoutedEventArgs e) { if (forward.TryPop(out var target)) { back.Push(group); Navigate(target, false); } }
    private void RootClick(object sender, RoutedEventArgs e) => Navigate(null);
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void LessClick(object sender, RoutedEventArgs e) { depth = 1; expanded.Clear(); collapsed.Clear(); context++; TaskDepth.Text = "1 层"; Present(); }
    private void MoreClick(object sender, RoutedEventArgs e) { depth = 2; expanded.Clear(); collapsed.Clear(); context++; TaskDepth.Text = "2 层"; Present(); }
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!ready) return; searchTimer.Stop(); searchTimer.Start(); }
    private void MetricChanged(object sender, SelectionChangedEventArgs e) { if (ready) { context++; Present(); } }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e) { if (TaskList.SelectedItem is ProcessNode node) { selected = node.Id; TaskMap.Select(selected); } }
    private void ListRightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? source = e.OriginalSource as DependencyObject;
        while (source is not null and not DataGridRow) source = VisualTreeHelper.GetParent(source);
        if (source is DataGridRow { Item: ProcessNode node }) { TaskList.SelectedItem = node; ShowProcessMenu(node.Id, TaskList); e.Handled = true; }
    }
    private void ListClick(object sender, RoutedEventArgs e)
    {
        listVisible = !listVisible;
        TaskListWidth.Width = new(listVisible ? 440 : 0); TaskListWidth.MinWidth = listVisible ? 330 : 0;
        TaskDividerWidth.Width = new(listVisible ? 6 : 0);
        TaskListPanel.Visibility = TaskDivider.Visibility = listVisible ? Visibility.Visible : Visibility.Collapsed;
    }
    public event Action<FrameworkElement>? SettingsRequested;
    private void SettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke((FrameworkElement)sender);
    public bool HandleShortcut(KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { TaskSearch.Focus(); TaskSearch.SelectAll(); }
        else if (e.Key == Key.F5) _ = RefreshAsync();
        else if (e.Key == Key.F8) ListClick(this, new());
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Left) BackClick(this, new());
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Right) ForwardClick(this, new());
        else if (Keyboard.Modifiers == ModifierKeys.Alt && (e.SystemKey == Key.Up || e.SystemKey == Key.Home)) Navigate(null);
        else return false;
        e.Handled = true; return true;
    }
    private void ShowProcessMenu(int id, FrameworkElement target)
    {
        if (tree?.Nodes.GetValueOrDefault(id) is not { } node) return;
        BuildProcessMenu(node, target).IsOpen = true;
    }
    private ContextMenu BuildProcessMenu(ProcessNode node, FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        ProcessSample[] targets = node.IsGroup
            ? tree!.Rows.Where(p => p.Group.Equals(node.Group, StringComparison.OrdinalIgnoreCase)).Select(p => p.Process!).ToArray()
            : [node.Process!];
        if (node.IsGroup)
        {
            var open = new MenuItem { Header = "查看此程序的进程" }; open.Click += (_, _) => Navigate(node.Group); menu.Items.Add(open);
        }
        else
        {
            var reveal = new MenuItem { Header = "打开文件所在位置" }; reveal.Click += (_, _) => RevealProcess(node); menu.Items.Add(reveal);
        }
        var end = new MenuItem
        {
            Header = node.IsGroup ? $"结束此组进程（{targets.Length}）…" : "结束此进程…",
            IsEnabled = targets.Length > 0 && targets.All(p => p.StartTicks > 0 && p.Pid > 4 && p.Pid != Environment.ProcessId),
            ToolTip = "结束前会确认；系统关键进程和当前程序不能结束。"
        };
        end.Click += async (_, _) => await EndProcessesAsync(targets, () => MessageBox.Show(Window.GetWindow(this),
            targets.Length == 1 ? $"结束 {targets[0].Name}（PID {targets[0].Pid}）？\n未保存的内容可能丢失。"
                : $"结束当前方块中 {node.Group} 的 {targets.Length} 个进程？\n该程序中未保存的内容可能丢失。",
            "结束进程", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        menu.Items.Add(end);
        var copy = new MenuItem { Header = "复制进程信息" };
        copy.Click += (_, _) => { try { Clipboard.SetText(node.Detail); } catch (COMException) { TaskStatus.Text = "剪贴板正忙，请稍后重试。"; } };
        menu.Items.Add(copy); return menu;
    }
    private async Task EndProcessesAsync(ProcessSample[] targets, Func<bool> confirm)
    {
        if (!confirm()) return;
        var outcome = await Task.Run(() => ProcessActions.EndMany(targets));
        await RefreshAsync();
        TaskStatus.Text = outcome.Failures.Count == 0 ? $"已结束 {outcome.Ended} 个进程"
            : $"已结束 {outcome.Ended} 个进程，{outcome.Failures.Count} 个未能结束";
        if (outcome.Failures.Count > 0)
            MessageBox.Show(Window.GetWindow(this), string.Join("\n", outcome.Failures), "部分进程未能结束", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async void RevealProcess(ProcessNode node)
    {
        if (node.Process is not { } sample) return;
        try
        {
            await Task.Run(() =>
            {
                string path = ProcessActions.ExecutablePath(sample);
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            });
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException)
        { TaskStatus.Text = "操作未完成：" + ex.Message; }
    }
}
