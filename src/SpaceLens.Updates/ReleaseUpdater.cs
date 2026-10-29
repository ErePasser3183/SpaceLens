using Velopack;
using Velopack.Sources;

namespace SpaceLens.Updates;

public interface IReleaseUpdater
{
    bool Available { get; }
    string CurrentVersion { get; }
    string? PendingVersion { get; }
    event Action<int>? DownloadProgressChanged;
    Task<string?> CheckAsync(CancellationToken cancellation);
    Task DownloadAsync(CancellationToken cancellation);
    void ApplyOnExit();
}

public sealed class ReleaseUpdater : IReleaseUpdater
{
    public const string RepositoryUrl = "https://github.com/ErePasser3183/SpaceLens";
    private readonly UpdateManager manager;
    private UpdateInfo? offer;
    private readonly ResumableDownloader? downloader;
    public event Action<int>? DownloadProgressChanged;

    public ReleaseUpdater() : this(new ResumableDownloader()) { }
    private ReleaseUpdater(ResumableDownloader downloader) : this(new UpdateManager(new GithubSource(RepositoryUrl, null, false, downloader)), downloader) { }
    public ReleaseUpdater(UpdateManager manager, ResumableDownloader? downloader = null) { this.manager = manager; this.downloader = downloader; }
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
        if (downloader is not null) downloader.ExpectedSha256 = offer.TargetFullRelease.SHA256;
        // Fixed full-package identity makes restart checkpoints reliable; Velopack verifies it again.
        var fullPackage = new UpdateInfo(offer.TargetFullRelease, offer.IsDowngrade);
        await Task.Run(() => manager.DownloadUpdatesAsync(fullPackage,
            progress: percent => DownloadProgressChanged?.Invoke(percent), cancelToken: cancellation), cancellation);
    }

    public void ApplyOnExit()
    {
        if (manager.UpdatePendingRestart is { } pending)
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
    }
}
