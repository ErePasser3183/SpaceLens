using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using SpaceLens.Updates;

internal static class DownloadChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "SpaceLens-download-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] bytes = new byte[5 * 1024 * 1024]; new Random(412).NextBytes(bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        const string url = "https://download.test/SpaceLens.nupkg";
        try
        {
            using var server = new TestServer(bytes);
            using var client = new HttpClient(server);
            string cache = Path.Combine(root, "resume"), target = Path.Combine(root, "resume.nupkg");
            var engine = new ResumableDownloader(cache, client) { ExpectedSha256 = hash };
            using var cancel = new CancellationTokenSource();
            bool cancelled = false;
            try { await engine.DownloadFile(url, target, p => { if (p >= 8) cancel.Cancel(); }, cancelToken: cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            long saved = Directory.GetFiles(cache, "part-*.bin", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
            check(cancelled && saved > 0 && saved < bytes.Length && !File.Exists(target), "cancelled download preserves partial bytes without staging an update");
            long before = server.BytesRead;
            // A new engine instance has no in-memory progress from the cancelled request.
            var resumed = new ResumableDownloader(cache, client) { ExpectedSha256 = hash };
            var progress = new List<int>();
            await resumed.DownloadFile(url, target, p => { lock (progress) progress.Add(p); });
            check(File.ReadAllBytes(target).SequenceEqual(bytes) && server.BytesRead - before == bytes.Length - saved,
                "new downloader resumes exact missing ranges from on-disk progress");
            check(progress[0] > 0 && progress[^1] == 100 && server.MaxActive > 1 && server.MaxActive <= 4,
                "resume reports retained percentage and uses at most four parallel workers");
            check(!Directory.GetFiles(cache, "part-*.bin", SearchOption.AllDirectories).Any(), "verified successful download removes its partial files");

            foreach (string mode in new[] { "no-range", "range-lost", "transient", "short-body", "unknown-length" })
            {
                using var otherServer = new TestServer(bytes) { Mode = mode };
                using var otherClient = new HttpClient(otherServer);
                var other = new ResumableDownloader(Path.Combine(root, mode), otherClient) { ExpectedSha256 = hash };
                string file = Path.Combine(root, mode + ".nupkg");
                await other.DownloadFile(url, file, _ => { });
                check(File.ReadAllBytes(file).SequenceEqual(bytes), $"download survives {mode} responses without corrupting bytes");
            }

            string corruptCache = Path.Combine(root, "corrupt"), corruptTarget = Path.Combine(root, "corrupt.nupkg");
            using var cancelCorrupt = new CancellationTokenSource();
            var corrupt = new ResumableDownloader(corruptCache, client) { ExpectedSha256 = hash };
            try { await corrupt.DownloadFile(url, corruptTarget, p => { if (p >= 8) cancelCorrupt.Cancel(); }, cancelToken: cancelCorrupt.Token); }
            catch (OperationCanceledException) { }
            string piece = Directory.GetFiles(corruptCache, "part-*.bin", SearchOption.AllDirectories).First(p => new FileInfo(p).Length > 0);
            using (var tamper = File.Open(piece, FileMode.Open, FileAccess.ReadWrite)) { byte original = (byte)tamper.ReadByte(); tamper.Position = 0; tamper.WriteByte((byte)(original ^ 0xFF)); }
            File.WriteAllText(corruptTarget, "previous package");
            bool rejected = false;
            try { await corrupt.DownloadFile(url, corruptTarget, _ => { }); } catch (InvalidDataException) { rejected = true; }
            check(rejected && File.ReadAllText(corruptTarget) == "previous package" && !Directory.GetFiles(corruptCache, "part-*.bin", SearchOption.AllDirectories).Any(),
                "checksum rejects corrupt checkpoints before publishing and clears them for a clean retry");
            await corrupt.DownloadFile(url, corruptTarget, _ => { });
            check(File.ReadAllBytes(corruptTarget).SequenceEqual(bytes), "retry after invalid checkpoint downloads a verified package");

            string changedCache = Path.Combine(root, "changed"), changedTarget = Path.Combine(root, "changed.nupkg");
            using var changedServer = new TestServer(bytes);
            using var changedClient = new HttpClient(changedServer);
            var changed = new ResumableDownloader(changedCache, changedClient);
            using var stop = new CancellationTokenSource();
            try { await changed.DownloadFile(url, changedTarget, p => { if (p >= 8) stop.Cancel(); }, cancelToken: stop.Token); }
            catch (OperationCanceledException) { }
            changedServer.ETag = "\"second\"";
            changedServer.Data = bytes.Select(b => (byte)(b ^ 0x55)).ToArray();
            before = changedServer.BytesRead;
            await changed.DownloadFile(url, changedTarget, _ => { });
            check(File.ReadAllBytes(changedTarget).SequenceEqual(changedServer.Data) && changedServer.BytesRead - before == bytes.Length,
                "changed ETag invalidates saved parts instead of mixing releases");

            string switchCache = Path.Combine(root, "version-switch"), switchTarget = Path.Combine(root, "latest.nupkg");
            var switching = new ResumableDownloader(switchCache, client) { ExpectedSha256 = hash };
            using var stopOld = new CancellationTokenSource();
            try { await switching.DownloadFile(url + "/1.1.4", switchTarget, p => { if (p >= 8) stopOld.Cancel(); }, cancelToken: stopOld.Token); }
            catch (OperationCanceledException) { }
            string oldFolder = Directory.GetDirectories(switchCache).Single();
            File.WriteAllText(Path.Combine(oldFolder, "unrelated.txt"), "keep");
            string activeFolder = Path.Combine(switchCache, new string('A', 64));
            Directory.CreateDirectory(activeFolder);
            string activePart = Path.Combine(activeFolder, "part-0.bin");
            File.WriteAllText(activePart, "active download");
            using (var activeLock = new FileStream(Path.Combine(activeFolder, "download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            {
                using var stopNew = new CancellationTokenSource();
                try { await switching.DownloadFile(url + "/1.1.5", switchTarget, p => { if (p >= 8) stopNew.Cancel(); }, cancelToken: stopNew.Token); }
                catch (OperationCanceledException) { }
                check(!Directory.GetFiles(oldFolder, "part-*.bin").Any() && !File.Exists(Path.Combine(oldFolder, "state.json")),
                    "new release clears abandoned older partial download and metadata");
                check(File.ReadAllText(activePart) == "active download" && File.ReadAllText(Path.Combine(oldFolder, "unrelated.txt")) == "keep",
                    "cache cleanup preserves locked downloads and unrelated files");
            }
            long retained = Directory.GetFiles(switchCache, "part-*.bin", SearchOption.AllDirectories)
                .Where(p => p != activePart).Sum(p => new FileInfo(p).Length);
            before = server.BytesRead;
            await switching.DownloadFile(url + "/1.1.5", switchTarget, _ => { });
            check(!File.Exists(activePart) && retained > 0 && server.BytesRead - before == bytes.Length - retained && File.ReadAllBytes(switchTarget).SequenceEqual(bytes),
                "later cleanup retries unlocked stale cache while retaining current release resume bytes");
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class TestServer(byte[] data) : HttpMessageHandler
    {
        public byte[] Data = data;
        public string ETag = "\"first\"";
        public string Mode = "range";
        public long BytesRead;
        public int MaxActive;
        private int active, fault;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var range = request.Headers.Range?.Ranges.Single();
            bool probe = range?.From == 0 && range.To == 0;
            if (!probe && Mode == "transient" && Interlocked.Increment(ref fault) == 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway));
            bool partial = range is not null && Mode is not ("no-range" or "unknown-length") && (Mode != "range-lost" || probe);
            long start = partial ? range!.From!.Value : 0, end = partial ? range!.To ?? Data.Length - 1 : Data.Length - 1;
            if (start >= Data.Length) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
            int length = (int)(end - start + 1);
            int delivered = !probe && Mode == "short-body" && Interlocked.Increment(ref fault) == 1 ? length / 2 : length;
            var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK);
            response.Headers.ETag = EntityTagHeaderValue.Parse(ETag);
            response.Content = new StreamContent(new BodyStream(Data, (int)start, delivered, this, probe));
            if (Mode != "unknown-length") response.Content.Headers.ContentLength = length;
            if (partial) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, Data.Length);
            return Task.FromResult(response);
        }

        private sealed class BodyStream(byte[] data, int start, int length, TestServer server, bool probe) : Stream
        {
            private int position;
            private bool entered, disposed;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => position; set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (!entered) { entered = true; int count = Interlocked.Increment(ref server.active); lock (server) server.MaxActive = Math.Max(server.MaxActive, count); }
                await Task.Delay(1, cancellationToken);
                int countRead = Math.Min(Math.Min(buffer.Length, 65536), length - position);
                data.AsMemory(start + position, countRead).CopyTo(buffer);
                position += countRead;
                if (!probe) Interlocked.Add(ref server.BytesRead, countRead);
                return countRead;
            }
            protected override void Dispose(bool disposing)
            {
                if (!disposed && entered) Interlocked.Decrement(ref server.active);
                disposed = true; base.Dispose(disposing);
            }
            public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }
}
