using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SpaceLens.App;

public partial class MainWindow
{
    internal async Task VerifyMenusAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var menu = BuildSettingsMenu(SettingsButton);
        var failures = new List<string>();
        try
        {
            foreach (var mode in new[] { AppThemeMode.Dark, AppThemeMode.Light, AppThemeMode.Dark })
            {
                ThemeManager.SetMode(mode);
                menu.IsOpen = true;
                await Task.Delay(150);
                menu.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(menu);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(directory, $"menu-{mode}.png"))) encoder.Save(stream);
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
                bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                var surface = ((SolidColorBrush)Application.Current.Resources["Surface"]).Color;
                // Empty space before the checkmark must use the popup surface, not the OS light gutter.
                int offset = ((bitmap.PixelHeight / 2) * bitmap.PixelWidth + 5) * 4;
                if (Math.Abs(pixels[offset] - surface.B) > 5 || Math.Abs(pixels[offset + 1] - surface.G) > 5 || Math.Abs(pixels[offset + 2] - surface.R) > 5)
                    failures.Add($"{mode}: checkmark gutter differs from menu background ({pixels[offset + 2]},{pixels[offset + 1]},{pixels[offset]})");
                var item = menu.Items.OfType<MenuItem>().First(x => x.IsChecked);
                if (item.Template.FindName("Check", item) is not TextBlock check || check.Visibility != Visibility.Visible || check.Foreground is not SolidColorBrush ink || ink.Color != ((SolidColorBrush)Application.Current.Resources["Ink"]).Color)
                    failures.Add($"{mode}: checked item does not have a visible themed checkmark");
            }
            if (menu.Items.OfType<MenuItem>().Any(x => Equals(x.Header, "视图") || Equals(x.Header, "文件列表    F8"))) failures.Add("View options remain in settings");
            menu.IsOpen = false;
            foreach (var width in new[] { 1440d, 1000d })
            {
                Width = width; Height = 700;
                await Task.Delay(150); UpdateLayout();
                var large = LargestButton.TranslatePoint(new Point(LargestButton.ActualWidth, 0), this);
                var listPoint = ListToggleButton.TranslatePoint(new Point(), this);
                if (listPoint.X < large.X || Math.Abs(listPoint.Y - large.Y) > 2 || SearchBox.ActualWidth < 100)
                    failures.Add($"Toolbar overlaps at {width}");
                if (ChooseButton.TranslatePoint(new Point(), this).X <= DepthText.TranslatePoint(new Point(), this).X)
                    failures.Add("Folder button is not after depth controls");
                ListToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!listVisible || ListPanel.Visibility != Visibility.Visible) failures.Add("Relocated list button failed");
                ListToggleButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                UpdateLayout();
                SaveVisual((FrameworkElement)Content, $"toolbar-{width}.png");
            }
            foreach (var (label, mode) in new[] { ("浅色", AppThemeMode.Light), ("深色", AppThemeMode.Dark), ("跟随系统", AppThemeMode.System) })
            {
                var theme = BuildSettingsMenu(SettingsButton).Items.OfType<MenuItem>().Single(x => Equals(x.Header, label));
                theme.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                if (ThemeManager.Mode != mode) failures.Add($"Settings theme action failed: {label}");
            }
            var help = BuildSettingsMenu(SettingsButton).Items.OfType<MenuItem>().Single(x => Equals(x.Header, "使用说明"));
            help.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            if (OwnedWindows.Count != 1) failures.Add("Settings help did not open");
            foreach (Window child in OwnedWindows) child.Close();
            SetTaskMode(true);
            await Task.Delay(300); UpdateLayout();
            var taskSettings = (Button)TaskView.FindName("TaskSettingsButton");
            var taskMenu = BuildSettingsMenu(taskSettings);
            if (taskMenu.Items.OfType<MenuItem>().Any(x => Equals(x.Header, "进程列表    F8")) ||
                taskMenu.Items.OfType<MenuItem>().Any(x => Equals(x.Header, "实时更新")))
                failures.Add("Task settings show incorrect options");
            ((Button)TaskView.FindName("TaskListButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (((FrameworkElement)TaskView.FindName("TaskListPanel")).Visibility != Visibility.Visible)
                failures.Add("Task list button failed");
            ((Button)TaskView.FindName("TaskListButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (((FrameworkElement)TaskView.FindName("TaskListPanel")).Visibility != Visibility.Collapsed)
                failures.Add("Task settings list toggle failed");
            UpdateLayout(); SaveVisual((FrameworkElement)Content, "toolbar-tasks.png");
            taskMenu.IsOpen = true; await Task.Delay(150); taskMenu.UpdateLayout();
            SaveVisual(taskMenu, "settings-tasks.png"); taskMenu.IsOpen = false;
            SetTaskMode(false);
            void SaveVisual(FrameworkElement element, string name)
            {
                var shot = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth), (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                shot.Render(element);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(shot));
                using var stream = File.Create(Path.Combine(directory, name)); png.Save(stream);
            }
            if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
            File.WriteAllText(Path.Combine(directory, "menu-passed.txt"), "Settings menus, theme actions, help, list toggles and toolbar layout at 1440/1000 passed.");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "menu-failure.txt"), ex.ToString()); }
        finally { menu.IsOpen = false; Close(); }
    }
}
