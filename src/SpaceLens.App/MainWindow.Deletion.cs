using System.IO;
using System.Windows;
using System.Windows.Controls;
using SpaceLens.Core;

namespace SpaceLens.App;

public partial class MainWindow
{
    private bool deleting;

    private void AddDeletionItems(ContextMenu menu, int id)
    {
        if (result is null) return;
        // Freeze the path now: a later selection or live refresh must never retarget a click.
        string path = result.Store.FullPath(id), root = result.Store.FullPath(0);
        var capturedResult = result;
        menu.Items.Add(new Separator());
        foreach (bool permanent in new[] { false, true })
        {
            var item = new MenuItem { Header = permanent ? "彻底删除…" : "删除到回收站…", IsEnabled = id != 0 && !deleting && scanControl is null };
            item.Click += async (_, _) => await DeleteDiskItemAsync(path, root, permanent, capturedResult);
            menu.Items.Add(item);
        }
    }

    private async Task DeleteDiskItemAsync(string path, string root, bool permanent, ScanResult original,
        Func<string, string, bool>? confirm = null, Action<DiskDeletionTarget, bool>? execute = null)
    {
        if (deleting || scanControl is not null) return;
        try
        {
            var target = DiskDeletion.Capture(path, root);
            string title = permanent ? "彻底删除" : "删除到回收站";
            string question = permanent ? $"彻底删除以下{(target.Directory ? "文件夹及其全部内容" : "文件")}？此操作无法从回收站恢复。\n\n{target.Path}"
                : $"将以下{(target.Directory ? "文件夹及其全部内容" : "文件")}移到回收站？\n\n{target.Path}";
            bool accepted = confirm?.Invoke(question, title) ?? MessageBox.Show(this, question, title, MessageBoxButton.YesNo,
                permanent ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!accepted) return;
            deleting = true;
            Notify("正在删除…");
            try { await DiskDeletion.DeleteAsync(target, permanent, execute); }
            finally
            {
                // Refresh even after a partial failure; deleting a populated folder is not atomic.
                if (!closing && ReferenceEquals(result, original))
                {
                    if (liveChanges is not null) { liveChanges.Record(path, true); await UpdateLiveAsync(force: true); }
                    else
                    {
                        var patch = await Task.Run(() => { using var control = new ScanControl(); return LiveUpdate.Read(original.Store, new ChangeBatch([new(path, true)], false), control); });
                        if (!closing && ReferenceEquals(result, original))
                        {
                            patch.Apply(original.Store);
                            if (!original.Store.IsActive(current)) current = 0;
                            if (selected >= 0 && !original.Store.IsActive(selected)) selected = -1;
                            foreach (var history in new[] { backHistory, forwardHistory })
                            {
                                var remaining = history.Where(original.Store.IsActive).Reverse().ToArray();
                                history.Clear(); foreach (int node in remaining) history.Push(node);
                            }
                            BackButton.IsEnabled = backHistory.Count > 0; ForwardButton.IsEnabled = forwardHistory.Count > 0;
                            SizeMetric.Text = DisplayFormat.Size(original.Store[0].Bytes);
                            FilesMetric.Text = original.Store[0].Files.ToString("N0");
                            FoldersMetric.Text = $"{original.Store[0].Folders:N0} 个文件夹";
                            TimeMetric.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
                            await UpdateViewAsync();
                        }
                    }
                }
            }
            Notify(permanent ? "已彻底删除" : "删除操作完成");
        }
        catch (OperationCanceledException) { Notify("已取消删除"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException or System.Security.SecurityException)
        { Notify("删除未完成：" + ex.Message); }
        finally { deleting = false; }
    }
}
