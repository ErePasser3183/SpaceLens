using System.Globalization;

namespace SpaceLens.Core;

[Flags]
public enum NodeFlags : byte { None = 0, Directory = 1, Link = 2, Unreadable = 4, Removed = 8 }

// Read snapshot. Storage keeps directory-only fields separately, rather than paying for them on every file.
public struct DiskNode
{
    public string Name;
    public long Bytes;
    public long ModifiedTicks;
    public int Parent, FirstChild, NextSibling, Files, Folders;
    public NodeFlags Flags;
    public bool IsDirectory => (Flags & NodeFlags.Directory) != 0;
}

public sealed class NodeStore
{
    private const int Shift = 13, ChunkSize = 1 << Shift, Mask = ChunkSize - 1;
    private struct StoredNode
    {
        public string Name;
        public long Bytes, ModifiedTicks;
        public int Parent, NextSibling, KindAndDirectory;
        public NodeFlags Flags { readonly get => (NodeFlags)((uint)KindAndDirectory >> 28); set => KindAndDirectory = (KindAndDirectory & 0x0fffffff) | ((int)value << 28); }
        public readonly int DirectoryIndex => (KindAndDirectory & 0x0fffffff) - 1;
    }
    private struct DirectoryData { public int FirstChild, Files, Folders; }
    private readonly List<StoredNode[]> chunks = [];
    private readonly List<DirectoryData[]> directories = [];
    private int directoryCount;
    private int[]? nameSlots;
    private int sharedNames;
    private readonly string?[] nameCache = new string?[65536];
    public object SyncRoot { get; } = new();
    public int Count { get; private set; }
    public int RemovedCount { get; private set; }
    public int LinkCount { get; private set; }
    public NodeStore(bool shareNames = false) { if (shareNames) nameSlots = new int[1024]; }
    private ref StoredNode Node(int id) => ref chunks[id >> Shift][id & Mask];
    private ref DirectoryData Directory(int index) => ref directories[index >> Shift][index & Mask];
    public DiskNode this[int index]
    {
        get
        {
            ref var node = ref Node(index);
            var flags = node.Flags;
            var detail = node.DirectoryIndex >= 0 ? Directory(node.DirectoryIndex) : new DirectoryData { FirstChild = -1, Files = (flags & NodeFlags.Link) == 0 ? 1 : 0 };
            return new DiskNode { Name = node.Name, Bytes = node.Bytes, ModifiedTicks = node.ModifiedTicks,
                Parent = node.Parent, NextSibling = node.NextSibling, FirstChild = detail.FirstChild, Files = detail.Files, Folders = detail.Folders, Flags = flags };
        }
    }
    public long StorageBytes => (long)chunks.Count * ChunkSize * System.Runtime.CompilerServices.Unsafe.SizeOf<StoredNode>()
        + (long)directories.Count * ChunkSize * System.Runtime.CompilerServices.Unsafe.SizeOf<DirectoryData>();
    public void FinishBuilding() => nameSlots = null;
    private string ShareBuildingName(string name, int id)
    {
        var slots = nameSlots!;
        if (sharedNames >= slots.Length * .7 && slots.Length < 1 << 21)
        {
            var grown = new int[slots.Length * 2];
            foreach (int entry in slots)
            {
                if (entry == 0) continue;
                int bucket = StringComparer.Ordinal.GetHashCode(Node(entry - 1).Name) & (grown.Length - 1);
                while (grown[bucket] != 0) bucket = (bucket + 1) & (grown.Length - 1);
                grown[bucket] = entry;
            }
            nameSlots = slots = grown;
        }
        int slot = StringComparer.Ordinal.GetHashCode(name) & (slots.Length - 1);
        while (slots[slot] != 0)
        {
            string existing = Node(slots[slot] - 1).Name;
            if (string.Equals(existing, name, StringComparison.Ordinal)) return existing;
            slot = (slot + 1) & (slots.Length - 1);
        }
        // Bound the temporary index; very large scans still work after the sharing budget is full.
        if (sharedNames < slots.Length * .7) { slots[slot] = id + 1; sharedNames++; }
        return name;
    }
    public int Add(string name, int parent, long bytes, long modified, NodeFlags flags)
    {
        // A fixed-size direct-mapped cache shares common names without an unbounded intern table.
        // Writers and background queries share SyncRoot after the initial scan.
        if (nameSlots is null && name.Length <= 96)
        {
            int slot = StringComparer.Ordinal.GetHashCode(name) & (nameCache.Length - 1);
            if (string.Equals(nameCache[slot], name, StringComparison.Ordinal)) name = nameCache[slot]!;
            else nameCache[slot] = name;
        }
        if ((Count & Mask) == 0) chunks.Add(new StoredNode[ChunkSize]);
        int index = Count++;
        if (nameSlots is not null) name = ShareBuildingName(name, index);
        int directory = -1;
        if ((flags & NodeFlags.Directory) != 0)
        {
            if (directoryCount >= 0x0ffffffe) throw new InvalidOperationException("目录数量超过支持范围。");
            if ((directoryCount & Mask) == 0) directories.Add(new DirectoryData[ChunkSize]);
            directory = directoryCount++;
            Directory(directory) = new DirectoryData { FirstChild = -1 };
        }
        if ((flags & NodeFlags.Link) != 0) LinkCount++;
        Node(index) = new StoredNode { Name = name, Parent = parent, Bytes = bytes, ModifiedTicks = modified,
            KindAndDirectory = directory + 1, Flags = flags, NextSibling = parent >= 0 ? Directory(Node(parent).DirectoryIndex).FirstChild : -1 };
        if (parent >= 0) Directory(Node(parent).DirectoryIndex).FirstChild = index;
        return index;
    }
    public IEnumerable<int> Children(int parent)
    {
        int directory = Node(parent).DirectoryIndex;
        for (int id = directory >= 0 ? Directory(directory).FirstChild : -1; id >= 0; id = Node(id).NextSibling) yield return id;
    }
    public bool IsActive(int id) => id >= 0 && id < Count && (this[id].Flags & NodeFlags.Removed) == 0;
    public void MarkLink(int id) { if ((Node(id).Flags & NodeFlags.Link) == 0) { Node(id).Flags |= NodeFlags.Link; LinkCount++; } }
    public void MarkUnreadable(int id) => Node(id).Flags |= NodeFlags.Unreadable;
    public void UpdateMetadata(int id, string name, long bytes, long modifiedTicks)
    { ref var node = ref Node(id); node.Name = name; node.Bytes = bytes; node.ModifiedTicks = modifiedTicks; }
    public int FindPath(string path)
    {
        string relative = Path.GetRelativePath(this[0].Name, path);
        if (relative == ".") return 0;
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) return -1;
        int id = 0;
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            int next = -1;
            foreach (int child in Children(id))
                if (string.Equals(this[child].Name, part, StringComparison.OrdinalIgnoreCase)) { next = child; break; }
            if (next < 0) return -1;
            id = next;
        }
        return id;
    }
    public void AdjustAncestors(int id, long bytes, int files, int folders)
    {
        for (; id >= 0; id = this[id].Parent)
        {
            ref var node = ref Node(id); node.Bytes += bytes;
            ref var detail = ref Directory(node.DirectoryIndex); detail.Files += files; detail.Folders += folders;
        }
    }
    public void RemoveTree(int id)
    {
        if (id <= 0 || !IsActive(id)) return;
        var removed = this[id];
        AdjustAncestors(removed.Parent, -removed.Bytes, -removed.Files, -removed.Folders - (removed.IsDirectory ? 1 : 0));
        int previous = -1;
        foreach (int child in Children(removed.Parent))
        {
            if (child == id)
            {
                if (previous < 0) Directory(Node(removed.Parent).DirectoryIndex).FirstChild = removed.NextSibling;
                else Node(previous).NextSibling = removed.NextSibling;
                break;
            }
            previous = child;
        }
        var pending = new Stack<int>(); pending.Push(id);
        while (pending.TryPop(out int node))
        {
            foreach (int child in Children(node)) pending.Push(child);
            if ((this[node].Flags & NodeFlags.Link) != 0) LinkCount--;
            Node(node).Flags |= NodeFlags.Removed;
            Node(node).Name = "";
            if (Node(node).DirectoryIndex >= 0) Directory(Node(node).DirectoryIndex).FirstChild = -1;
            RemovedCount++;
        }
    }
    public int ImportTree(int parent, NodeStore source, string name)
    {
        var ids = new int[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            var n = source[i];
            int id = Add(i == 0 ? name : n.Name, i == 0 ? parent : ids[n.Parent], n.Bytes, n.ModifiedTicks, n.Flags);
            if (n.IsDirectory) { ref var detail = ref Directory(Node(id).DirectoryIndex); detail.Files = n.Files; detail.Folders = n.Folders; }
            ids[i] = id;
        }
        var root = source[0];
        AdjustAncestors(parent, root.Bytes, root.Files, root.Folders + 1);
        return ids[0];
    }
    public NodeStore Compact()
    {
        var next = new NodeStore();
        var pending = new Stack<(int Old, int Parent)>(); pending.Push((0, -1));
        while (pending.TryPop(out var item))
        {
            var n = this[item.Old];
            int id = next.Add(n.Name, item.Parent, n.Bytes, n.ModifiedTicks, n.Flags);
            if (n.IsDirectory) { ref var detail = ref next.Directory(next.Node(id).DirectoryIndex); detail.Files = n.Files; detail.Folders = n.Folders; }
            foreach (int child in Children(item.Old)) pending.Push((child, id));
        }
        return next;
    }
    public string FullPath(int index)
    {
        string root = Node(0).Name;
        if (index == 0) return root;
        int length = root.Length - (Path.EndsInDirectorySeparator(root) ? 1 : 0);
        for (int id = index; id > 0; id = Node(id).Parent) length += Node(id).Name.Length + 1;
        // Allocate the final path once; repeated Path.Combine created every intermediate prefix
        // for every directory visited by the scanner.
        return string.Create(length, (Store: this, Index: index, Root: root), static (destination, state) =>
        {
            int end = destination.Length;
            for (int id = state.Index; id > 0; id = state.Store.Node(id).Parent)
            {
                string name = state.Store.Node(id).Name;
                end -= name.Length; name.AsSpan().CopyTo(destination[end..]);
                if (end > state.Root.Length) destination[--end] = Path.DirectorySeparatorChar;
            }
            state.Root.AsSpan().CopyTo(destination);
        });
    }
    // Parent is always created before child, so a reverse pass replaces recursive aggregation.
    public void Aggregate()
    {
        for (int i = Count - 1; i > 0; i--)
        {
            var node = this[i];
            Node(node.Parent).Bytes += node.Bytes;
            ref var detail = ref Directory(Node(node.Parent).DirectoryIndex);
            detail.Files += node.Files;
            detail.Folders += node.Folders + (node.IsDirectory ? 1 : 0);
        }
    }
}

