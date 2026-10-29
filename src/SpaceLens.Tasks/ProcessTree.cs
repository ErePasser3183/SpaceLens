using SpaceLens.Core;

namespace SpaceLens.Tasks;

public enum ProcessMetric { Memory, Cpu }
public sealed record ProcessNode(int Id, string Name, string Group, ProcessSample? Process, long WorkingSet, double? CpuPercent)
{
    public bool IsGroup => Process is null;
    public string Size => DisplayFormat.Size(WorkingSet);
    public ulong TotalMemory { get; init; }
    public string MemoryText => TotalMemory > 0 ? $"{Size} · {WorkingSet * 100d / TotalMemory:F1}%" : Size;
    public string Cpu => CpuPercent is double cpu ? $"{cpu:F1}%" : "—";
    public string Pid => Process?.Pid.ToString() ?? "";
    public string Detail => IsGroup ? $"{Name}\n内存占用 {MemoryText}\n已知 CPU {Cpu}" : $"{Group}\nPID {Pid}\n内存占用 {MemoryText}\nCPU {Cpu}";
    public long Weight(ProcessMetric metric) => metric == ProcessMetric.Memory ? WorkingSet : (long)Math.Round((CpuPercent ?? 0) * 10000);
}
public sealed record ProcessTree(IReadOnlyList<MapItem> Map, IReadOnlyList<ProcessNode> Rows, IReadOnlyDictionary<int, ProcessNode> Nodes);

public sealed class ProcessTreeBuilder
{
    private readonly Dictionary<string, int> groupIds = new(StringComparer.OrdinalIgnoreCase);
    private int nextGroup = int.MaxValue;

    public ProcessTree Build(ProcessSnapshot snapshot, ProcessMetric metric, string search = "", string? group = null,
        int depth = 2, IReadOnlySet<string>? expanded = null, IReadOnlySet<string>? collapsed = null)
    {
        var nodes = new Dictionary<int, ProcessNode>();
        var rows = new List<ProcessNode>();
        var map = new List<MapItem>();
        var allNames = snapshot.Processes.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string missing in groupIds.Keys.Where(name => !allNames.Contains(name)).ToArray()) groupIds.Remove(missing);
        foreach (var program in snapshot.Processes.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (group is not null && !program.Key.Equals(group, StringComparison.OrdinalIgnoreCase)) continue;
            var children = program.Where(p => p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || p.Pid.ToString().Contains(search, StringComparison.Ordinal))
                .Select(p => new ProcessNode(p.Pid, $"{p.Name} · {p.Pid}", program.Key, p, p.WorkingSet, p.CpuPercent) { TotalMemory = snapshot.Memory?.Total ?? 0 })
                .OrderByDescending(p => p.Weight(metric)).ThenBy(p => p.Id).ToArray();
            if (children.Length == 0) continue;
            foreach (var child in children) { nodes.Add(child.Id, child); rows.Add(child); }
            if (!groupIds.TryGetValue(program.Key, out int id)) groupIds[program.Key] = id = nextGroup--;
            double? cpu = children.Any(p => p.CpuPercent.HasValue) ? children.Sum(p => p.CpuPercent ?? 0) : null;
            var parent = new ProcessNode(id, $"{program.Key} ({children.Length})", program.Key, null, children.Sum(p => p.WorkingSet), cpu) { TotalMemory = snapshot.Memory?.Total ?? 0 };
            nodes.Add(id, parent);
            MapItem Tile(ProcessNode p, IReadOnlyList<MapItem>? nested = null) => new(p.Id, p.Name, p.Weight(metric), p.IsGroup, "进程:" + p.Group, nested,
                metric == ProcessMetric.Memory ? p.MemoryText : p.Cpu, p.Detail);
            var tiles = children.Where(p => p.Weight(metric) > 0).Select(p => Tile(p)).ToArray();
            if (group is not null) map.AddRange(tiles);
            else if (parent.Weight(metric) > 0) map.Add(Tile(parent,
                collapsed?.Contains(program.Key) != true && (depth > 1 || expanded?.Contains(program.Key) == true) ? tiles : null));
        }
        return new(map.OrderByDescending(p => p.Bytes).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            rows.OrderByDescending(p => p.Weight(metric)).ThenBy(p => p.Id).ToArray(), nodes);
    }
}
