namespace SpaceLens.Updates;

/// <summary>Stages updates without restarting a running app. Call PrepareForExit only on actual app exit.</summary>
public sealed class UpdateService
{
    private readonly IReleaseUpdater updater;
    private readonly UpdateSettings settings;
    private readonly object gate = new();
    private CancellationTokenSource? operation;
    private bool busy, closing, applyPending;
    private int generation;
    public string Status { get; private set; } = "尚未检查更新";
    public bool Automatic => settings.Automatic;
    public bool Available => updater.Available;
    public bool Busy { get { lock (gate) return busy; } }
    public string Version => updater.CurrentVersion;
    public event Action? Changed;

    public UpdateService(IReleaseUpdater updater, UpdateSettings settings)
    {
        this.updater = updater; this.settings = settings;
        settings.Load();
        if (!Available) Status = "此版本未打包，请使用正式发布版";
        else if (!Automatic) Status = "自动更新已关闭";
    }

    public void SetAutomatic(bool enabled)
    {
        settings.Save(enabled); // A failed save must not pretend the preference was persisted.
        lock (gate)
        {
            generation++;
            operation?.Cancel();
            if (!enabled) applyPending = false;
        }
        SetStatus(enabled ? "自动更新已开启" : "自动更新已关闭");
    }

    public async Task CheckAsync(bool manual = false)
    {
        CancellationTokenSource cancellation;
        int version;
        lock (gate)
        {
            if (closing || busy || !Available || (!manual && !Automatic)) return;
            busy = true; version = generation;
            operation = cancellation = new CancellationTokenSource();
        }
        try
        {
            if (updater.PendingVersion is { } pending)
            {
                Ready(pending, version);
                return;
            }
            SetStatus("正在检查更新…");
            string? next = await updater.CheckAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (next is null) { SetStatus("已是最新版本"); return; }
            SetStatus($"正在后台下载 {next}…");
            await updater.DownloadAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (updater.PendingVersion is not { } downloaded) throw new IOException("更新包未就绪。");
            Ready(downloaded, version);
        }
        catch (OperationCanceledException) { SetStatus(Automatic ? "更新已取消，稍后重试" : "自动更新已关闭"); }
        catch (Exception ex)
        {
            // Update failures never interrupt scanning or process monitoring.
            System.Diagnostics.Debug.WriteLine(ex);
            SetStatus("更新暂不可用，可稍后重试");
        }
        finally
        {
            lock (gate) { busy = false; operation = null; }
            cancellation.Dispose();
            Changed?.Invoke();
        }
    }

    private void Ready(string version, int expectedGeneration)
    {
        lock (gate)
        {
            if (closing || generation != expectedGeneration) return;
            applyPending = true;
        }
        SetStatus($"{version} 已下载，关闭后更新");
    }

    public void PrepareForExit()
    {
        bool apply;
        lock (gate)
        {
            if (closing) return;
            closing = true; generation++;
            operation?.Cancel();
            apply = applyPending && !busy;
        }
        if (!apply) return;
        try { updater.ApplyOnExit(); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); } // Keep the existing app usable.
    }

    private void SetStatus(string value) { Status = value; Changed?.Invoke(); }
}
