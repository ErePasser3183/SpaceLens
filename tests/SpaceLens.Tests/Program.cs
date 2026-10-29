using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SpaceLens.Core;

if (args.Length == 3 && args[0] == "--verify-update-package")
{
    await UpdatePackageChecks.VerifyDownload(args[1], args[2]);
    return;
}
if (args.Length == 2 && args[0] == "--verify-update-apply")
{
    await UpdatePackageChecks.VerifyApply(args[1]);
    return;
}

if (args.Length > 0 && args[0] == "--profile-memory")
{
    using var control = new ScanControl();
    using var process = Process.GetCurrentProcess();
    var clock = Stopwatch.StartNew();
    var scan = new DiskScanner().Scan(args[1], control);
    double seconds = clock.Elapsed.TotalSeconds;
    long allocated = GC.GetTotalAllocatedBytes();
    long liveBytes = GC.GetTotalMemory(true); // Diagnostic only: measure live storage separately from reclaimable scan buffers.
    process.Refresh(); long workingSet = process.WorkingSet64;
    var references = new HashSet<string>(ReferenceEqualityComparer.Instance);
    var unique = new HashSet<string>(StringComparer.Ordinal);
    long retainedNameBytes = 0, uniqueNameBytes = 0;
    for (int i = 0; i < scan.Store.Count; i++)
    {
        string name = scan.Store[i].Name;
        long size = (24 + name.Length * 2 + 7) & ~7L;
        if (references.Add(name)) retainedNameBytes += size;
        if (unique.Add(name)) uniqueNameBytes += size;
    }
    var report = new { nodes = scan.Store.Count, files = scan.Store[0].Files, bytes = scan.Store[0].Bytes, scanSeconds = seconds,
        nodeSize = System.Runtime.CompilerServices.Unsafe.SizeOf<DiskNode>(), liveMiB = liveBytes / 1048576.0,
        workingSetMiB = workingSet / 1048576.0, allocatedMiB = allocated / 1048576.0, retainedNames = references.Count,
        uniqueNames = unique.Count, retainedNameMiB = retainedNameBytes / 1048576.0, uniqueNameMiB = uniqueNameBytes / 1048576.0 };
    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json); if (args.Length > 2) File.WriteAllText(args[2], json);
    GC.KeepAlive(scan); return;
}

if (args.Length > 0 && args[0] == "--benchmark")
{
    var clock = Stopwatch.StartNew();
    using var control = new ScanControl();
    using var process = Process.GetCurrentProcess();
    var cpuBefore = process.TotalProcessorTime;
    int reports = 0;
    var result = new DiskScanner().Scan(args[1], control, p => { if (++reports % 35 == 0) Console.WriteLine($"progress {p.Files:N0} files, {p.Elapsed.TotalSeconds:F1}s"); }, args.Contains("--low-impact"));
    var scanSeconds = clock.Elapsed.TotalSeconds;
    var watch = Stopwatch.StartNew();
    var view = ViewQuery.Build(result.Store, 0, new QueryOptions(LargestFiles: true), CancellationToken.None);
    var report = new { path = args[1], files = result.Store[0].Files, folders = result.Store[0].Folders,
        bytes = result.Store[0].Bytes, nodes = result.Store.Count, scanSeconds, filesPerSecond = result.Store[0].Files / scanSeconds,
        querySeconds = watch.Elapsed.TotalSeconds, workingSetMiB = process.WorkingSet64 / 1048576.0,
        peakWorkingSetMiB = process.PeakWorkingSet64 / 1048576.0, cpuSeconds = (process.TotalProcessorTime - cpuBefore).TotalSeconds,
        managedMiB = GC.GetTotalMemory(false) / 1048576.0, errors = result.IssueCount, links = result.SkippedLinks,
        cancelled = result.Cancelled, largestRows = view.Rows.Count };
    string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(json);
    if (args.Length > 2 && !args[2].StartsWith("--")) File.WriteAllText(args[2], json);
    return;
}

