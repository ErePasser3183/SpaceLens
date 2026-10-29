using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

internal static class UpdatePackageChecks
{
    internal static async Task VerifyDownload(string source, string output)
    {
        Directory.CreateDirectory(output);
        var locator = new TestVelopackLocator("SpaceLens", "1.0.0", Path.Combine(output, "packages"),
            output, output, Path.Combine(output, "Update.exe"), "win");
        var manager = source == "github"
            ? new UpdateManager(new GithubSource(SpaceLens.Updates.ReleaseUpdater.RepositoryUrl, null, false), locator: locator)
            : new UpdateManager(source, locator: locator);
        var offer = await manager.CheckForUpdatesAsync() ?? throw new Exception("No newer release found.");
        await manager.DownloadUpdatesAsync(offer);
        var pending = manager.UpdatePendingRestart ?? throw new Exception("Downloaded package is not ready.");
        string file = Path.Combine(locator.PackagesDir!, pending.FileName);
        await using var stream = File.OpenRead(file);
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        if (!hash.Equals(offer.TargetFullRelease.SHA256, StringComparison.OrdinalIgnoreCase)) throw new Exception("Package digest differs.");
        File.WriteAllText(Path.Combine(output, "download-passed.txt"), $"Version={pending.Version}\nSHA256={hash}\n");
        Console.WriteLine("PASS: release feed, real download and SHA-256 verification");

        if (source == "github") return;
        string corrupt = Path.Combine(output, "corrupt-feed");
        Directory.CreateDirectory(corrupt);
        var feed = JsonNode.Parse(File.ReadAllText(Path.Combine(source, "releases.win.json")))!;
        foreach (var asset in feed["Assets"]!.AsArray())
        {
            asset!["SHA256"] = new string('0', 64);
            asset["SHA1"] = new string('0', 40);
            File.Copy(Path.Combine(source, asset["FileName"]!.GetValue<string>()), Path.Combine(corrupt, asset["FileName"]!.GetValue<string>()));
        }
        File.WriteAllText(Path.Combine(corrupt, "releases.win.json"), feed.ToJsonString());
        var badLocator = new TestVelopackLocator("SpaceLens", "1.0.0", Path.Combine(output, "bad-packages"),
            output, output, Path.Combine(output, "Update.exe"), "win");
        var badManager = new UpdateManager(corrupt, locator: badLocator);
        var badOffer = await badManager.CheckForUpdatesAsync() ?? throw new Exception("Corrupt fixture not offered.");
        bool rejected = false;
        try { await badManager.DownloadUpdatesAsync(badOffer); }
        catch (Velopack.Exceptions.ChecksumFailedException) { rejected = true; }
        if (!rejected || badManager.UpdatePendingRestart is not null) throw new Exception("Corrupt package was not rejected.");
        Console.WriteLine("PASS: corrupted package checksum rejected without preparing an update");
    }

    internal static async Task VerifyApply(string source)
    {
        // Run only from a disposable extracted portable fixture, never the developer checkout.
        var locator = new WindowsVelopackLocator(null, null);
        if (!File.Exists(Path.Combine(locator.RootAppDir!, "update-test-fixture"))) throw new Exception("Not an update test fixture.");
        var manager = new UpdateManager(source, locator: locator);
        var offer = await manager.CheckForUpdatesAsync() ?? throw new Exception("No fixture update found.");
        await manager.DownloadUpdatesAsync(offer);
        File.WriteAllText(Path.Combine(locator.RootAppDir!, "apply-scheduled.txt"), offer.TargetFullRelease.Version.ToString());
        manager.WaitExitThenApplyUpdates(manager.UpdatePendingRestart, silent: true, restart: false);
    }
}
