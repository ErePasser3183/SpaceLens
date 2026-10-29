using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using SpaceLens.Core;

namespace SpaceLens.App;

public partial class MainWindow
{
    internal async Task VerifyRenderAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        try
        {
            await Task.Delay(200);
            var delays = new List<double>();
            for (int frame = 0; frame < 12; frame++)
            {
                var items = Enumerable.Range(0, 32).Select(parent => new MapItem(parent * 100, "项目-" + parent, 320000 + parent * frame, true, "文件夹",
                    Enumerable.Range(1, 32).Select(child => new MapItem(parent * 100 + child, "中文-file-" + child + ".bin", 10000, false, "其他")).ToArray())).ToArray();
                var clock = Stopwatch.StartNew();
                var input = Dispatcher.InvokeAsync(() => clock.Elapsed.TotalMilliseconds, DispatcherPriority.Input);
                Map.SetItems(items);
                delays.Add(await input);
                await Task.Delay(120);
            }
            await Map.WaitForRenderAsync();
            int benchmarkFrames = Map.RenderedFrames, benchmarkLabels = Map.LabelledTileCount;
            double benchmarkRenderMs = Map.LastRenderMilliseconds;
            if (delays.Max() >= 50 || benchmarkFrames < 9) throw new InvalidOperationException("render blocks input or fails to publish progressive frames");
            int unchangedFrames = Map.RenderedFrames;
            Map.SetItems(Enumerable.Range(0, 32).Select(parent => new MapItem(parent * 100, "项目-" + parent, 320000 + parent * 11, true, "文件夹",
                Enumerable.Range(1, 32).Select(child => new MapItem(parent * 100 + child, "中文-file-" + child + ".bin", 10000, false, "其他")).ToArray())).ToArray());
            await Map.WaitForRenderAsync();
            if (Map.RenderedFrames != unchangedFrames) throw new InvalidOperationException("unchanged data triggers redundant redraw");
            int streamingStart = Map.RenderedFrames;
            for (int frame = 0; frame < 40; frame++)
            {
                Map.SetItems(Enumerable.Range(0, 32).Select(parent => new MapItem(parent * 100, "更新-" + frame + "-" + parent, 320000, true, "文件夹",
                    Enumerable.Range(1, 32).Select(child => new MapItem(parent * 100 + child, "中文-file-" + child + ".bin", 10000, false, "其他")).ToArray())).ToArray(), 100);
                await Task.Delay(15);
            }
            int framesWhileStreaming = Map.RenderedFrames - streamingStart;
            if (framesWhileStreaming < 3) throw new InvalidOperationException("continuous updates starve completed frames");
            var levels = new NodeStore(); levels.Add("D:\\detail", -1, 0, 0, NodeFlags.Directory);
            int a = levels.Add("folder A", 0, 0, 0, NodeFlags.Directory);
            int b = levels.Add("folder B", 0, 0, 0, NodeFlags.Directory);
            for (int i = 0; i < 100; i++) levels.Add("small-file-" + i, a, 1, 0, NodeFlags.None);
            int inner = levels.Add("inner B", b, 0, 0, NodeFlags.Directory); levels.Add("file", inner, 100, 0, NodeFlags.None);
            levels.Aggregate();
            Map.SetItems(ViewQuery.Build(levels, 0, new QueryOptions(MapDepth: 1), CancellationToken.None).Map, 101);
            await Map.WaitForRenderAsync();
            if (Map.ExpandedDirectories.Count != 0) throw new InvalidOperationException("detail 1 unexpectedly expands folders");
            Map.SetItems(ViewQuery.Build(levels, 0, new QueryOptions(MapDepth: 2), CancellationToken.None).Map);
            await Map.WaitForRenderAsync();
            if (!Map.ExpandedDirectories.SetEquals(new[] { a, b })) throw new InvalidOperationException("detail 2 does not expand both sparse and dense folders equally");
            Map.SetItems(ViewQuery.Build(levels, 0, new QueryOptions(MapDepth: 3), CancellationToken.None).Map);
            await Map.WaitForRenderAsync();
            if (!Map.ExpandedDirectories.SetEquals(new[] { a, b, inner })) throw new InvalidOperationException("detail 3 does not expand next level");
            File.WriteAllText(Path.Combine(directory, "render-verification.json"), JsonSerializer.Serialize(new { inputDelayMs = delays,
                maxInputDelayMs = delays.Max(), averageInputDelayMs = delays.Average(), responsive = delays.Max() < 50,
                labelledTiles = benchmarkLabels, unlabelledTiles = Map.UnlabelledTileCount, renderedFrames = benchmarkFrames,
                lastRenderMs = benchmarkRenderMs, framesWhileStreaming, detailLevelsConsistent = true, unchangedFramesSkipped = true }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(directory, "render-failure.txt"), ex.ToString()); }
        finally { Close(); }
    }
}
