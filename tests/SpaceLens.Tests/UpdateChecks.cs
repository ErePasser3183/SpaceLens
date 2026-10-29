using SpaceLens.Updates;

internal static class UpdateChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "SpaceLens-update-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new UpdateSettings(Path.Combine(root, "settings.json"));
            var backend = new FakeUpdater();
            var service = new UpdateService(backend, settings);
            check(service.Automatic, "automatic updates default to enabled");
            service.SetAutomatic(false);
            var reloaded = new UpdateSettings(Path.Combine(root, "settings.json")); reloaded.Load();
            await service.CheckAsync();
            check(!reloaded.Automatic && backend.Checks == 0, "disabled preference persists and prevents automatic network checks");
            await service.CheckAsync(manual: true);
            check(backend.Downloads == 1 && backend.Applies == 0 && service.Status.Contains("关闭后更新"), "manual check stages update without restarting a running app");
            service.PrepareForExit(); service.PrepareForExit();
            check(backend.Applies == 1, "prepared update is scheduled exactly once on actual exit");

            backend = new FakeUpdater(); service = new(backend, new(Path.Combine(root, "second.json")));
            await service.CheckAsync();
            service.SetAutomatic(false); service.PrepareForExit();
            check(backend.Applies == 0, "turning automatic updates off also prevents applying a staged update on exit");

            backend = new FakeUpdater { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            service = new(backend, new(Path.Combine(root, "third.json")));
            var running = service.CheckAsync();
            await service.CheckAsync(manual: true);
            check(backend.Checks == 1 && service.Busy, "overlapping checks do not start duplicate downloads");
            service.SetAutomatic(false);
            await running;
            service.PrepareForExit();
            check(!service.Busy && backend.PendingVersion is null && backend.Applies == 0, "disabling during download cancels it and does not schedule installation");

            backend = new FakeUpdater { Failure = true }; service = new(backend, new(Path.Combine(root, "fourth.json")));
            await service.CheckAsync(); service.PrepareForExit();
            check(!service.Busy && backend.Applies == 0 && service.Status.Contains("暂不可用"), "failed update remains non-blocking and never schedules replacement");

            backend = new FakeUpdater { PendingVersion = "1.2.0" }; service = new(backend, new(Path.Combine(root, "fifth.json")));
            await service.CheckAsync(); service.PrepareForExit();
            check(backend.Checks == 0 && backend.Downloads == 0 && backend.Applies == 1, "verified pending package can be applied after a later normal session");

            backend = new FakeUpdater { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            service = new(backend, new(Path.Combine(root, "sixth.json")));
            running = service.CheckAsync(); service.PrepareForExit(); await running;
            check(backend.Applies == 0 && backend.PendingVersion is null, "exit during an incomplete download leaves the running version intact");

            File.WriteAllText(Path.Combine(root, "corrupt.json"), "bad json");
            service = new(new FakeUpdater(), new(Path.Combine(root, "corrupt.json")));
            check(!service.Automatic, "corrupt preferences do not silently re-enable automatic updates");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FakeUpdater : IReleaseUpdater
    {
        public bool Available => true;
        public string CurrentVersion => "1.1.0";
        public string? PendingVersion { get; set; }
        public int Checks, Downloads, Applies;
        public bool Failure;
        public TaskCompletionSource? Gate;
        public Task<string?> CheckAsync(CancellationToken cancellation) { Checks++; return Task.FromResult<string?>("1.2.0"); }
        public async Task DownloadAsync(CancellationToken cancellation)
        {
            Downloads++;
            if (Failure) throw new IOException("checksum mismatch");
            if (Gate is not null) await Gate.Task.WaitAsync(cancellation);
            cancellation.ThrowIfCancellationRequested();
            PendingVersion = "1.2.0";
        }
        public void ApplyOnExit() => Applies++;
    }
}
