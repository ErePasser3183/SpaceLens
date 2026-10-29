using System.Text;

namespace SpaceLens.Core;

public sealed record ViewItem(int Id, string Name, long Bytes, bool Directory, string Category, string RelativePath,
    long ModifiedTicks, NodeFlags Flags);
public sealed record MapItem(int Id, string Name, long Bytes, bool Directory, string Category, IReadOnlyList<MapItem>? Children = null,
    string? ValueText = null, string? DetailText = null);
public readonly record struct DirectoryDetail(int Depth, int Revision);
public sealed record ViewResult(IReadOnlyList<ViewItem> Rows, IReadOnlyList<MapItem> Map, long MatchCount, long MatchBytes, bool Filtered);
public sealed record QueryOptions(string Search = "", string Category = "全部类型", long MinimumBytes = 0,
    bool LargestFiles = false, string Sort = "大小 ↓", int Limit = 2000, int MapDepth = 3, int MapLimit = 6000,
    IReadOnlyDictionary<int, DirectoryDetail>? ExpandedDepths = null)
{
    public bool IsFiltered => Search.Length > 0 || Category != "全部类型" || MinimumBytes > 0 || LargestFiles;
}

public static class ViewQuery
{
    public static ViewResult Build(NodeStore store, int root, QueryOptions options, CancellationToken token)
    {
        lock (store.SyncRoot) return BuildLocked(store, root, options, token);
    }
    private static ViewResult BuildLocked(NodeStore store, int root, QueryOptions options, CancellationToken token)
    {
        var best = new PriorityQueue<int, long>();
        int retained = Math.Max(options.Limit, options.MapLimit);
        long count = 0, matchedBytes = 0;
        var pending = new Stack<int>();
        pending.Push(root);
        bool recursive = options.IsFiltered;
        while (pending.TryPop(out int parent))
        {
            foreach (int id in store.Children(parent))
            {
                token.ThrowIfCancellationRequested();
                var node = store[id];
                if (recursive && node.IsDirectory) { pending.Push(id); continue; }
                if (node.Bytes < options.MinimumBytes || !node.Name.Contains(options.Search, StringComparison.OrdinalIgnoreCase)) continue;
                if (options.Category != "全部类型" && DisplayFormat.Category(node.Name) != options.Category) continue;
                count++; matchedBytes += node.Bytes;
                // A bounded heap prevents folders with millions of children from allocating a million UI rows.
                if (best.Count < retained) best.Enqueue(id, node.Bytes);
                else if (best.TryPeek(out _, out long smallest) && node.Bytes > smallest) { best.Dequeue(); best.Enqueue(id, node.Bytes); }
            }
            if (!recursive) break;
        }
        var ids = best.UnorderedItems.Select(x => x.Element).OrderByDescending(id => store[id].Bytes).ThenBy(id => store[id].Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var map = MakeMap(store, ids.Take(options.MapLimit), matchedBytes, token, recursive ? 1 : Math.Clamp(options.MapDepth, 1, 15), options.MapLimit, recursive ? null : options.ExpandedDepths);
        string prefix = store.FullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var rows = ids.Take(options.Limit).Select(id =>
        {
            var n = store[id];
            string relative = recursive ? store.FullPath(id)[prefix.Length..] : n.Name;
            return new ViewItem(id, n.Name, n.Bytes, n.IsDirectory, n.IsDirectory ? "文件夹" : DisplayFormat.Category(n.Name), relative, n.ModifiedTicks, n.Flags);
        });
        rows = options.Sort switch
        {
            "名称 A–Z" => rows.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase),
            "修改时间 ↓" => rows.OrderByDescending(x => x.ModifiedTicks),
            _ => rows
        };
        return new ViewResult(rows.ToArray(), map, count, matchedBytes, recursive);
    }

    private static List<MapItem> MakeMap(NodeStore store, IEnumerable<int> ids, long total, CancellationToken token, int depth, int itemLimit,
        IReadOnlyDictionary<int, DirectoryDetail>? expandedDepths, int level = 1, int inheritedRevision = -1)
    {
        var result = new List<MapItem>();
        long shown = 0;
        foreach (int id in ids)
        {
            token.ThrowIfCancellationRequested();
            var n = store[id];
            if (n.Bytes <= 0) continue;
            IReadOnlyList<MapItem>? children = null;
            int localDepth = depth, revision = inheritedRevision;
            // A newer parent click supersedes older descendant expansions. A later child click
            // may open that child again, without disturbing other branches or the global depth.
            if (expandedDepths is not null && expandedDepths.TryGetValue(id, out var detail) && detail.Revision > inheritedRevision)
            { localDepth = detail.Depth; revision = detail.Revision; }
            localDepth = Math.Clamp(localDepth, 1, 16 - level);
            if (localDepth > 1 && n.IsDirectory)
            {
                // Allocate breadth by projected area at every level. A global traversal budget must
                // not silently collapse later siblings to a different depth.
                int childLimit = Math.Clamp((int)Math.Ceiling(itemLimit * (double)n.Bytes / Math.Max(1, total)), 1, Math.Max(1, itemLimit));
                var top = new PriorityQueue<int, long>();
                foreach (int child in store.Children(id))
                {
                    token.ThrowIfCancellationRequested();
                    if (top.Count < childLimit) top.Enqueue(child, store[child].Bytes);
                    else if (top.TryPeek(out _, out long min) && store[child].Bytes > min) { top.Dequeue(); top.Enqueue(child, store[child].Bytes); }
                }
                children = MakeMap(store, top.UnorderedItems.Select(x => x.Element).OrderByDescending(i => store[i].Bytes), n.Bytes, token, localDepth - 1, childLimit, expandedDepths, level + 1, revision);
            }
            result.Add(new MapItem(id, n.Name, n.Bytes, n.IsDirectory, n.IsDirectory ? "文件夹" : DisplayFormat.Category(n.Name), children));
            shown += n.Bytes;
        }
        // An invisible spacer preserves the exact total. Never invent a labelled folder for omitted entries.
        if (total > shown) result.Add(new MapItem(-1, "", total - shown, false, "其他"));
        return result;
    }

    public static void ExportCsv(string path, NodeStore store, IEnumerable<ViewItem> rows)
    {
        // Snapshot paths before writing; live deletion/renaming may occur while the export is saved.
        (ViewItem Row, string Path)[] snapshot;
        lock (store.SyncRoot) snapshot = rows.Where(row => store.IsActive(row.Id)).Select(row => (row, store.FullPath(row.Id))).ToArray();
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("名称,路径,类型,字节数,修改时间UTC,状态");
        foreach (var (row, fullPath) in snapshot)
            writer.WriteLine($"{Csv(row.Name)},{Csv(fullPath)},{Csv(row.Category)},{row.Bytes},{Csv(row.ModifiedTicks > 0 ? new DateTime(row.ModifiedTicks, DateTimeKind.Utc).ToString("O") : "")},{Csv(row.Flags.ToString())}");
    }
    private static string Csv(string value)
    {
        // Prevent Excel from executing filename text as a formula when opening reports.
        if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
