using System.Windows;

namespace SpaceLens.App;

public partial class MainWindow
{
    private bool taskMode;
    private void DiskModeClick(object sender, RoutedEventArgs e) => SetTaskMode(false);
    private void TaskModeClick(object sender, RoutedEventArgs e) => SetTaskMode(true);
    internal void SetTaskMode(bool tasks)
    {
        taskMode = tasks;
        foreach (var panel in new UIElement[] { DiskToolbar, DiskFilter, DiskMapPanel, DiskFooter })
            panel.Visibility = tasks ? Visibility.Collapsed : Visibility.Visible;
        ScanPanel.Visibility = !tasks && scanControl is not null ? Visibility.Visible : Visibility.Collapsed;
        TaskView.Visibility = tasks ? Visibility.Visible : Visibility.Collapsed;
        TaskView.SetActive(tasks && WindowState != WindowState.Minimized);
        DiskModeButton.SetResourceReference(BackgroundProperty, tasks ? "WindowBg" : "Selected");
        TaskModeButton.SetResourceReference(BackgroundProperty, tasks ? "Selected" : "WindowBg");
        Title = "SpaceLens";
    }
}
