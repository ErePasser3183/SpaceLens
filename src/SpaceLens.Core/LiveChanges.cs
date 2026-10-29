namespace SpaceLens.Core;

public sealed record PathChange(string Path, bool Structural);
public sealed record ChangeBatch(IReadOnlyList<PathChange> Changes, bool Rescan);

// One OS subscription per root. Event bursts consume bounded memory and never run disk I/O here.
public sealed class LiveChanges : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, bool> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly FileSystemWatcher watcher;
    private long first, last;
    private bool overflow, disposed;
    public string? Failure { get; private set; }
    public LiveChanges(string path)
    {
        watcher = new FileSystemWatcher(path)
        {
            IncludeSubdirectories = true, InternalBufferSize = 32768,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.Attributes
        };
        watcher.Created += (_, e) => Record(e.FullPath, true);
        watcher.Deleted += (_, e) => Record(e.FullPath, true);
        watcher.Changed += (_, e) => Record(e.FullPath, false);
        watcher.Renamed += (_, e) => { Record(e.OldFullPath, true); Record(e.FullPath, true); };
        watcher.Error += (_, e) =>
        {
            if (e.GetException() is InternalBufferOverflowException) RequestReconcile();
            else { lock (gate) Failure = e.GetException().Message; }
        };
        try { watcher.EnableRaisingEvents = true; }
        catch { watcher.Dispose(); throw; }
    }
    public void Record(string path, bool structural)
    {
        lock (gate)
        {
            if (disposed) return;
            if (pending.Count == 0 && !overflow) first = Environment.TickCount64;
            last = Environment.TickCount64;
            if (overflow) return;
            pending.TryGetValue(path, out bool previous);
            pending.Remove(path); // Keep the latest casing from a case-only rename.
            pending[path] = previous || structural;
            if (pending.Count > 4096) { pending.Clear(); overflow = true; }
        }
    }
    public void RequestReconcile()
    {
        lock (gate)
        {
            if (disposed) return;
            if (pending.Count == 0 && !overflow) first = Environment.TickCount64;
            pending.Clear(); overflow = true; last = Environment.TickCount64;
        }
    }
    public ChangeBatch? TakeReady(bool force = false)
    {
        lock (gate)
        {
            if (disposed || (!overflow && pending.Count == 0)) return null;
            long now = Environment.TickCount64;
            if (!force && now - last < 600 && now - first < 2000) return null;
            var batch = new ChangeBatch(pending.Select(p => new PathChange(p.Key, p.Value)).ToArray(), overflow);
            pending.Clear(); overflow = false;
            return batch;
        }
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; pending.Clear(); }
        watcher.Dispose();
    }
}

