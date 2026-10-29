using System.Diagnostics;
using System.IO;
using SpaceLens.Core;

namespace SpaceLens.App;

public partial class MainWindow
{
    private async Task VerifyLiveUiAsync(Action<bool, string> check)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "SpaceLens-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "keep"));
        string keep = Path.Combine(fixture, "keep", "original.bin");
        File.WriteAllBytes(keep, new byte[100]);
        try
        {
            await StartScanAsync(fixture);
            check(liveEnabled && liveChanges is not null, "monitoring starts by default");
            int folder = result!.Store.FindPath(Path.Combine(fixture, "keep"));
            Navigate(folder); await UpdateViewAsync();
            SearchBox.Text = "new"; searchTimer.Stop(); await UpdateViewAsync();
            File.WriteAllBytes(Path.Combine(fixture, "keep", "new.bin"), new byte[300]);
            await Until(() => result!.Store[0].Bytes == 400 && view!.Rows.Any(row => row.Name == "new.bin"));
            check(current == folder && SearchBox.Text == "new", "live addition updates filtered view without resetting navigation");
            File.WriteAllBytes(Path.Combine(fixture, "keep", "new.bin"), new byte[900]);
            await Until(() => view!.Rows.Any(row => row.Name == "new.bin" && row.Bytes == 900));
            check(SizeMetric.Text == DisplayFormat.Size(1000), "live resize updates status and displayed rows");
            File.Move(Path.Combine(fixture, "keep", "new.bin"), Path.Combine(fixture, "keep", "new-name.bin"));
            await Until(() => view!.Rows.Any(row => row.Name == "new-name.bin") && view.Rows.All(row => row.Name != "new.bin"));
            check(result!.Store[0].Files == 2, "live rename updates labels without duplicating files");
            Directory.Delete(Path.Combine(fixture, "keep"), true);
            await Until(() => current == 0 && result!.Store[0].Files == 0 && view!.Rows.Count == 0);
            check(result!.Store[0].Bytes == 0, "deleted current folder returns safely to surviving ancestor");
            StopLive();
            File.WriteAllBytes(Path.Combine(fixture, "off.bin"), new byte[77]);
            await Task.Delay(900);
            check(result!.Store[0].Bytes == 0, "monitoring can be stopped without background reads");
            StartLive(fixture); liveChanges!.RequestReconcile(); await UpdateLiveAsync(true);
            check(result!.Store[0].Bytes == 77, "reenabling monitoring reconciles changes made while off");
        }
        finally
        {
            StopLive();
            // Only this verification run's unique temporary fixture is removed.
            Directory.Delete(fixture, true);
        }
        async Task Until(Func<bool> ready)
        {
            var timeout = Stopwatch.StartNew();
            while (!ready())
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(12)) throw new InvalidOperationException("live UI update timed out");
                await Task.Delay(100);
            }
        }
    }
}
