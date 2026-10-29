using System.IO;
using System.Windows.Controls;

namespace SpaceLens.App;

public partial class MainWindow
{
    internal async Task VerifyDeletionAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<string>();
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        string fixture = Path.Combine(Path.GetFullPath(directory), "delete-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            string file = Path.Combine(fixture, "测试 file.txt"), folder = Path.Combine(fixture, "测试 folder"), keep = Path.Combine(fixture, "保留.txt");
            File.WriteAllText(file, "test"); File.WriteAllText(keep, "keep");
            Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "child.txt"), "child");
            bool rootRejected = false, outsideRejected = false;
            try { DiskDeletion.Capture(fixture, fixture); } catch (IOException) { rootRejected = true; }
            try { DiskDeletion.Capture(keep, folder); } catch (IOException) { outsideRejected = true; }
            Check(rootRejected && outsideRejected, "扫描根目录和范围外路径不可删除");
            var stale = DiskDeletion.Capture(file, fixture) with { Created = DateTime.MinValue };
            bool staleRejected = false, executed = false;
            try { await DiskDeletion.DeleteAsync(stale, true, (_, _) => executed = true); } catch (IOException) { staleRejected = true; }
            Check(staleRejected && !executed && File.Exists(file), "变化后的目标不会进入删除后端");
            await StartScanAsync(fixture); StopLive(); liveEnabled = false;
            var original = result!;
            int id = original.Store.FindPath(file), folderId = original.Store.FindPath(folder);
            foreach (int target in new[] { id, folderId })
            {
                var menu = new ContextMenu(); AddDeletionItems(menu, target);
                Check(menu.Items.OfType<MenuItem>().Count(x => x.IsEnabled && x.Header.ToString()!.Contains("删除")) == 2, "文件和文件夹均提供两种删除方式");
            }
            var rootMenu = new ContextMenu(); AddDeletionItems(rootMenu, 0);
            Check(rootMenu.Items.OfType<MenuItem>().All(x => !x.IsEnabled), "根目录菜单禁用删除");
            await DeleteDiskItemAsync(file, fixture, true, original, (_, _) => false, (_, _) => executed = true);
            Check(!executed && File.Exists(file), "取消确认不删除文件");
            SelectNode(original.Store.FindPath(keep));
            bool recycle = false;
            string recycleFixture = Path.Combine(Path.GetFullPath(directory), "recycled-" + Guid.NewGuid().ToString("N") + ".txt");
            await DeleteDiskItemAsync(file, fixture, false, original, (_, _) => true, (target, permanent) =>
            {
                recycle = !permanent;
                if (target.Path != file) throw new InvalidOperationException("删除目标被选中项改变");
                File.Move(target.Path, recycleFixture);
            });
            Check(recycle && File.Exists(keep) && !File.Exists(file) && File.Exists(recycleFixture), "回收请求保持原路径且不受当前选中项影响（后端替身）");
            Check(!original.Store.IsActive(id), "关闭实时更新时也移除树图中的已删除文件");
            await DeleteDiskItemAsync(folder, fixture, true, original, (_, _) => true);
            Check(!Directory.Exists(folder) && File.Exists(keep) && !original.Store.IsActive(folderId), "真实彻底删除仅清理测试文件夹及其子文件并更新树图");
            await DeleteDiskItemAsync(keep, fixture, true, original, (_, _) => true);
            Check(!File.Exists(keep) && !deleting && original.Store[0].Bytes == 0 && FilesMetric.Text == "0", "真实彻底删除测试文件并更新汇总、恢复操作状态");
            File.WriteAllLines(Path.Combine(directory, "deletion-passed.txt"), checks);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "deletion-failure.txt"), ex.ToString()); Environment.ExitCode = 1; }
        finally { Close(); }
    }
}