public sealed record EntryUpdate(string Path, DiskNode? Metadata, NodeStore? Subtree);
public sealed record LiveUpdate(IReadOnlyList<EntryUpdate> Entries, IReadOnlyList<ScanIssue> Issues,
    int IssueCount, int SkippedLinks, ScanResult? Replacement = null)
{
    public static LiveUpdate Read(NodeStore store, ChangeBatch batch, ScanControl control, bool lowImpact = false)
    {
        string root = store[0].Name;
        if (batch.Rescan)
        {
            var scan = new DiskScanner().Scan(root, control, lowImpact: lowImpact);
            return new LiveUpdate([], [], 0, 0, scan);
        }
        var updates = new List<EntryUpdate>(); var issues = new List<ScanIssue>();
        var covered = new List<string>();
        int issueCount = 0, links = 0;
        foreach (var change in batch.Changes.OrderBy(x => x.Path.Length))
        {
            control.Checkpoint();
            string path = Path.GetFullPath(change.Path);
            string relative = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) continue;
            if (relative == ".") continue;
            int existing;
            bool skip = false;
            lock (store.SyncRoot)
            {
                // Promote a new nested file to its first unknown ancestor so new folders arrive as one subtree.
                string? parent = Path.GetDirectoryName(path);
                while (parent is not null)
                {
                    int parentId = store.FindPath(parent);
                    if (parentId >= 0)
                    {
                        for (int id = parentId; id >= 0; id = store[id].Parent)
                            if ((store[id].Flags & NodeFlags.Link) != 0) skip = true;
                        break;
                    }
                    path = parent; parent = Path.GetDirectoryName(path);
                }
                existing = store.FindPath(path);
            }
            if (skip || covered.Any(p => SameOrInside(path, p))) continue;
            try
            {
                // Never follow an ancestor that changed into a junction since the previous scan.
                for (string? parent = Path.GetDirectoryName(path); parent is not null && SameOrInside(parent, root); parent = Path.GetDirectoryName(parent))
                    if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) { skip = true; break; }
                if (skip) continue;
                var info = new FileInfo(path); var attributes = File.GetAttributes(path);
                bool directory = (attributes & FileAttributes.Directory) != 0;
                bool link = (attributes & FileAttributes.ReparsePoint) != 0;
                DiskNode? old = existing >= 0 ? store[existing] : null;
                if (directory && !link && (old is null || !old.Value.IsDirectory || (old.Value.Flags & NodeFlags.Link) != 0 || change.Structural))
                {
                    var scan = new DiskScanner().Scan(path, control, lowImpact: lowImpact);
                    control.Token.ThrowIfCancellationRequested();
                    updates.Add(new EntryUpdate(path, null, scan.Store)); covered.Add(path);
                    issueCount += scan.IssueCount; links += scan.SkippedLinks;
                    issues.AddRange(scan.Issues.Take(Math.Max(0, 200 - issues.Count)));
                }
                else
                {
                    var flags = (directory ? NodeFlags.Directory : NodeFlags.None) | (link ? NodeFlags.Link : NodeFlags.None);
                    var node = new DiskNode { Name = Path.GetFileName(path), Flags = flags, ModifiedTicks = info.LastWriteTimeUtc.Ticks,
                        Bytes = directory || link ? 0 : info.Length, Files = directory || link ? 0 : 1 };
                    updates.Add(new EntryUpdate(path, node, null));
                    if (link) covered.Add(path);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // A missing ancestor means the old subtree is gone; later queued events can restore recreated content.
                updates.Add(new EntryUpdate(path, null, null)); covered.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                issueCount++; if (issues.Count < 200) issues.Add(new ScanIssue(path, ex.Message));
            }
        }
        return new LiveUpdate(updates, issues, issueCount, links);
    }
    private static bool SameOrInside(string path, string parent) => string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Applied on the UI thread; background queries share the lock, and no disk I/O runs under it.
    public bool Apply(NodeStore store)
    {
        bool changed = false;
        lock (store.SyncRoot)
        {
            foreach (var update in Entries)
            {
                int id = store.FindPath(update.Path);
                if (id == 0) continue;
                int parent = store.FindPath(Path.GetDirectoryName(update.Path)!);
                if (update.Subtree is not null)
                {
                    if (parent < 0) continue;
                    if (id >= 0) store.RemoveTree(id);
                    store.ImportTree(parent, update.Subtree, Path.GetFileName(update.Path)); changed = true;
                }
                else if (update.Metadata is { } next)
                {
                    if (parent < 0) continue;
                    if (id >= 0 && (store[id].Flags & (NodeFlags.Directory | NodeFlags.Link)) != (next.Flags & (NodeFlags.Directory | NodeFlags.Link)))
                    { store.RemoveTree(id); id = -1; }
                    if (id < 0)
                    {
                        id = store.Add(next.Name, parent, next.Bytes, next.ModifiedTicks, next.Flags);
                        store.AdjustAncestors(parent, next.Bytes, next.Files, next.IsDirectory ? 1 : 0); changed = true;
                    }
                    else
                    {
                        var old = store[id];
                        if (old.ModifiedTicks == next.ModifiedTicks && old.Name == next.Name && (old.IsDirectory || old.Bytes == next.Bytes)) continue;
                        if (!old.IsDirectory) store.AdjustAncestors(parent, next.Bytes - old.Bytes, next.Files - old.Files, 0);
                        store.UpdateMetadata(id, next.Name, old.IsDirectory ? old.Bytes : next.Bytes, next.ModifiedTicks); changed = true;
                    }
                }
                else if (id > 0) { store.RemoveTree(id); changed = true; }
            }
        }
        return changed;
    }
}
