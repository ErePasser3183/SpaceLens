using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Velopack.Sources;

namespace SpaceLens.Updates;

/// <summary>
/// Adapted from PCL's ModNet.NetFile / NetThread flow (龙腾猫跃及 PCL 贡献者):
/// range workers, bounded retries, sequential fallback, merge and verification.
/// This smaller update-only implementation adds durable cross-process checkpoints.
/// See docs/第三方参考.md and docs/licenses/PCL-LICENCE.txt.
/// </summary>
public sealed class ResumableDownloader : IFileDownloader
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(20), MaxConnectionsPerServer = 4,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient client;
    private readonly HttpClientFileDownloader metadata = new();
    private readonly string cacheRoot;
    private readonly TimeSpan idleTimeout;
    public string? ExpectedSha256 { get; set; }

    public ResumableDownloader(string? cacheRoot = null, HttpClient? client = null, TimeSpan? idleTimeout = null)
    {
        this.cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SpaceLens", "downloads"));
        this.client = client ?? Client;
        this.idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
    }

    public Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        => metadata.DownloadBytes(url, headers, Math.Min(timeout, 1));
    public Task<string> DownloadString(string url, IDictionary<string, string>? headers = null, double timeout = 30)
        => metadata.DownloadString(url, headers, Math.Min(timeout, 1));

    public async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers = null, double timeout = 30, CancellationToken cancelToken = default)
    {
        string? expected = ExpectedSha256;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url + "\n" + expected)));
        string folder = Path.Combine(cacheRoot, key);
        Directory.CreateDirectory(folder);
        // The OS releases this lock on crashes; a second app instance must not append to the same parts.
        await using var fileLock = new FileStream(Path.Combine(folder, "download.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(timeout > 0 ? timeout : 30));
        CancellationToken token = deadline.Token;
        var remote = await RetryAsync(() => ProbeAsync(url, headers, token), token);
        string statePath = Path.Combine(folder, "state.json");
        State? saved = null;
        try { saved = JsonSerializer.Deserialize<State>(await File.ReadAllTextAsync(statePath, token)); }
        catch (Exception ex) when (ex is IOException or JsonException) { }
        // Resume only when immutable package hash or a strong validator identifies the content.
        if (saved != remote || expected is null && remote.ETag is null && remote.Modified is null) ClearParts(folder);
        await File.WriteAllTextAsync(statePath + ".tmp", JsonSerializer.Serialize(remote), token);
        File.Move(statePath + ".tmp", statePath, true);
        int lastPercent = -1;
        void Report(long bytes)
        {
            int percent = remote.Length > 0 ? (int)Math.Clamp(bytes * 100d / remote.Length, 0, 99) : 0;
            lock (fileLock) { if (percent == lastPercent) return; lastPercent = percent; progress(percent); }
        }
        string assembled = Path.Combine(folder, "assembled.tmp");
        try
        {
            if (remote.Range && remote.Length > 0)
            {
                try { await DownloadPartsAsync(url, headers, folder, remote, Report, token); }
                catch (RangeRejectedException) { ClearParts(folder); remote = remote with { Range = false }; }
            }
            if (!remote.Range)
            {
                await RetryAsync(async () =>
                {
                    using var request = Request(url, headers);
                    using var response = await SendAsync(request, token);
                    response.EnsureSuccessStatusCode();
                    if (response.StatusCode != HttpStatusCode.OK) throw new IOException("下载服务器返回了不完整内容。");
                    await using var output = new FileStream(PartPath(folder, 0), FileMode.Create, FileAccess.Write, FileShare.Read, 65536, true);
                    await CopyResponseAsync(response, output, remote.Length, Report, token);
                    return true;
                }, token);
            }
            int count = remote.Range ? remote.Parts : 1;
            await using (var merged = new FileStream(assembled, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                for (int i = 0; i < count; i++)
                {
                    await using var part = File.OpenRead(PartPath(folder, i));
                    await part.CopyToAsync(merged, token);
                }
            if (remote.Length >= 0 && new FileInfo(assembled).Length != remote.Length) throw new InvalidDataException("更新包长度不一致。");
            if (!string.IsNullOrEmpty(expected))
            {
                await using var stream = File.OpenRead(assembled);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
                if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包校验失败，已丢弃损坏的下载进度。");
            }
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetFile))!);
            await using (var input = File.OpenRead(assembled))
            await using (var output = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                await input.CopyToAsync(output, token);
            ClearParts(folder);
            File.Delete(statePath);
            progress(100);
        }
        catch (InvalidDataException) { ClearParts(folder); File.Delete(statePath); throw; }
        finally { if (File.Exists(assembled)) File.Delete(assembled); }
    }

    private async Task<State> ProbeAsync(string url, IDictionary<string, string>? headers, CancellationToken token)
    {
        using var request = Request(url, headers);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await SendAsync(request, token);
        response.EnsureSuccessStatusCode();
        var range = response.Content.Headers.ContentRange;
        bool supportsRange = response.StatusCode == HttpStatusCode.PartialContent && range?.Unit == "bytes" && range.From == 0 && range.To == 0 && range.Length > 0;
        long length = supportsRange ? range!.Length!.Value : response.Content.Headers.ContentLength ?? -1;
        if (response.StatusCode == HttpStatusCode.PartialContent && !supportsRange) throw new IOException("下载服务器的分段响应无效。");
        string? etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
        return new(length, etag, response.Content.Headers.LastModified, supportsRange, length >= 4 * 1024 * 1024 ? 4 : 1);
    }

    private async Task DownloadPartsAsync(string url, IDictionary<string, string>? headers, string folder, State remote, Action<long> progress, CancellationToken token)
    {
        using var workers = CancellationTokenSource.CreateLinkedTokenSource(token);
        long[] sizes = new long[remote.Parts];
        for (int i = 0; i < sizes.Length; i++)
        {
            long expected = End(i) - Start(i) + 1;
            string path = PartPath(folder, i);
            if (File.Exists(path) && new FileInfo(path).Length > expected) File.Delete(path);
            sizes[i] = File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        void Report()
        {
            long total = 0;
            for (int i = 0; i < sizes.Length; i++) total += Interlocked.Read(ref sizes[i]);
            progress(total);
        }
        Report();
        Exception? failure = null;
        var tasks = Enumerable.Range(0, remote.Parts).Select(async i =>
        {
            try
            {
                await RetryAsync(async () =>
                {
                    long offset = Start(i) + sizes[i];
                    if (offset > End(i)) return true;
                    using var request = Request(url, headers);
                    request.Headers.Range = new RangeHeaderValue(offset, End(i));
                    if (remote.ETag is not null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(remote.ETag));
                    else if (remote.Modified is { } modified) request.Headers.IfRange = new RangeConditionHeaderValue(modified);
                    using var response = await SendAsync(request, workers.Token);
                    if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.RequestedRangeNotSatisfiable) throw new RangeRejectedException();
                    response.EnsureSuccessStatusCode();
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" || range.From != offset || range.To != End(i) || range.Length != remote.Length)
                        throw new RangeRejectedException();
                    if (remote.ETag is not null && response.Headers.ETag is { } current && current.ToString() != remote.ETag) throw new RangeRejectedException();
                    await using var output = new FileStream(PartPath(folder, i), FileMode.Append, FileAccess.Write, FileShare.Read, 65536, true);
                    long initial = sizes[i];
                    await CopyResponseAsync(response, output, End(i) - offset + 1, bytes => { Interlocked.Exchange(ref sizes[i], initial + bytes); Report(); }, workers.Token);
                    return true;
                }, workers.Token);
            }
            catch (Exception ex) { Interlocked.CompareExchange(ref failure, ex, null); workers.Cancel(); }
        }).ToArray();
        await Task.WhenAll(tasks);
        token.ThrowIfCancellationRequested();
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        long Start(int i) => remote.Length * i / remote.Parts;
        long End(int i) => remote.Length * (i + 1) / remote.Parts - 1;
    }

    private async Task CopyResponseAsync(HttpResponseMessage response, FileStream output, long expected, Action<long> progress, CancellationToken token)
    {
        await using var input = await response.Content.ReadAsStreamAsync(token);
        byte[] buffer = new byte[65536];
        long done = 0;
        while (true)
        {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
            idle.CancelAfter(idleTimeout);
            int read = await input.ReadAsync(buffer, idle.Token);
            if (read == 0) break;
            if (expected >= 0 && done + read > expected) throw new RangeRejectedException();
            // Finish each disk write before observing cancellation so its length is a valid checkpoint.
            await output.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None);
            await output.FlushAsync(CancellationToken.None);
            done += read; progress(done);
            token.ThrowIfCancellationRequested();
        }
        if (expected >= 0 && done != expected) throw new IOException("下载连接提前中断，已保存进度。");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        headersDeadline.CancelAfter(idleTimeout);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersDeadline.Token);
    }

    private static HttpRequestMessage Request(string url, IDictionary<string, string>? headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("SpaceLens-Updater/1.0");
        if (headers is not null) foreach (var header in headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        request.Headers.AcceptEncoding.Clear(); request.Headers.AcceptEncoding.ParseAdd("identity");
        return request;
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return await action(); }
            catch (Exception ex) when (attempt < 2 && !token.IsCancellationRequested && ex is not RangeRejectedException &&
                (ex is IOException or HttpRequestException or OperationCanceledException))
            { await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), token); }
        }
    }

    private static string PartPath(string folder, int i) => Path.Combine(folder, $"part-{i}.bin");
    private static void ClearParts(string folder)
    {
        for (int i = 0; i < 4; i++) File.Delete(PartPath(folder, i));
    }
    private sealed record State(long Length, string? ETag, DateTimeOffset? Modified, bool Range, int Parts);
    private sealed class RangeRejectedException : IOException;
}