string fixture = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "fixtures", Guid.NewGuid().ToString("N")));
Directory.CreateDirectory(fixture);
int passed = 0;
void Check(bool value, string description) { if (!value) throw new Exception("FAIL: " + description); passed++; Console.WriteLine("PASS: " + description); }
void Make(string relative, int length) { string path = Path.Combine(fixture, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); using var file = File.Create(path); file.SetLength(length); }
Make("Alpha/a.bin", 1024); Make("Alpha/b.mp4", 4096); Make("Beta/中文.wav", 2048); Make("empty.txt", 0); Make("=formula.csv", 37);
Directory.CreateDirectory(Path.Combine(fixture, "empty"));
Make("hidden.dat", 123); File.SetAttributes(Path.Combine(fixture, "hidden.dat"), FileAttributes.Hidden | FileAttributes.System);
using var baselineControl = new ScanControl();
var baseline = new DiskScanner().Scan(fixture, baselineControl, lowImpact: false);
Check(baseline.Store[0].Bytes == 7328, "exact byte sum including hidden/system files");
Check(baseline.Store[0].Files == 6 && baseline.Store[0].Folders == 3, "file and directory counts include empty entries");
Check(!baseline.Cancelled && baseline.IssueCount == 0, "successful clean scan");
int alpha = baseline.Store.Children(0).Single(i => baseline.Store[i].Name == "Alpha");
Check(baseline.Store[alpha].Bytes == 5120 && baseline.Store[alpha].Files == 2, "nested aggregation");
Check(baseline.Store.FullPath(alpha) == Path.Combine(fixture, "Alpha"), "path reconstruction");
var filtered = ViewQuery.Build(baseline.Store, 0, new QueryOptions(Search: "中文"), CancellationToken.None);
Check(filtered.MatchCount == 1 && filtered.Rows[0].Bytes == 2048, "recursive Unicode search");
Check(ViewQuery.Build(baseline.Store, 0, new QueryOptions(Category: "视频"), CancellationToken.None).MatchBytes == 4096, "recursive category filter");
Check(ViewQuery.Build(baseline.Store, 0, new QueryOptions(MinimumBytes: 2000), CancellationToken.None).MatchCount == 2, "size filter");
Check(ViewQuery.Build(baseline.Store, alpha, new QueryOptions(LargestFiles: true), CancellationToken.None).MatchCount == 2, "search remains within current directory");
string csv = Path.Combine(fixture, "export.csv");
ViewQuery.ExportCsv(csv, baseline.Store, ViewQuery.Build(baseline.Store, 0, new QueryOptions(LargestFiles: true), CancellationToken.None).Rows);
Check(File.ReadAllText(csv).Contains("\"'=formula.csv\""), "CSV formula injection neutralized");

Directory.CreateSymbolicLink(Path.Combine(fixture, "loop"), fixture);
using var linkControl = new ScanControl();
var linked = new DiskScanner().Scan(fixture, linkControl, lowImpact: false);
Check(linked.SkippedLinks == 1 && linked.Store.Count < 30, "directory symlink loop skipped");

string longRelative = string.Join(Path.DirectorySeparatorChar, Enumerable.Repeat("long-directory-name-0123456789", 12)) + "\\深层.txt";
Make(longRelative, 100);
using var longControl = new ScanControl();
var longScan = new DiskScanner().Scan(fixture, longControl, lowImpact: false);
Check(ViewQuery.Build(longScan.Store, 0, new QueryOptions(Search: "深层"), CancellationToken.None).MatchBytes == 100, "paths beyond 260 characters");

var denied = Directory.CreateDirectory(Path.Combine(fixture, "denied"));
Make("denied/secret.dat", 13);
var acl = denied.GetAccessControl();
var originalAcl = acl.GetSecurityDescriptorBinaryForm();
var user = WindowsIdentity.GetCurrent().User!;
acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ListDirectory, AccessControlType.Deny));
try
{
    denied.SetAccessControl(acl);
    using var deniedControl = new ScanControl();
    var deniedScan = new DiskScanner().Scan(fixture, deniedControl, lowImpact: false);
    Check(deniedScan.IssueCount == 1 && deniedScan.Store[0].Files >= 8, "access denied recorded while other directories continue");
}
finally { var restore = new DirectorySecurity(); restore.SetSecurityDescriptorBinaryForm(originalAcl); denied.SetAccessControl(restore); }

