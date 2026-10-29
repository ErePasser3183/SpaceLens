using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SpaceLens.App;

public partial class MainWindow
{
    private void SettingsClick(object sender, RoutedEventArgs e) => OpenSettings((FrameworkElement)sender);

    private void OpenSettings(FrameworkElement target) => BuildSettingsMenu(target).IsOpen = true;

    private ContextMenu BuildSettingsMenu(FrameworkElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        AddSection("主题");
        AddThemeSettings(menu);
        if (!taskMode) { AddSection("磁盘"); AddDiskSettings(menu); }
        AddSection("更新");
        var updates = ((App)Application.Current).Updates;
        var automatic = new MenuItem { Header = "自动更新", IsCheckable = true, IsChecked = updates?.Automatic ?? true,
            IsEnabled = updates?.Available == true, ToolTip = "后台下载，关闭程序后更新，下次启动生效" };
        automatic.Click += async (_, _) =>
        {
            if (updates is null) return;
            try
            {
                updates.SetAutomatic(automatic.IsChecked);
                if (automatic.IsChecked) await updates.CheckAsync();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            { automatic.IsChecked = updates.Automatic; ShowText("更新设置", "无法保存设置：" + ex.Message); }
        };
        menu.Items.Add(automatic);
        var check = new MenuItem { Header = "检查更新", StaysOpenOnClick = true, IsEnabled = updates?.Available == true && !updates.Busy,
            ToolTip = "检查并下载新版，关闭程序后生效" };
        check.Click += async (_, _) => { if (updates is not null) await updates.CheckAsync(manual: true); };
        menu.Items.Add(check);
        var state = new MenuItem { Header = updates?.Status ?? "开发验证模式", IsEnabled = false, FontSize = 11 };
        menu.Items.Add(state);
        menu.Items.Add(new MenuItem { Header = "版本 " + (updates?.Version ?? "1.1.0"), IsEnabled = false, FontSize = 11 });
        if (updates is not null)
        {
            void Refresh() => Dispatcher.InvokeAsync(() => { state.Header = updates.Status; check.IsEnabled = updates.Available && !updates.Busy; });
            updates.Changed += Refresh;
            menu.Closed += (_, _) => updates.Changed -= Refresh;
        }
        AddSection("帮助");
        var help = new MenuItem { Header = "使用说明" };
        help.Click += HelpClick;
        menu.Items.Add(help);
        return menu;

        void AddSection(string title)
        {
            var heading = new MenuItem { Header = title, IsEnabled = false, FontSize = 11, Padding = new Thickness(9, 4, 9, 2) };
            heading.SetResourceReference(ForegroundProperty, "Muted");
            menu.Items.Add(heading);
        }
    }
}
