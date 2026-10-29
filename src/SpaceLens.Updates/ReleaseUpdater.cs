using Velopack;
using Velopack.Sources;

namespace SpaceLens.Updates;

public interface IReleaseUpdater
{
    bool Available { get; }
    string CurrentVersion { get; }
    string? PendingVersion { get; }
    Task<string?> CheckAsync(CancellationToken cancellation);
    Task DownloadAsync(CancellationToken cancellation);
    void ApplyOnExit();
}

public sealed class ReleaseUpdater : IReleaseUpdater
{
    public const string RepositoryUrl = "https://github.com/ErePasser3183/SpaceLens";
    private readonly UpdateManager manager;
    private UpdateInfo? offer;

    public ReleaseUpdater() : this(new UpdateManager(new GithubSource(RepositoryUrl, null, false))) { }
    public ReleaseUpdater(UpdateManager manager) => this.manager = manager;
    public bool Available => manager.IsInstalled;
    public string CurrentVersion => manager.CurrentVersion?.ToString() ?? "开发版";
    public string? PendingVersion => Available ? manager.UpdatePendingRestart?.Version.ToString() : null;

    public async Task<string?> CheckAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var next = await manager.CheckForUpdatesAsync();
        cancellation.ThrowIfCancellationRequested();
        offer = next;
        return next?.TargetFullRelease.Version.ToString();
    }

    public async Task DownloadAsync(CancellationToken cancellation)
    {
        if (offer is null) throw new InvalidOperationException("尚未找到可下载的更新。");
        // Velopack checks the release package checksum before marking it ready.
        await manager.DownloadUpdatesAsync(offer, cancelToken: cancellation);
    }

    public void ApplyOnExit()
    {
        if (manager.UpdatePendingRestart is { } pending)
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
    }
}
