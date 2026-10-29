using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

internal static class DownloadRestartChecks
{
    internal static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "SpaceLens-restart-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        byte[] bytes = new byte[8 * 1024 * 1024]; new Random(414).NextBytes(bytes);
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        string cache = Path.Combine(root, "cache"), target = Path.Combine(root, "package.nupkg");
        await using var server = new RangeServer(bytes);
        try
        {
            using (var first = Launch())
            {
                try
                {
                    var watch = Stopwatch.StartNew();
                    while (watch.Elapsed < TimeSpan.FromSeconds(15) && !first.HasExited)
                    {
                        if (Directory.Exists(cache) && Directory.GetFiles(cache, "part-*.bin", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length) >= 128 * 1024) break;
                        await Task.Delay(20);
                    }
                    if (first.HasExited) throw new Exception("Download fixture unexpectedly exited: " + await first.StandardError.ReadToEndAsync());
                    first.Kill(); await first.WaitForExitAsync();
                }
                finally { if (!first.HasExited) { first.Kill(); await first.WaitForExitAsync(); } }
            }
            long saved = Directory.GetFiles(cache, "part-*.bin", SearchOption.AllDirectories).Sum(p => new FileInfo(p).Length);
            int requestsBefore = server.Starts.Count;
            using (var second = Launch())
            {
                try
                {
                    using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await second.WaitForExitAsync(limit.Token);
                    if (second.ExitCode != 0) throw new Exception("Restart fixture failed: " + await second.StandardError.ReadToEndAsync());
                }
                finally { if (!second.HasExited) { second.Kill(); await second.WaitForExitAsync(); } }
            }
            check(saved > 0 && saved < bytes.Length && File.ReadAllBytes(target).SequenceEqual(bytes) &&
                server.Starts.Skip(requestsBefore).Any(offset => offset % (bytes.Length / 4) != 0),
                "killed download process resumes durable ranges over real HTTP after a fresh process starts");
        }
        finally { Directory.Delete(root, true); }

        Process Launch()
        {
            string dotnet = Path.Combine(Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..")), "dotnet.exe");
            var info = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (string value in new[] { Assembly.GetExecutingAssembly().Location, "--download-fixture", server.Url, cache, target, hash }) info.ArgumentList.Add(value);
            return Process.Start(info)!;
        }
    }

    private sealed class RangeServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly ConcurrentBag<Task> requests = [];
        private readonly Task loop;
        private readonly byte[] bytes;
        public readonly ConcurrentQueue<long> Starts = [];
        public string Url { get; }
        public RangeServer(byte[] bytes)
        {
            this.bytes = bytes;
            listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/package.nupkg";
            loop = AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                    requests.Add(HandleAsync(await listener.AcceptTcpClientAsync(stop.Token)));
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
        private async Task HandleAsync(TcpClient socket)
        {
            using (socket)
            try
            {
                var stream = socket.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                long start = 0, end = bytes.Length - 1;
                bool range = false;
                for (string? line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token));)
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        var bounds = line[13..].Split('-'); start = long.Parse(bounds[0]);
                        if (bounds.Length > 1 && bounds[1].Length > 0) end = long.Parse(bounds[1]);
                        range = true;
                    }
                Starts.Enqueue(start);
                string header = $"HTTP/1.1 {(range ? "206 Partial Content" : "200 OK")}\r\nContent-Length: {end - start + 1}\r\nETag: \"fixture\"\r\nConnection: close\r\n";
                if (range) header += $"Content-Range: bytes {start}-{end}/{bytes.Length}\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header + "\r\n"), stop.Token);
                for (long offset = start; offset <= end;)
                {
                    int count = (int)Math.Min(32768, end - offset + 1);
                    await stream.WriteAsync(bytes.AsMemory((int)offset, count), stop.Token);
                    offset += count; await Task.Delay(8, stop.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop(); await loop; await Task.WhenAll(requests); stop.Dispose();
        }
    }
}