using var cancelledControl = new ScanControl();
cancelledControl.Cancel();
var cancelled = new DiskScanner().Scan(fixture, cancelledControl);
Check(cancelled.Cancelled && cancelled.Store[0].Bytes == 0, "pre-cancelled scan returns consistent partial result");

using var pauseControl = new ScanControl();
pauseControl.Pause();
var pauseTask = Task.Run(() => new DiskScanner().Scan(fixture, pauseControl));
await Task.Delay(120);
Check(!pauseTask.IsCompleted, "pause prevents directory traversal");
pauseControl.Resume();
Check(!(await pauseTask.WaitAsync(TimeSpan.FromSeconds(5))).Cancelled, "resume completes scan");
using var pauseCancel = new ScanControl();
pauseCancel.Pause();
var paused = Task.Run(() => new DiskScanner().Scan(fixture, pauseCancel));
await Task.Delay(40); var cancelWatch = Stopwatch.StartNew(); pauseCancel.Cancel();
Check((await paused.WaitAsync(TimeSpan.FromSeconds(2))).Cancelled && cancelWatch.ElapsedMilliseconds < 1000, "cancel interrupts pause within one second");

var big = new NodeStore(); big.Add("D:\\fixture", -1, 0, 0, NodeFlags.Directory);
for (int i = 1; i <= 30000; i++) big.Add($"file-{i}.bin", 0, i, 0, NodeFlags.None);
big.Aggregate();
var bigView = ViewQuery.Build(big, 0, new QueryOptions(), CancellationToken.None);
Check(bigView.Rows.Count == 2000 && bigView.MatchCount == 30000, "large directories bound list memory");
Check(bigView.Rows[0].Bytes == 30000 && bigView.Rows[^1].Bytes == 28001, "bounded heap retains largest entries");
Check(bigView.Map.Sum(i => i.Bytes) == big[0].Bytes, "treemap other bucket preserves full byte total");
Check(bigView.Map.Where(x => x.Id < 0).All(x => x.Name.Length == 0), "pixel-budget remainder never becomes a named synthetic folder");
var nested = new NodeStore(); nested.Add("D:\\nested", -1, 0, 0, NodeFlags.Directory);
int group = nested.Add("programs", 0, 0, 0, NodeFlags.Directory);
for (int i = 0; i < 100; i++) nested.Add($"app-{i}", group, 1_000_000, 0, NodeFlags.None);
nested.Aggregate();
var nestedView = ViewQuery.Build(nested, 0, new QueryOptions(), CancellationToken.None);
Check(nestedView.Map[0].Children!.Count == 100 && nestedView.Map[0].Children!.All(x => x.Id >= 0), "all 100 visible children retained instead of folding after 24");
using var queryCancel = new CancellationTokenSource(); queryCancel.Cancel();
try { ViewQuery.Build(big, 0, new QueryOptions(), queryCancel.Token); Check(false, "cancel query"); } catch (OperationCanceledException) { Check(true, "large queries are cancellable"); }

