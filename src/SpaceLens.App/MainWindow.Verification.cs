using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpaceLens.App;

public partial class MainWindow
{
    // Opt-in integration harness. Exercises the actual window and saves its actual WPF render.
    // Normal startup never runs this code, takes screenshots, or writes scan data.
    internal async Task VerifyUiAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        try
        {
            await Task.Delay(300);
            await VerifyLiveUiAsync(Check);
            var scanning = StartScanAsync(@"D:\");
            await Task.Delay(3500);
            Check(scanPreviewCount > 0, "treemap is populated before full disk scan finishes");
            MoreDepthClick(this, new RoutedEventArgs());
            Check(Volatile.Read(ref scanPreviewSettings).Depth == 4, "detail control updates the running scan preview");
            await Task.Delay(600);
            LessDepthClick(this, new RoutedEventArgs());
            await RenderAsync("scan-progress.png");
            await scanning;
            await Map.WaitForRenderAsync();
            Check(result is { Cancelled: false } && result.Store[0].Files > 0 && view is { Rows.Count: > 0 }, "actual folder scan populates map and list");
            Check(!ScanBar.IsIndeterminate, "scan animation stops when idle");
            int folder = view!.Rows.First(x => x.Directory && x.Bytes > 0).Id;
            SelectNode(folder);
            Check(selected == folder && FindName("DetailsPanel") is null, "selection retains context actions without a bottom detail panel");
            Navigate(folder); await UpdateViewAsync();
            Check(current == folder && UpButton.IsEnabled && PathBox.Text == result!.Store.FullPath(folder), "folder navigation");
            BackClick(this, new RoutedEventArgs()); await UpdateViewAsync();
            Check(current == 0 && ForwardButton.IsEnabled, "back navigation");
            ForwardClick(this, new RoutedEventArgs()); await UpdateViewAsync();
            Check(current == folder, "forward navigation");
            UpClick(this, new RoutedEventArgs()); await UpdateViewAsync();
            Check(current == 0 && !UpButton.IsEnabled, "up navigation");
            LessDepthClick(this, new RoutedEventArgs()); LessDepthClick(this, new RoutedEventArgs());
            await UpdateViewAsync(); await RenderAsync("detail-1.png");
            Check(mapDepth == 1 && Map.ExpandedDirectories.Count == 0, "one detail level shows folders as whole tiles");
            MoreDepthClick(this, new RoutedEventArgs()); await UpdateViewAsync(); await RenderAsync("detail-2.png");
            Check(mapDepth == 2 && Map.ExpandedDirectories.Count > 0, "plus button expands the next directory level");
            MoreDepthClick(this, new RoutedEventArgs()); await UpdateViewAsync(); await RenderAsync("detail-3.png");
            Check(mapDepth == 3, "default three-level detail can be restored");
            SearchBox.Text = "python"; searchTimer.Stop(); await UpdateViewAsync();
            Check(view is { Filtered: true, Rows.Count: > 0 } && view.Rows.All(x => x.Name.Contains("python", StringComparison.OrdinalIgnoreCase)), "search filters real files");
            SearchBox.Clear(); searchTimer.Stop(); largest = true; await UpdateViewAsync();
            Check(view!.Rows.All(x => !x.Directory), "largest-file mode excludes folders");
            largest = false; await UpdateViewAsync();
            selected = -1; Map.Select(-1);
            await Task.Delay(3100);
            ThemeManager.SetMode(AppThemeMode.Light); await RenderAsync("light.png");
            Check(!ThemeManager.IsDark && ((SolidColorBrush)Application.Current.Resources["Surface"]).Color.R == 255, "light theme resources");
            ThemeManager.SetMode(AppThemeMode.Dark); await RenderAsync("dark.png");
            Check(Map.HiddenTileCount > 0, "subpixel tiles blend into their parent without a grid");
            Check(Map.LabelledTileCount > 150 && Map.UnlabelledTileCount == 0, "visible tiles all carry labels; only unreadably small tiles blend into parent");
            Check(ThemeManager.IsDark && ((SolidColorBrush)Application.Current.Resources["Surface"]).Color.R < 80, "dark theme resources");
            SetListVisible(true); await RenderAsync("dark-list.png");
            Check(FileList.ActualWidth >= 270, "optional file list");
            SetListVisible(false);
            Width = 1040; Height = 650; await RenderAsync("dark-compact.png");
            Check(Map.ActualWidth >= 700 && ListPanel.Visibility == Visibility.Collapsed, "minimum-size content remains usable");
            Width = 1440; Height = 900;
            ThemeManager.SetMode(AppThemeMode.System);
            Check(ThemeManager.IsDark == ThemeManager.ReadSystemDark(), "system theme follows Windows app preference");
            await Task.Delay(3000);
            using var process = Process.GetCurrentProcess();
            var idleSamples = new List<double>();
            for (int sample = 0; sample < 3; sample++)
            {
                process.Refresh(); var cpu = process.TotalProcessorTime; var elapsed = Stopwatch.StartNew();
                await Task.Delay(2000); process.Refresh();
                idleSamples.Add((process.TotalProcessorTime - cpu).TotalSeconds / elapsed.Elapsed.TotalSeconds * 100);
            }
            double idleCpu = idleSamples.Order().ElementAt(1);
            File.WriteAllText(Path.Combine(directory, "ui-verification.json"), JsonSerializer.Serialize(new { passed = checks.Count, checks,
                idleCpuPercentOfOneCore = idleCpu, idleCpuSamples = idleSamples, workingSetMiB = process.WorkingSet64 / 1048576.0,
                managedMiB = GC.GetTotalMemory(false) / 1048576.0, committedMiB = GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576.0,
                fragmentedMiB = GC.GetGCMemoryInfo().FragmentedBytes / 1048576.0, allocatedMiB = GC.GetTotalAllocatedBytes() / 1048576.0,
                systemDark = ThemeManager.IsDark, scanSeconds = result!.Elapsed.TotalSeconds, files = result.Store[0].Files,
                scanPreviews = scanPreviewCount, labelledTiles = Map.LabelledTileCount, unlabelledTiles = Map.UnlabelledTileCount,
                liveMonitoring = liveChanges is not null, renderedFrames = Map.RenderedFrames,
                lastRenderMs = Map.LastRenderMilliseconds }, new JsonSerializerOptions { WriteIndented = true }));
            Check(idleCpu < 15, $"steady idle CPU: {idleCpu:F2}% of one logical core");
            Notify("验证完成");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(directory, "ui-failure.txt"), ex.ToString());
        }
        finally { Close(); }

        async Task RenderAsync(string file)
        {
            UpdateLayout();
            await Map.WaitForRenderAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(120);
            var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, file)); encoder.Save(stream);
        }
    }
}
