using System.ComponentModel;
using System.Windows;
using SpaceLens.Core;
using SpaceLens.Tasks;

namespace SpaceLens.App;

public partial class TaskManagerView
{
    private bool optimizingMemory;
    private string? memoryNotice;

    private async void OptimizeMemoryClick(object sender, RoutedEventArgs e)
        => await OptimizeMemoryAsync(MemoryOptimizer.OptimizeAsync);

    private async Task OptimizeMemoryAsync(Func<Task<MemoryOptimizationResult>> optimize)
    {
        if (optimizingMemory) return;
        optimizingMemory = true;
        TaskOptimizeMemory.IsEnabled = false;
        TaskOptimizeMemory.Content = "优化中…";
        memoryNotice = "正在优化内存…";
        UpdateRefreshState();
        try
        {
            var result = await optimize();
            memoryNotice = FormatMemoryResult(result);
            await RefreshAsync();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { memoryNotice = "已取消内存优化"; }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException or System.IO.IOException or System.Security.SecurityException)
        { memoryNotice = "内存优化失败：" + ex.Message; }
        finally
        {
            optimizingMemory = false;
            TaskOptimizeMemory.IsEnabled = true;
            TaskOptimizeMemory.Content = "内存优化";
            UpdateRefreshState();
        }
    }

    private static string FormatMemoryResult(MemoryOptimizationResult result)
    {
        if (result.Skipped) return "未检测到内存不足，已跳过优化";
        string available = result.After is { } after ? $" · 当前可用 {DisplayFormat.Size((long)after.Available)}" : "";
        string change = result.AvailableChange is { } delta
            ? delta == 0 ? " · 可用内存无变化" : $" · 可用内存{(delta > 0 ? "增加" : "减少")} {DisplayFormat.Size(Math.Abs(delta))}"
            : " · 暂无法读取内存变化";
        return "优化完成" + change + available;
    }

    private void UpdateRefreshState()
    {
        TaskRefreshState.Text = memoryNotice ?? (snapshot is null ? "正在读取进程…" : $"每 2 秒更新 · {snapshot.CapturedAt.LocalDateTime:HH:mm:ss}");
        TaskRefreshState.ToolTip = TaskRefreshState.Text;
    }
}