var random = new Random(41);
for (int iteration = 0; iteration < 100; iteration++)
{
    var items = Enumerable.Range(0, random.Next(1, 200)).Select(i => new MapItem(i, "test", random.Next(1, 100000), false, "其他")).ToArray();
    var rect = new MapRect(3, 5, random.Next(20, 2000), random.Next(20, 1000));
    var tiles = TreemapLayout.Arrange(items, rect);
    double total = items.Sum(i => (double)i.Bytes);
    foreach (var t in tiles)
    {
        if (Math.Abs(t.Bounds.Width * t.Bounds.Height / (rect.Width * rect.Height) - t.Item.Bytes / total) > 1e-8) throw new Exception("treemap area ratio");
        if (t.Bounds.X < rect.X - 1e-6 || t.Bounds.Y < rect.Y - 1e-6 || t.Bounds.X + t.Bounds.Width > rect.X + rect.Width + 1e-6 || t.Bounds.Y + t.Bounds.Height > rect.Y + rect.Height + 1e-6) throw new Exception("treemap bounds");
    }
    for (int a = 0; a < tiles.Count; a++) for (int b = a + 1; b < tiles.Count; b++)
    {
        var x = tiles[a].Bounds; var y = tiles[b].Bounds;
        if (Math.Min(x.X + x.Width, y.X + y.Width) - Math.Max(x.X, y.X) > 1e-6 && Math.Min(x.Y + x.Height, y.Y + y.Height) - Math.Max(x.Y, y.Y) > 1e-6) throw new Exception("treemap overlap");
    }
}
Check(true, "100 randomized layouts preserve area, bounds and non-overlap");
Check(TreemapLayout.Arrange([], new MapRect(0, 0, 10, 10)).Count == 0, "empty treemap");
Check(DisplayFormat.Size(1024) == "1.00 KiB", "binary size units");
var dense = Enumerable.Range(0, 400).Select(i => new MapItem(i, "f" + i, 1, false, "其他")).ToArray();
var packed = TreemapLayout.ArrangeVisible(dense, new MapRect(0, 0, 120, 70), tile => tile.Bounds.Width >= 10 && tile.Bounds.Height >= 11);
Check(packed.Count > 0 && Math.Abs(packed.Sum(tile => tile.Bounds.Width * tile.Bounds.Height) - 8400) < .001,
    "culling unreadable tiles repacks the visible items without an empty remainder");
Check(packed.All(tile => tile.Item.Id >= 0 && tile.Bounds.Width >= 10 && tile.Bounds.Height >= 11), "culled layout keeps only real readable tiles");
var levels = new NodeStore(); levels.Add("D:\\levels", -1, 0, 0, NodeFlags.Directory);
for (int branch = 0; branch < 5; branch++)
{
    int parent = 0;
    for (int level = 1; level <= 6; level++) parent = levels.Add($"b{branch}-level{level}", parent, 0, 0, NodeFlags.Directory);
    levels.Add("file.bin", parent, 100, 0, NodeFlags.None);
}
levels.Aggregate();
int Depth(MapItem node) => node.Children is { Count: > 0 } ? 1 + node.Children.Where(child => child.Id >= 0).Select(Depth).DefaultIfEmpty(0).Max() : 1;
for (int detail = 1; detail <= 6; detail++)
{
    var map = ViewQuery.Build(levels, 0, new QueryOptions(MapDepth: detail, MapLimit: 5), CancellationToken.None).Map;
    Check(map.Where(item => item.Id >= 0).All(item => Depth(item) == detail), $"detail {detail} applies equally to every folder branch");
}

int expandedBranch = levels.Children(0).First();
var localExpansion = ViewQuery.Build(levels, 0, new QueryOptions(MapDepth: 2, ExpandedDepths: new Dictionary<int, DirectoryDetail> { [expandedBranch] = new(4, 1) }), CancellationToken.None).Map;
Check(localExpansion.Where(item => item.Id >= 0).All(item => Depth(item) == (item.Id == expandedBranch ? 4 : 2)), "click expansion affects only the requested subtree");
Check(localExpansion.Sum(item => item.Bytes) == levels[0].Bytes, "local expansion preserves parent sizes and total area weights");
var resetExpansion = ViewQuery.Build(levels, 0, new QueryOptions(MapDepth: 1), CancellationToken.None).Map;
Check(resetExpansion.Where(item => item.Id >= 0).All(item => Depth(item) == 1), "global detail control can reset local expansion");
int expandedChild = levels.Children(expandedBranch).First();
int expandedGrandchild = levels.Children(expandedChild).First();
var clickDetails = new Dictionary<int, DirectoryDetail> { [expandedBranch] = new(2, 3), [expandedChild] = new(3, 2), [expandedGrandchild] = new(3, 1) };
var parentClick = ViewQuery.Build(levels, 0, new QueryOptions(MapDepth: 5, ExpandedDepths: clickDetails), CancellationToken.None).Map;
Check(parentClick.Where(item => item.Id >= 0).All(item => Depth(item) == (item.Id == expandedBranch ? 2 : 5)), "new parent click overrides both global depth and older descendant expansions without changing siblings");
clickDetails[expandedChild] = new(2, 4);
var childClick = ViewQuery.Build(levels, 0, new QueryOptions(MapDepth: 5, ExpandedDepths: clickDetails), CancellationToken.None).Map;
Check(Depth(childClick.Single(item => item.Id == expandedBranch)) == 3, "new child click reopens one level without resurrecting older deeper expansions");
clickDetails[expandedBranch] = new(2, 5);
Check(Depth(ViewQuery.Build(levels, 0, new QueryOptions(MapDepth: 5, ExpandedDepths: clickDetails), CancellationToken.None).Map.Single(item => item.Id == expandedBranch)) == 2, "parent can collapse a re-expanded child again");

