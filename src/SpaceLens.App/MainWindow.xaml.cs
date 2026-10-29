using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SpaceLens.Core;

namespace SpaceLens.App;

public partial class MainWindow : Window
{
    private ScanResult? result;
    private ScanControl? scanControl;
    private CancellationTokenSource? viewCancellation;
    private readonly DispatcherTimer searchTimer = new() { Interval = TimeSpan.FromMilliseconds(240) };
    private readonly DispatcherTimer memoryTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int current, selected = -1, viewVersion;
    private bool ready, largest, closing, starting;
    private string? lastRoot;
    private ViewResult? view;
    private readonly string? initialPath;
    private readonly Stack<int> backHistory = new(), forwardHistory = new();
    private int mapDepth = 3;
    private IReadOnlyDictionary<int, DirectoryDetail> expandedDepths = new Dictionary<int, DirectoryDetail>();
    private int expansionVersion;
    private bool listVisible;
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly DispatcherTimer liveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private LiveChanges? liveChanges;
    private ScanControl? liveControl;
    private bool liveEnabled = true, liveBusy;
    private int scanPreviewCount;
    private ScanPreviewSettings scanPreviewSettings = new();
    private long lastInteraction = Environment.TickCount64;
    public MainWindow(string? path)
    {
        initialPath = path;
        InitializeComponent();
        TaskView.SettingsRequested += OpenSettings;
        MapRootName.ToolTip = "目录总量包含未显示的极小项；方块面积按同级可见项的相对大小分配。";
        ThemeManager.Attach(this);
        ThemeManager.Changed += OnThemeChanged;
        OnThemeChanged();
        PreviewMouseMove += (_, _) => { lastInteraction = Environment.TickCount64; RefreshPreviewSettings(); };
        Activated += (_, _) => RefreshPreviewSettings();
        Deactivated += (_, _) => RefreshPreviewSettings();
        StateChanged += (_, _) => { RefreshPreviewSettings(); TaskView.SetActive(taskMode && WindowState != WindowState.Minimized); };
        LowImpactCheck.IsChecked = false;
        ready = true;
        noticeTimer.Tick += (_, _) => { noticeTimer.Stop(); StatusText.Visibility = Visibility.Collapsed; StatusMetrics.Visibility = Visibility.Visible; };
        Map.Selected += SelectNode;
        Map.Entered += Navigate;
        Map.ExpandRequested += ExpandNode;
        Map.ContextRequested += (id, _) => ShowContextMenu(id, Map);
        searchTimer.Tick += (_, _) => { searchTimer.Stop(); _ = UpdateViewAsync(); };
        liveTimer.Tick += async (_, _) => await UpdateLiveAsync();
        memoryTimer.Tick += (_, _) =>
        {
            using var p = Process.GetCurrentProcess();
            RefreshPreviewSettings();
            MemoryText.Text = $"内存 {DisplayFormat.Size(p.WorkingSet64)}   ·   {(scanControl is not null ? "后台扫描" : liveChanges is not null ? "实时" : "空闲")}";
        };
        Loaded += async (_, _) => { await LoadDrivesAsync(); memoryTimer.Start(); if (initialPath is not null) await StartScanAsync(initialPath); };
        Closing += (_, _) => { closing = true; TaskView.SetActive(false); StopLive(); scanControl?.Cancel(); viewCancellation?.Cancel(); memoryTimer.Stop(); searchTimer.Stop(); noticeTimer.Stop(); ThemeManager.Changed -= OnThemeChanged; };
    }

