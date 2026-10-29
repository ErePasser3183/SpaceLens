using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace SpaceLens.App;

internal sealed record DiskDeletionTarget(string Path, string Root, bool Directory, DateTime Created);

internal static class DiskDeletion
{
    internal static DiskDeletionTarget Capture(string path, string scanRoot)
    {
        path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        scanRoot = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(scanRoot));
        string prefix = scanRoot.EndsWith(System.IO.Path.DirectorySeparatorChar) ? scanRoot : scanRoot + System.IO.Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || path.Equals(scanRoot, StringComparison.OrdinalIgnoreCase))
            throw new IOException("只能删除当前扫描目录内的文件或子文件夹。");
        // Do not resolve a stale selection through a newly inserted junction or symbolic link.
        for (string? parent = path; parent is not null; parent = System.IO.Path.GetDirectoryName(parent))
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("此路径包含目录链接，请在资源管理器中处理。");
        }
        bool directory = (File.GetAttributes(path) & FileAttributes.Directory) != 0;
        return new(path, scanRoot, directory, directory ? System.IO.Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path));
    }

    internal static Task DeleteAsync(DiskDeletionTarget target, bool permanent, Action<DiskDeletionTarget, bool>? execute = null)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                if (Capture(target.Path, target.Root) != target) throw new IOException("文件已发生变化，请重新选择后再删除。");
                (execute ?? DeleteWithShell)(target, permanent);
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "SpaceLens file operation" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private static void DeleteWithShell(DiskDeletionTarget target, bool permanent)
    {
        // Keep shell prompts for recycling: Windows may need explicit consent when a volume has no recycle bin.
        var ui = permanent ? UIOption.OnlyErrorDialogs : UIOption.AllDialogs;
        var recycle = permanent ? RecycleOption.DeletePermanently : RecycleOption.SendToRecycleBin;
        if (target.Directory) FileSystem.DeleteDirectory(target.Path, ui, recycle, UICancelOption.ThrowException);
        else FileSystem.DeleteFile(target.Path, ui, recycle, UICancelOption.ThrowException);
    }
}
