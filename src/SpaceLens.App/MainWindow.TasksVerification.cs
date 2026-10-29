using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpaceLens.App;

public partial class MainWindow
{
    internal async Task VerifyTasksAsync(string directory)
    {
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        try
        {
            await Task.Delay(300);
            string fixture = Path.Combine(directory, "disk-fixture");
            Directory.CreateDirectory(fixture);
            File.WriteAllBytes(Path.Combine(fixture, "document.bin"), new byte[16384]);
            await StartScanAsync(fixture);
            await Map.WaitForRenderAsync();
            var disk = result;
            double diskHeight = Map.ActualHeight;
            SetTaskMode(true);
            Check(Title == "SpaceLens" && TaskModeButton.TranslatePoint(new Point(), this).X < DiskModeButton.TranslatePoint(new Point(), this).X,
                "SpaceLens title and task-first tab order");
            await TaskView.VerifyTasksAsync(directory, Check);
            Check(Math.Abs(TaskView.MapHeight - diskHeight) < 1, "task mode retains full-window map height and toolbar structure");
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            {
                ThemeManager.SetMode(theme);
                await SaveFrame("tasks-" + theme.ToString().ToLowerInvariant() + ".png");
            }
            Width = 1040; Height = 650;
            await SaveFrame("tasks-compact.png");
            Check(TaskView.ActualWidth >= 1000 && TaskView.MapHeight > 400, "compact task window retains usable map area");
            SetTaskMode(false);
            Check(ReferenceEquals(disk, result) && result?.Store[0].Bytes == 16384 && DiskMapPanel.IsVisible && !TaskView.IsVisible, "returning to disk preserves scan result and restores original view");
            Check(!TaskView.IsSampling, "leaving task mode stops process sampling");
            SetTaskMode(true); WindowState = WindowState.Minimized;
            Check(!TaskView.IsSampling, "minimized window stops process sampling");
            WindowState = WindowState.Normal;
            Check(TaskView.IsSampling, "restoring window resumes process sampling");
            File.WriteAllText(Path.Combine(directory, "tasks-passed.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "tasks-failure.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }

        async Task SaveFrame(string file)
        {
            UpdateLayout(); await TaskView.WaitForMapAsync();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var visual = (FrameworkElement)Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = File.Create(Path.Combine(directory, file)); encoder.Save(output);
        }
    }
}