    private async Task LoadDrivesAsync()
    {
        DrivePanel.Children.Clear();
        var drives = await Task.Run(() =>
        {
            var list = new List<(string Path, string Label, string Info, double Used)>();
            foreach (var d in DriveInfo.GetDrives())
            {
                // Don't probe unresponsive network shares or optical media during application startup.
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                try { if (d.IsReady) list.Add((d.RootDirectory.FullName, $"{d.Name.TrimEnd('\\')}  {(string.IsNullOrEmpty(d.VolumeLabel) ? "本地磁盘" : d.VolumeLabel)}", $"可用 {DisplayFormat.Size(d.AvailableFreeSpace)}", d.TotalSize > 0 ? 100.0 * (d.TotalSize - d.AvailableFreeSpace) / d.TotalSize : 0)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return list;
        });
        if (closing) return;
        foreach (var d in drives)
        {
            var button = new Button { Content = d.Path.TrimEnd('\\'), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 5, 0), ToolTip = d.Label + " · " + d.Info };
            button.Click += async (_, _) => await StartScanAsync(d.Path);
            DrivePanel.Children.Add(button);
        }
    }

    private async Task StartScanAsync(string path)
    {
        if (scanControl is not null || starting) { Notify("请先停止当前扫描，再扫描其他位置。"); return; }
        starting = true;
        try
        {
            path = Path.GetFullPath(path.Trim().Trim('"'));
            if (string.IsNullOrWhiteSpace(path)) return;
            // Validate on a worker: even an existence probe may block for disconnected locations.
            bool exists = await Task.Run(() => Directory.Exists(path));
            if (!exists) { Notify("目录不存在或当前无法访问，请检查路径。"); return; }
            if (closing) return;
            StopLive();
            viewCancellation?.Cancel(); viewVersion++;
            result = null; view = null; current = 0; selected = -1;
            backHistory.Clear(); forwardHistory.Clear(); BackButton.IsEnabled = ForwardButton.IsEnabled = false;
            ResetExpansions(); MapRootName.Text = path;
            Map.SetItems([]); FileList.ItemsSource = null;
            ExportButton.IsEnabled = RootButton.IsEnabled = UpButton.IsEnabled = false;
            lastRoot = path; PathBox.Text = path;
            largest = false; LargestButton.Content = "大文件";
            SearchBox.Clear(); TypeFilter.SelectedIndex = SizeFilter.SelectedIndex = 0;
            WelcomePanel.Visibility = Visibility.Visible; EmptyHeading.Text = "扫描中…"; EmptyText.Text = "";
            ScanPanel.Visibility = Visibility.Visible; RefreshButton.IsEnabled = false; LowImpactCheck.IsEnabled = false;
            PauseButton.Content = "暂停"; PauseButton.IsEnabled = CancelButton.IsEnabled = true; ScanBar.IsIndeterminate = true;
            StateMetric.Text = "扫描中"; LinkMetric.Text = ""; IssuesButton.Content = "0 项异常";
            ListCaption.Text = ""; SizeMetric.Text = "0 B"; FilesMetric.Text = "0"; FoldersMetric.Text = "0 个文件夹";
            scanControl = new ScanControl();
            var control = scanControl;
            StartLive(path); // Subscribe before enumeration so changes made during the scan are caught too.
            scanPreviewCount = 0;
            var preview = new Progress<IReadOnlyList<MapItem>>(items =>
            {
                if (closing || scanControl != control || result is not null) return;
                Map.SetItems(items); WelcomePanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                scanPreviewCount++;
            });
            bool lowImpact = LowImpactCheck.IsChecked == true;
            var progress = new Progress<ScanProgress>(p =>
            {
                if (closing || scanControl != control) return;
                SizeMetric.Text = DisplayFormat.Size(p.Bytes); FilesMetric.Text = p.Files.ToString("N0"); FoldersMetric.Text = $"{p.Folders:N0} 个文件夹";
                TimeMetric.Text = $"{DisplayFormat.Duration(p.Elapsed)} · {(p.Elapsed.TotalSeconds > 0 ? p.Files / p.Elapsed.TotalSeconds : 0):N0} 文件/秒";
                ScanPath.Text = p.CurrentPath; IssuesButton.Content = $"{p.IssueCount:N0} 项异常";
            });
            Notify(lowImpact ? "低占用扫描" : "快速扫描 · 最多四路并行");
            int previewDepth = mapDepth;
            result = await Task.Run(() => new DiskScanner().Scan(path, control, ((IProgress<ScanProgress>)progress).Report, lowImpact,
                ((IProgress<IReadOnlyList<MapItem>>)preview).Report, previewDepth, () => Volatile.Read(ref scanPreviewSettings)));
            if (closing) return;
            SizeMetric.Text = DisplayFormat.Size(result.Store[0].Bytes); FilesMetric.Text = result.Store[0].Files.ToString("N0");
            FoldersMetric.Text = $"{result.Store[0].Folders:N0} 个文件夹";
            if (result.Cancelled) StopLive();
            StateMetric.Text = result.Cancelled ? "部分结果" : liveChanges is not null ? "实时更新" : result.IssueCount > 0 ? "完成 · 有跳过" : "已完成";
            TimeMetric.Text = $"用时 {DisplayFormat.Duration(result.Elapsed)}";
            IssuesButton.Content = $"{result.IssueCount:N0} 项异常"; LinkMetric.Text = $"跳过 {result.SkippedLinks:N0} 个链接 / 重解析点";
            RootButton.IsEnabled = true;
            Notify(result.Cancelled ? "已停止，显示部分结果" : liveChanges is not null ? "扫描完成 · 实时更新已开启" : "扫描完成");
            await UpdateViewAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            StopLive();
            Notify("无法扫描：" + ex.Message); StateMetric.Text = "未完成"; EmptyHeading.Text = "无法读取此位置"; EmptyText.Text = ex.Message;
        }
        finally
        {
            starting = false;
            scanControl?.Dispose(); scanControl = null;
            if (!closing) { ScanPanel.Visibility = Visibility.Collapsed; ScanBar.IsIndeterminate = false; RefreshButton.IsEnabled = lastRoot is not null; LowImpactCheck.IsEnabled = true; }
        }
    }

    private QueryOptions Options() => new(SearchBox.Text.Trim(), ComboText(TypeFilter), SizeFilter.SelectedIndex switch { 1 => 100L << 20, 2 => 1L << 30, 3 => 10L << 30, _ => 0 }, largest, ComboText(SortBox), MapDepth: mapDepth,
        MapLimit: Math.Clamp((int)(Map.ActualWidth * Map.ActualHeight / 64), 6000, 12000), ExpandedDepths: expandedDepths);
    private async Task UpdateViewAsync()
    {
        if (result is null || closing) return;
        viewCancellation?.Cancel();
        var cancellation = new CancellationTokenSource(); viewCancellation = cancellation;
        var token = cancellation.Token; int version = ++viewVersion;
        var store = result.Store; int root = current; var options = Options(); int expansions = expansionVersion;
        try
        {
            ListCaption.Text = "正在整理…";
            var next = await Task.Run(() => ViewQuery.Build(store, root, options, token), token);
            if (closing || version != viewVersion || token.IsCancellationRequested) return;
            view = next;
            FileList.ItemsSource = next.Rows.Select(x => new Row(x)).ToArray();
            Map.SetItems(next.Map, HashCode.Combine(store, root, options.Search, options.Category, options.MinimumBytes, options.LargestFiles, options.MapDepth, expansions));
            ExportButton.IsEnabled = next.Rows.Count > 0;
            ListCaption.Text = next.MatchCount > next.Rows.Count ? $"{next.MatchCount:N0} 项 · 显示最大的 {next.Rows.Count:N0} 项" : $"{next.MatchCount:N0} 项 · {DisplayFormat.Size(next.MatchBytes)}";
            MapCaption.Text = next.Filtered ? "筛选结果" : "";
            UpButton.IsEnabled = current > 0;
            PathBox.Text = store.FullPath(current);
            MapRootName.Text = $"{store.FullPath(current)}  ·  {DisplayFormat.Size(store[current].Bytes)}";
            if (!taskMode) Title = "SpaceLens";
            WelcomePanel.Visibility = next.Map.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyHeading.Text = next.MatchCount > 0 ? "零字节项目" : "无结果";
            EmptyText.Text = "";
            if (selected >= 0) Map.Select(selected);
        }
        catch (OperationCanceledException) { }
        finally { if (viewCancellation == cancellation) viewCancellation = null; cancellation.Dispose(); }
    }
    private void Navigate(int id)
        => NavigateTo(id, true);
    private void NavigateTo(int id, bool remember)
    {
        if (result is null || !result.Store.IsActive(id) || !result.Store[id].IsDirectory) return;
        if (remember && id != current) { backHistory.Push(current); forwardHistory.Clear(); }
        BackButton.IsEnabled = backHistory.Count > 0; ForwardButton.IsEnabled = forwardHistory.Count > 0;
        ResetExpansions();
        current = id; selected = -1; SearchBox.Clear(); TypeFilter.SelectedIndex = SizeFilter.SelectedIndex = 0; largest = false; LargestButton.Content = "大文件";
        _ = UpdateViewAsync();
    }
    private void SelectNode(int id)
    {
        if (result is null || !result.Store.IsActive(id)) return;
        selected = id; Map.Select(id);
    }
    private void ExpandNode(int id, int depth)
    {
        if (result is null && scanControl is null) return;
        if (result is not null && (!result.Store.IsActive(id) || !result.Store[id].IsDirectory)) return;
        expansionVersion++;
        expandedDepths = new Dictionary<int, DirectoryDetail>(expandedDepths) { [id] = new(depth, expansionVersion) };
        RefreshPreviewSettings();
        _ = UpdateViewAsync();
    }
    private void ResetExpansions()
    {
        expandedDepths = new Dictionary<int, DirectoryDetail>(); expansionVersion++; RefreshPreviewSettings();
    }
    private void ShowContextMenu(int id, FrameworkElement target)
    {
        if (result is null || !result.Store.IsActive(id)) return;
        SelectNode(id);
        var menu = new ContextMenu();
        if (result?.Store[id].IsDirectory == true) { var enter = new MenuItem { Header = "进入文件夹" }; enter.Click += (_, _) => Navigate(id); menu.Items.Add(enter); }
        var reveal = new MenuItem { Header = "在资源管理器中定位" }; reveal.Click += (_, _) => Reveal(); menu.Items.Add(reveal);
        var copy = new MenuItem { Header = "复制完整路径" }; copy.Click += (_, _) => CopyPath(); menu.Items.Add(copy);
        AddDeletionItems(menu, id);
        menu.PlacementTarget = target; menu.IsOpen = true;
    }
    private void Reveal()
    {
        if (result is null || selected < 0) return;
        var path = result.Store.FullPath(selected);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception) { Notify("无法打开资源管理器：" + ex.Message); }
    }
    private void CopyPath() { if (result is null || selected < 0) return; try { Clipboard.SetText(result.Store.FullPath(selected)); Notify("已复制完整路径。"); } catch (System.Runtime.InteropServices.COMException) { Notify("剪贴板正忙，请稍后重试。"); } }
    public void Notify(string message)
    {
        if (closing) return;
        StatusText.Text = message; StatusText.ToolTip = message;
        StatusText.Visibility = Visibility.Visible; StatusMetrics.Visibility = Visibility.Collapsed;
        noticeTimer.Stop(); noticeTimer.Start();
    }
    private async void ChooseClick(object sender, RoutedEventArgs e) { var d = new OpenFolderDialog { Title = "选择要分析的文件夹或磁盘" }; if (d.ShowDialog(this) == true) await StartScanAsync(d.FolderName); }
    private async void RefreshClick(object sender, RoutedEventArgs e) { if (lastRoot is not null) await StartScanAsync(lastRoot); }
    private async void PathScanClick(object sender, RoutedEventArgs e) => await StartScanAsync(PathBox.Text);
    private async void PathKeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; await StartScanAsync(PathBox.Text); } }
    private async void RefreshDrivesClick(object sender, RoutedEventArgs e) => await LoadDrivesAsync();
    private void PauseClick(object sender, RoutedEventArgs e)
    {
        if (scanControl is null) return;
        if (scanControl.IsPaused) { scanControl.Resume(); PauseButton.Content = "暂停"; StateMetric.Text = "扫描中"; ScanBar.IsIndeterminate = true; }
        else { scanControl.Pause(); PauseButton.Content = "继续"; StateMetric.Text = "已暂停"; ScanBar.IsIndeterminate = false; }
    }
    private void CancelClick(object sender, RoutedEventArgs e) { scanControl?.Cancel(); PauseButton.IsEnabled = CancelButton.IsEnabled = false; StateMetric.Text = "正在停止"; }
    private void UpClick(object sender, RoutedEventArgs e) { if (result is not null && current > 0) Navigate(result.Store[current].Parent); }
    private void RootClick(object sender, RoutedEventArgs e) => Navigate(0);
    private void SearchChanged(object sender, TextChangedEventArgs e) { if (!ready) return; searchTimer.Stop(); searchTimer.Start(); }
    private void FilterChanged(object sender, SelectionChangedEventArgs e) { if (ready) _ = UpdateViewAsync(); }
    private void LargestClick(object sender, RoutedEventArgs e) { largest = !largest; LargestButton.Content = largest ? "目录视图" : "大文件"; if (largest) SetListVisible(true); _ = UpdateViewAsync(); }
    private void ListSelectionChanged(object sender, SelectionChangedEventArgs e) { if (FileList.SelectedItem is Row row) SelectNode(row.Id); }
    private void ListDoubleClick(object sender, MouseButtonEventArgs e) { if (FileList.SelectedItem is Row row && FindRow(e.OriginalSource as DependencyObject) is not null) Navigate(row.Id); }
    private void ListRightClick(object sender, MouseButtonEventArgs e) { if (FindRow(e.OriginalSource as DependencyObject)?.Item is Row row) { FileList.SelectedItem = row; ShowContextMenu(row.Id, FileList); e.Handled = true; } }
    private static DataGridRow? FindRow(DependencyObject? element) { while (element is not null and not DataGridRow) element = VisualTreeHelper.GetParent(element); return element as DataGridRow; }
    private async void ExportClick(object sender, RoutedEventArgs e)
    {
        if (result is null || view is null) return;
        var dialog = new SaveFileDialog { Title = "导出当前显示的列表（最多 2000 项）", Filter = "CSV 报告|*.csv", FileName = "SpaceLens-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".csv" };
        if (dialog.ShowDialog(this) != true) return;
        var store = result.Store; var rows = view.Rows;
        try { await Task.Run(() => ViewQuery.ExportCsv(dialog.FileName, store, rows)); Notify($"已导出 {rows.Count:N0} 项到 {dialog.FileName}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notify("导出失败：" + ex.Message); }
    }
    private void IssuesClick(object sender, RoutedEventArgs e)
    {
        if (result is null) { Notify("扫描完成或停止后可查看记录。"); return; }
        ShowText("扫描记录", $"异常 {result.IssueCount:N0} 项（最多保留 200 条）；跳过 {result.SkippedLinks:N0} 个链接 / 重解析点。\n" +
            (result.Cancelled ? "扫描被停止，结果不完整。\n" : "") + "\n" + (result.Issues.Count == 0 ? "没有记录到读取异常。" : string.Join("\n\n", result.Issues.Select(x => x.Path + "\n" + x.Reason))));
    }
    private void HelpClick(object sender, RoutedEventArgs e) => ShowText("使用说明", taskMode ? """
        Ctrl+F　搜索
        F5　刷新
        F8　显示 / 隐藏列表
        Alt+← / Alt+→　后退 / 前进
        Alt+↑ / Alt+Home　返回全部进程

        内存优化与下载功能参考了龙腾猫跃的 PCL2 启动器中的相关模块。
        """ : """
        Ctrl+F　搜索
        F5　重新扫描
        Esc　停止扫描
        F8　显示 / 隐藏列表
        Alt+← / Alt+→　后退 / 前进
        Alt+↑　上一级
        Alt+Home　根目录
        """);
    private void ShowText(string title, string text)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Margin = new Thickness(22), Background = Brushes.Transparent };
        var window = new Window { Owner = this, Title = title, Width = title == "使用说明" ? 600 : 720, Height = title == "使用说明" ? 340 : 600, MinWidth = 400, MinHeight = 300, Content = box, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ThemeManager.Attach(window); window.Show();
    }
    private void OnThemeChanged() { Map.SetDark(ThemeManager.IsDark); TaskView.SetDark(ThemeManager.IsDark); }
    private void AddThemeSettings(ContextMenu menu)
    {
        foreach (var (label, mode) in new[] { ("跟随系统", AppThemeMode.System), ("浅色", AppThemeMode.Light), ("深色", AppThemeMode.Dark) })
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = ThemeManager.Mode == mode };
            item.Click += (_, _) => ThemeManager.SetMode(mode); menu.Items.Add(item);
        }
    }
    private void SetListVisible(bool show)
    {
        listVisible = show; ListColumn.Width = new GridLength(show ? 380 : 0); ListDividerColumn.Width = new GridLength(show ? 6 : 0);
        ListPanel.Visibility = ListDivider.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ListColumn.MinWidth = show ? 270 : 0;
    }
    private void ListToggleClick(object sender, RoutedEventArgs e) => SetListVisible(!listVisible);
    private void LessDepthClick(object sender, RoutedEventArgs e) { if (mapDepth > 1 || expandedDepths.Count > 0) { mapDepth = Math.Max(1, mapDepth - 1); DepthText.Text = $"{mapDepth} 层"; ResetExpansions(); _ = UpdateViewAsync(); } }
    private void MoreDepthClick(object sender, RoutedEventArgs e) { if (mapDepth < 15) { mapDepth++; DepthText.Text = $"{mapDepth} 层"; ResetExpansions(); _ = UpdateViewAsync(); } }
    private void RefreshPreviewSettings()
    {
        int interval = WindowState == WindowState.Minimized ? 1000 : !IsActive ? 400 : Environment.TickCount64 - lastInteraction < 2000 ? 50 : 120;
        Volatile.Write(ref scanPreviewSettings, new ScanPreviewSettings(mapDepth, interval, expandedDepths));
    }
    private void BackClick(object sender, RoutedEventArgs e) { if (backHistory.TryPop(out int id)) { forwardHistory.Push(current); NavigateTo(id, false); } }
    private void ForwardClick(object sender, RoutedEventArgs e) { if (forwardHistory.TryPop(out int id)) { backHistory.Push(current); NavigateTo(id, false); } }
    private void AddDiskSettings(ContextMenu menu)
    {
        if (taskMode) return;
        var drives = new MenuItem { Header = "刷新磁盘列表" }; drives.Click += RefreshDrivesClick; menu.Items.Add(drives);
        var live = new MenuItem { Header = "实时更新", IsCheckable = true, IsChecked = liveEnabled };
        live.Click += async (_, _) =>
        {
            liveEnabled = live.IsChecked;
            if (!liveEnabled) { StopLive(); if (scanControl is null) StateMetric.Text = "实时已关闭"; }
            else if (lastRoot is not null)
            {
                StartLive(lastRoot);
                // Reconcile the interval when monitoring was disabled before accepting new notifications.
                if (result is not null) { liveChanges?.RequestReconcile(); await UpdateLiveAsync(true); }
            }
        }; menu.Items.Add(live);
    }
    private async void OnDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) { SetTaskMode(false); await StartScanAsync(Directory.Exists(files[0]) ? files[0] : Path.GetDirectoryName(files[0])!); } }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (taskMode) { TaskView.HandleShortcut(e); return; }
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); e.Handled = true; }
        else if (e.Key == Key.F5) { if (lastRoot is not null) _ = StartScanAsync(lastRoot); e.Handled = true; }
        else if (e.Key == Key.F8) { SetListVisible(!listVisible); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Left) { BackClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Right) { ForwardClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Escape && scanControl is not null) { CancelClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Up) { UpClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.SystemKey == Key.Home) { RootClick(this, new RoutedEventArgs()); e.Handled = true; }
        else if (e.Key == Key.Enter && FileList.IsKeyboardFocusWithin && FileList.SelectedItem is Row row) { Navigate(row.Id); e.Handled = true; }
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private static string ComboText(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
    private static Brush Hex(string value) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
    private sealed class Row(ViewItem item)
    {
        public int Id => item.Id; public string Name => item.Name; public string RelativePath => item.RelativePath;
        public string Size => DisplayFormat.Size(item.Bytes); public string Icon => item.Directory ? "\uE8B7" : "\uE8A5";
        public Brush Color => Hex(item.Directory ? "#B39B60" : "#849BAD");
    }
}
