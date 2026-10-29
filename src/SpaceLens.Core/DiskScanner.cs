using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Enumeration;

namespace SpaceLens.Core;

public sealed class DiskScanner
{
    private readonly record struct Entry(string Name, long Bytes, long Modified, FileAttributes Attributes);

    public ScanResult Scan(string path, ScanControl control, Action<ScanProgress>? report = null, bool lowImpact = false,
        Action<IReadOnlyList<MapItem>>? preview = null, int previewDepth = 3, Func<ScanPreviewSettings>? previewSettings = null)
    {
        path = Path.GetFullPath(path.Trim().Trim('"'));
        var rootAttributes = File.GetAttributes(path);
        if ((rootAttributes & FileAttributes.Directory) == 0) throw new IOException("请选择文件夹或磁盘。");
        if ((rootAttributes & FileAttributes.ReparsePoint) != 0) throw new IOException("此目录是链接，请选择实际目录。");
        var started = DateTime.UtcNow;
        var clock = Stopwatch.StartNew();
        var nodes = new NodeStore(shareNames: true);
        nodes.Add(path, -1, 0, 0, NodeFlags.Directory);
        var pending = new ConcurrentQueue<int>();
        pending.Enqueue(0);
        using var signal = new SemaphoreSlim(1);
        var storeGate = new object();
        var reportGate = new object();
        var issues = new List<ScanIssue>();
        long files = 0, folders = 0, bytes = 0, lastReport = -200, lastPreview = -1000;
        int lastPreviewCount = -1, lastPreviewDepth = -1;
        IReadOnlyDictionary<int, DirectoryDetail>? lastExpansions = null;
        int totalIssues = 0, links = 0, remaining = 1;
        int workers = lowImpact ? 1 : Math.Min(4, Environment.ProcessorCount);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = false,
            AttributesToSkip = 0, ReturnSpecialDirectories = false, BufferSize = 65536 };
        void Report(string current, bool force = false)
        {
            if ((report is null && preview is null) || (!force && clock.ElapsedMilliseconds - Interlocked.Read(ref lastReport) < 50)) return;
            lock (reportGate)
            {
                if (!force && clock.ElapsedMilliseconds - lastReport < 50) return;
                lastReport = clock.ElapsedMilliseconds;
                report?.Invoke(new ScanProgress(Interlocked.Read(ref files), Interlocked.Read(ref folders), Interlocked.Read(ref bytes),
                    Volatile.Read(ref totalIssues), current, clock.Elapsed));
                var settings = previewSettings?.Invoke() ?? new ScanPreviewSettings(previewDepth);
                if (preview is not null && !control.Token.IsCancellationRequested && clock.ElapsedMilliseconds - lastPreview >= Math.Clamp(settings.IntervalMilliseconds, 50, 1500))
                {
                    IReadOnlyList<MapItem>? map = null;
                    lock (storeGate)
                    {
                        if (nodes.Count != lastPreviewCount || settings.Depth != lastPreviewDepth || settings.ExpandedDepths != lastExpansions)
                        {
                            map = ViewQuery.Build(nodes, 0, new QueryOptions(Limit: 0, MapDepth: settings.Depth, MapLimit: 1500, ExpandedDepths: settings.ExpandedDepths), CancellationToken.None).Map;
                            lastPreviewCount = nodes.Count; lastPreviewDepth = settings.Depth; lastExpansions = settings.ExpandedDepths;
                        }
                    }
                    lastPreview = clock.ElapsedMilliseconds;
                    if (map is not null) preview(map);
                }
            }
        }
        void Commit(int parent, Entry[] batch, int count)
        {
            long addedFiles = 0, addedFolders = 0, addedBytes = 0;
            int addedLinks = 0, queued = 0;
            lock (storeGate)
            {
                for (int i = 0; i < count; i++)
                {
                    var entry = batch[i];
                    bool directory = (entry.Attributes & FileAttributes.Directory) != 0;
                    bool link = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
                    var flags = (directory ? NodeFlags.Directory : NodeFlags.None) | (link ? NodeFlags.Link : NodeFlags.None);
                    int id = nodes.Add(entry.Name, parent, link ? 0 : entry.Bytes, entry.Modified, flags);
                    if (link) addedLinks++;
                    if (directory)
                    {
                        addedFolders++;
                        if (!link) { Interlocked.Increment(ref remaining); pending.Enqueue(id); queued++; }
                    }
                    else if (!link) { addedFiles++; addedBytes += entry.Bytes; }
                    batch[i] = default;
                }
                nodes.AdjustAncestors(parent, addedBytes, (int)addedFiles, (int)addedFolders);
            }
            Interlocked.Add(ref files, addedFiles); Interlocked.Add(ref folders, addedFolders);
            Interlocked.Add(ref bytes, addedBytes); Interlocked.Add(ref links, addedLinks);
            if (queued > 0) signal.Release(queued);
        }
        void Worker()
        {
            var batch = new Entry[256];
            long lastYield = 0;
            try
            {
                while (true)
                {
                    control.Checkpoint();
                    signal.Wait(control.Token);
                    if (Volatile.Read(ref remaining) == 0) return;
                    if (!pending.TryDequeue(out int parent)) continue;
                    string current;
                    lock (storeGate) current = nodes.FullPath(parent);
                    int count = 0;
                    try
                    {
                        // Re-check immediately before descent to reduce directory-to-junction races.
                        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        {
                            lock (storeGate) nodes.MarkLink(parent);
                            Interlocked.Increment(ref links);
                        }
                        else
                        {
                            var entries = new FileSystemEnumerable<Entry>(current,
                                (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory ? 0 : e.Length,
                                    e.LastWriteTimeUtc.UtcTicks, e.Attributes), options);
                            foreach (var entry in entries)
                            {
                                control.Checkpoint();
                                batch[count++] = entry;
                                if (count == batch.Length) { Commit(parent, batch, count); count = 0; Report(current); }
                                if (lowImpact && clock.ElapsedMilliseconds - lastYield >= 24)
                                {
                                    control.Token.WaitHandle.WaitOne(2); lastYield = clock.ElapsedMilliseconds;
                                }
                            }
                        }
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
                    {
                        lock (storeGate)
                        {
                            nodes.MarkUnreadable(parent);
                            Interlocked.Increment(ref totalIssues);
                            if (issues.Count < 200) issues.Add(new ScanIssue(current, ex.Message));
                        }
                    }
                    finally
                    {
                        // Keep even the last partial batch on cancellation or an enumeration failure.
                        if (count > 0) Commit(parent, batch, count);
                        if (Interlocked.Decrement(ref remaining) == 0) signal.Release(workers);
                    }
                    Report(current);
                }
            }
            catch (OperationCanceledException) { }
            catch { control.Cancel(); throw; }
        }
        // A fixed pool bounds both I/O contention and temporary buffers. No task is created per directory.
        if (workers == 1) Worker();
        else Task.WaitAll(Enumerable.Range(0, workers).Select(_ => Task.Run(Worker)).ToArray());
        Report(path, true);
        nodes.FinishBuilding();
        return new ScanResult(nodes, issues.AsReadOnly(), totalIssues, links, control.Token.IsCancellationRequested, clock.Elapsed, started);
    }
}
