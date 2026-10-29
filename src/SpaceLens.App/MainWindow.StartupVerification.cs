using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpaceLens.App;

public partial class MainWindow
{
    internal bool VerifyBeforeFirstFrame(string directory)
    {
        Directory.CreateDirectory(directory);
        if (taskMode && TaskView.Visibility == Visibility.Visible && DiskToolbar.Visibility == Visibility.Collapsed) return true;
        File.WriteAllText(Path.Combine(directory, "startup-failure.txt"), "任务管理器必须在窗口首次显示之前准备好，不能等 Loaded 后再切换。\n");
        return false;
    }

    internal async Task VerifyStartupAsync(string directory)
    {
        try
        {
            if (RenderOptions.ProcessRenderMode != System.Windows.Interop.RenderMode.SoftwareOnly)
                throw new InvalidOperationException("首次启动未启用兼容绘制模式");
            foreach (var theme in new[] { AppThemeMode.Dark, AppThemeMode.Light })
            foreach (int width in new[] { 1000, 1440 })
            {
                ThemeManager.SetMode(theme);
                Width = width; Height = 800;
                UpdateLayout();
                await TaskView.WaitForMapAsync();
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (!TaskView.IsVisible || !DiskModeButton.IsVisible || !TaskModeButton.IsVisible || DiskToolbar.IsVisible)
                    throw new InvalidOperationException("首次启动工具栏不完整");
                var content = (FrameworkElement)Content;
                foreach (double scale in new[] { 1.0, 1.5, 2.0 })
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(content.ActualWidth * scale), (int)Math.Ceiling(content.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(content);
                    var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                    bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                    int white = 0, count = 0;
                    for (int y = 0; y < (int)(120 * scale); y++)
                    for (int x = 0; x < bitmap.PixelWidth; x++)
                    {
                        int p = (y * bitmap.PixelWidth + x) * 4;
                        if (pixels[p] > 245 && pixels[p + 1] > 245 && pixels[p + 2] > 245) white++;
                        count++;
                    }
                    if (theme == AppThemeMode.Dark && white > count / 10) throw new InvalidOperationException("顶部出现大面积白块");
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output = File.Create(Path.Combine(directory, $"startup-{theme}-{width}-{scale:0.0}.png")); encoder.Save(output);
                }
            }
            File.WriteAllText(Path.Combine(directory, "startup-passed.txt"), "PASS: 首帧模式、顶部按钮、深浅主题、1000/1440宽度、100%/150%/200%栅格渲染。\n");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "startup-failure.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }
    }
}