public sealed record ScanIssue(string Path, string Reason);
public sealed record ScanProgress(long Files, long Folders, long Bytes, int IssueCount, string CurrentPath, TimeSpan Elapsed);
public sealed record ScanPreviewSettings(int Depth = 3, int IntervalMilliseconds = 120, IReadOnlyDictionary<int, DirectoryDetail>? ExpandedDepths = null);
public sealed record ScanResult(NodeStore Store, IReadOnlyList<ScanIssue> Issues, int IssueCount, int SkippedLinks,
    bool Cancelled, TimeSpan Elapsed, DateTime StartedUtc);

public sealed class ScanControl : IDisposable
{
    private readonly ManualResetEventSlim gate = new(true);
    private readonly CancellationTokenSource source = new();
    public CancellationToken Token => source.Token;
    public bool IsPaused { get; private set; }
    public void Pause() { gate.Reset(); IsPaused = true; }
    public void Resume() { IsPaused = false; gate.Set(); }
    public void Cancel() { source.Cancel(); gate.Set(); }
    public void Checkpoint() { Token.ThrowIfCancellationRequested(); gate.Wait(Token); }
    public void Dispose() { source.Dispose(); gate.Dispose(); }
}

public static class DisplayFormat
{
    public static string Size(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double value = bytes; int u = 0;
        while (value >= 1024 && u < units.Length - 1) { value /= 1024; u++; }
        return $"{value.ToString(u == 0 ? "N0" : value >= 100 ? "N1" : "N2", CultureInfo.InvariantCulture)} {units[u]}";
    }
    public static string Duration(TimeSpan time) => time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} 分 {time.Seconds} 秒" : $"{time.TotalSeconds:F1} 秒";
    public static string Category(string name)
    {
        return Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".m4v" => "视频",
            ".wav" or ".mp3" or ".flac" or ".aac" or ".ogg" or ".m4a" or ".mid" => "音频",
            ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".psd" or ".svg" or ".heic" => "图片",
            ".zip" or ".7z" or ".rar" or ".tar" or ".gz" or ".iso" => "归档",
            ".exe" or ".dll" or ".msi" or ".sys" or ".pck" or ".pak" or ".jar" => "程序",
            ".doc" or ".docx" or ".pdf" or ".txt" or ".md" or ".xlsx" or ".pptx" or ".csv" => "文档",
            ".cs" or ".cpp" or ".h" or ".py" or ".js" or ".ts" or ".json" or ".html" or ".css" => "代码",
            _ => "其他"
        };
    }
}