var compactStorage = new NodeStore(shareNames: true);
compactStorage.Add("D:\\storage-fixture", -1, 0, 0, NodeFlags.Directory);
int firstShared = -1, lastShared = -1;
const long preciseTicks = 638998765432100123;
for (int i = 0; i < 9000; i++)
{
    int parent = compactStorage.Add("directory-" + i, 0, 0, preciseTicks, NodeFlags.Directory);
    int file = compactStorage.Add(new string("shared-name.bin".ToCharArray()), parent, 1L << 34, preciseTicks + i, NodeFlags.None);
    if (i == 0) firstShared = file;
    lastShared = file;
}
compactStorage.Aggregate(); compactStorage.FinishBuilding();
Check(ReferenceEquals(compactStorage[firstShared].Name, compactStorage[lastShared].Name), "filename sharing survives pool growth and releasing its temporary index");
Check(compactStorage[0].Files == 9000 && compactStorage[0].Folders == 9000 && compactStorage[0].Bytes == 9000L * (1L << 34), "separate directory storage retains counts across chunk boundaries");
Check(compactStorage[lastShared].ModifiedTicks == preciseTicks + 8999 && compactStorage[lastShared].Bytes == 1L << 34,
    "compact storage retains full 64-bit sizes and timestamp precision");
compactStorage.UpdateMetadata(lastShared, "renamed.bin", 1L << 34, preciseTicks);
Check(compactStorage[firstShared].Name == "shared-name.bin" && compactStorage[lastShared].Name == "renamed.bin", "updating one shared name does not rename other entries");
foreach (string root in new[] { @"D:\", @"D:\base", @"\\server\share\", @"\\?\D:\" })
{
    var pathStore = new NodeStore(); pathStore.Add(root, -1, 0, 0, NodeFlags.Directory);
    int directory = pathStore.Add("中文目录", 0, 0, 0, NodeFlags.Directory);
    int file = pathStore.Add("emoji-😀.bin", directory, 1, 0, NodeFlags.None);
    Check(pathStore.FullPath(file) == Path.Combine(root, "中文目录", "emoji-😀.bin"), "single-allocation path preserves separators and Unicode: " + root);
}

string liveRoot = Path.Combine(fixture, "live"); Directory.CreateDirectory(liveRoot);
Make("live/keep/original.bin", 100); Make("live/stable.bin", 50);
using (var monitor = new LiveChanges(liveRoot))
using (var liveScanControl = new ScanControl())
{
    int previews = 0;
    var liveScan = new DiskScanner().Scan(liveRoot, liveScanControl, _ => { }, preview: map => { if (map.Count > 0) previews++; });
    var store = liveScan.Store;
    Check(previews > 0 && store[0].Bytes == 150, "scan publishes nonempty live map with exact incremental totals");
    int stableId = store.FindPath(Path.Combine(liveRoot, "stable.bin"));
    async Task PumpUntil(Func<bool> complete)
    {
        var timeout = Stopwatch.StartNew();
        do
        {
            await Task.Delay(100);
            if (monitor.TakeReady() is { } changes)
            {
                using var control = new ScanControl();
                var patch = LiveUpdate.Read(store, changes, control);
                if (patch.Replacement is { } replacement) store = replacement.Store;
                else patch.Apply(store);
                Check(patch.IssueCount == 0, "watcher batch applies without read failures");
            }
            if (timeout.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("watcher update timed out");
        } while (!complete());
    }
    Make("live/new/deep/created.bin", 300);
    await PumpUntil(() => store[0].Bytes == 450 && store.FindPath(Path.Combine(liveRoot, "new/deep/created.bin")) >= 0);
    Check(store[0].Files == 3 && store[0].Folders == 3, "live newly created subtree and ancestor counts");
    using (var file = File.OpenWrite(Path.Combine(liveRoot, "keep/original.bin"))) file.SetLength(700);
    await PumpUntil(() => store[0].Bytes == 1050);
    Check(store[store.FindPath(Path.Combine(liveRoot, "keep"))].Bytes == 700, "live file size updates every ancestor");
    Directory.Move(Path.Combine(liveRoot, "new"), Path.Combine(liveRoot, "moved"));
    await PumpUntil(() => store.FindPath(Path.Combine(liveRoot, "moved/deep/created.bin")) >= 0 && store.FindPath(Path.Combine(liveRoot, "new")) < 0);
    Check(store[0].Bytes == 1050 && store[0].Files == 3 && store[0].Folders == 3, "directory rename preserves exact totals");
    File.Move(Path.Combine(liveRoot, "stable.bin"), Path.Combine(liveRoot, "STABLE.bin"));
    await PumpUntil(() => store[store.FindPath(Path.Combine(liveRoot, "STABLE.bin"))].Name == "STABLE.bin");
    Check(store.FindPath(Path.Combine(liveRoot, "STABLE.bin")) == stableId, "case-only rename retains node identity");
    Directory.Delete(Path.Combine(liveRoot, "moved"), true);
    await PumpUntil(() => store[0].Bytes == 750 && store[0].Folders == 1);
    Check(store[0].Files == 2 && store.FindPath(Path.Combine(liveRoot, "moved")) < 0, "live subtree removal subtracts files and folders");
    for (int i = 0; i < 40; i++) { using var file = File.OpenWrite(Path.Combine(liveRoot, "keep/original.bin")); file.SetLength(1000 + i); }
    await PumpUntil(() => store[0].Bytes == 1089);
    Check(store[0].Files == 2, "bursty repeated writes coalesce without double counting");
    Directory.CreateSymbolicLink(Path.Combine(liveRoot, "external"), fixture);
    await PumpUntil(() => store.FindPath(Path.Combine(liveRoot, "external")) >= 0);
    Check(store[0].Bytes == 1089 && store[store.FindPath(Path.Combine(liveRoot, "external"))].Bytes == 0, "live junction is recorded without following its target");
    Directory.Delete(Path.Combine(liveRoot, "external"));
    await PumpUntil(() => store[0].Folders == 1);
    var compacted = store.Compact();
    Check(compacted.Count == 4 && compacted[0].Bytes == 1089 && compacted.FindPath(Path.Combine(liveRoot, "keep/original.bin")) > 0, "compaction frees deleted nodes while retaining paths and totals");
    for (int i = 0; i < 5000; i++) monitor.Record(Path.Combine(liveRoot, $"overflow-{i}"), true);
    var overflow = monitor.TakeReady(true)!;
    Check(overflow.Rescan && overflow.Changes.Count == 0, "notification overflow has bounded queue and requests reconciliation");
    using var reconcileControl = new ScanControl();
    var reconcile = LiveUpdate.Read(store, overflow, reconcileControl);
    Check(reconcile.Replacement is { Cancelled: false } replacementScan && replacementScan.Store[0].Bytes == 1089, "overflow reconciliation recovers exact filesystem totals");
    monitor.Record(Path.Combine(liveRoot, "pending.bin"), true); monitor.Dispose();
    Check(monitor.TakeReady(true) is null, "stopped watcher releases pending changes");
}

// Remove only our unique fixture; unlink the loop before recursive cleanup.
Directory.Delete(Path.Combine(fixture, "loop"));
File.SetAttributes(Path.Combine(fixture, "hidden.dat"), FileAttributes.Normal);
Directory.Delete(fixture, true);
await TaskChecks.Run(Check);
await UpdateChecks.Run(Check);
Console.WriteLine($"ALL {passed} CHECKS PASSED");
