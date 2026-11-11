using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using PCL.Shion.Core.IO;

namespace PCL.Shion.Core.Download;

/// <summary><see cref="SegmentedDownloader"/> 的选项。</summary>
public sealed class SegmentedDownloadOptions
{
    /// <summary>同时使用的连接数上限，取值 1 到 <see cref="SegmentedDownloader.MaxConnections"/>。</summary>
    public int Connections { get; init; } = 8;

    /// <summary>每段的最小字节数；文件较小时会少开连接。</summary>
    public long MinSegmentSize { get; init; } = 1024 * 1024;

    /// <summary>每段在没有任何进展时连续失败的最多重试次数，只对网络错误、超时、408、429 与 5xx 生效。</summary>
    public int MaxRetries { get; init; } = 5;

    /// <summary>等待响应头以及两次收到数据之间的最长时间。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 期望的哈希（十六进制，不区分大小写）。按长度识别算法：32 位为 MD5，40 位为 SHA-1，64 位为 SHA-256，128 位为 SHA-512。
    /// 为 null 或空白时不校验。
    /// </summary>
    public string? ExpectedHash { get; init; }

    /// <summary>第一次重试前的等待时间，之后每次翻倍。</summary>
    internal TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>报告进度与保存续传状态的间隔。</summary>
    internal TimeSpan ReportInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// 单个文件的下载进度。
/// </summary>
/// <param name="DownloadedBytes">已经写入的字节数，包括续传前已有的部分。</param>
/// <param name="TotalBytes">文件大小，服务器没有提供时为 null。</param>
/// <param name="BytesPerSecond">最近约 2 秒的平均速度。</param>
/// <param name="Connections">正在传输的连接数。</param>
/// <param name="CanResume">服务器支持分段请求，取消或失败后可以从已下载的位置继续。</param>
public sealed record SegmentedDownloadProgress(
    long DownloadedBytes,
    long? TotalBytes,
    double BytesPerSecond,
    int Connections,
    bool CanResume)
{
    /// <summary>按当前速度估算的剩余时间，无法估算时为 null。</summary>
    public TimeSpan? Remaining => TotalBytes is { } total && BytesPerSecond > 1
        ? TimeSpan.FromSeconds(Math.Max(0, total - DownloadedBytes) / BytesPerSecond)
        : null;
}

/// <summary>下载的内容与期望的哈希不符。</summary>
public sealed class DownloadHashMismatchException(string expected, string actual)
    : Exception($"Hash mismatch: expected {expected}, received {actual}")
{
    public string Expected { get; } = expected;

    public string Actual { get; } = actual;
}

/// <summary>
/// 多线程下载单个文件：服务器支持 Range 请求时把文件分成若干段并行下载，否则退回单连接。
/// 下载中的数据写入 <c>目标.shiondl</c>，分段进度写入 <c>目标.shiondl.json</c>；取消、失败或启动器退出后，
/// 再次下载同一地址到同一位置会从已完成的位置继续（服务器的 ETag 或 Last-Modified 变化时重新开始）。
/// 下载完成并通过校验后才替换目标文件。
/// </summary>
public static class SegmentedDownloader
{
    /// <summary>连接数上限。</summary>
    public const int MaxConnections = 64;

    private const int BufferSize = 81920;

    private static readonly JsonSerializerOptions StateJson = new() { WriteIndented = false };

    /// <summary>下载中的数据文件。</summary>
    public static string PartialPathOf(string targetPath) => targetPath + ".shiondl";

    /// <summary>续传状态文件。</summary>
    public static string StatePathOf(string targetPath) => targetPath + ".shiondl.json";

    /// <summary>是否留有可以续传的未完成下载。</summary>
    public static bool HasPartial(string targetPath) =>
        File.Exists(PartialPathOf(targetPath)) && File.Exists(StatePathOf(targetPath));

    /// <summary>删除未完成下载留下的数据与状态文件。</summary>
    public static void DeletePartial(string targetPath)
    {
        DeleteQuietly(PartialPathOf(targetPath));
        DeleteQuietly(StatePathOf(targetPath));
    }

    /// <summary>
    /// 下载 <paramref name="url"/> 到 <paramref name="targetPath"/>，已存在的目标文件会在下载成功后被替换。
    /// </summary>
    /// <exception cref="OperationCanceledException">已取消；未完成的数据保留，可以续传。</exception>
    /// <exception cref="HttpRequestException">服务器返回错误或连接失败，重试用尽。</exception>
    /// <exception cref="DownloadHashMismatchException">内容与期望的哈希不符，数据已删除。</exception>
    public static async Task DownloadAsync(
        HttpClient http,
        Uri url,
        string targetPath,
        SegmentedDownloadOptions? options = null,
        IProgress<SegmentedDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        options ??= new SegmentedDownloadOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Connections, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Connections, MaxConnections);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MinSegmentSize);
        var hashAlgorithm = options.ExpectedHash is { } expected && !string.IsNullOrWhiteSpace(expected)
            ? AlgorithmOf(expected.Trim())
            : (HashAlgorithmName?)null;

        targetPath = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var partialPath = PartialPathOf(targetPath);
        var statePath = StatePathOf(targetPath);

        var probe = await WithRetriesAsync(options, token => ProbeAsync(http, url, options, token), cancellationToken);
        var tracker = new Tracker();
        using (probe.Response)
        {
            if (probe.Total is { } total && probe.Response.StatusCode == HttpStatusCode.PartialContent)
            {
                probe.Response.Dispose();
                await DownloadRangesAsync(http, url, partialPath, statePath, total, probe.Validator, options, tracker,
                    progress, cancellationToken);
            }
            else
            {
                // 服务器不支持分段：直接使用探测请求的响应体，不能续传
                DeleteQuietly(statePath);
                await DownloadSingleAsync(probe.Response, partialPath, probe.Total, options, tracker, progress,
                    cancellationToken);
            }
        }

        if (hashAlgorithm is { } algorithm)
        {
            var actual = await HashFileAsync(partialPath, algorithm, cancellationToken);
            if (!string.Equals(actual, options.ExpectedHash!.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                DeletePartial(targetPath);
                throw new DownloadHashMismatchException(options.ExpectedHash.Trim().ToLowerInvariant(), actual);
            }
        }

        File.Move(partialPath, targetPath, overwrite: true);
        DeleteQuietly(statePath);
    }

    /// <summary>按哈希长度识别算法；无法识别时抛出 <see cref="ArgumentException"/>。</summary>
    public static HashAlgorithmName AlgorithmOf(string hash)
    {
        if (!hash.All(Uri.IsHexDigit))
            throw new ArgumentException("The hash must be hexadecimal.", nameof(hash));
        return hash.Length switch
        {
            32 => HashAlgorithmName.MD5,
            40 => HashAlgorithmName.SHA1,
            64 => HashAlgorithmName.SHA256,
            128 => HashAlgorithmName.SHA512,
            _ => throw new ArgumentException("Unsupported hash length.", nameof(hash))
        };
    }

    private sealed record ProbeResult(HttpResponseMessage Response, long? Total, string? Validator);

    /// <summary>请求第一个字节：206 表示支持分段并给出总大小，200 表示不支持，此时直接用这个响应下载。</summary>
    private static async Task<ProbeResult> ProbeAsync(HttpClient http, Uri url, SegmentedDownloadOptions options,
        CancellationToken cancellationToken)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(options.Timeout);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        try
        {
            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 空文件无法请求第 0 个字节，改用普通请求
                response.Dispose();
                response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            }

            response.EnsureSuccessStatusCode();
            var validator = response.Headers.ETag?.ToString() ?? response.Content.Headers.LastModified?.ToString("R");
            if (response.StatusCode == HttpStatusCode.PartialContent &&
                response.Content.Headers.ContentRange is { HasLength: true, Length: { } length })
                return new ProbeResult(response, length, validator);
            if (response.StatusCode == HttpStatusCode.PartialContent)
                throw new HttpRequestException("The server returned a partial response without a total length.");
            return new ProbeResult(response, response.Content.Headers.ContentLength, validator);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private static async Task DownloadSingleAsync(HttpResponseMessage response, string partialPath, long? total,
        SegmentedDownloadOptions options, Tracker tracker, IProgress<SegmentedDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        tracker.Total = total;
        tracker.Connections = 1;
        using var reporter = StartReporter(tracker, progress, options, null, cancellationToken);
        try
        {
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            stall.CancelAfter(options.Timeout);
            await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
            await using var target = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.Read,
                BufferSize, FileOptions.Asynchronous);
            var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                int read;
                while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(options.Timeout);
                    await target.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                    tracker.Add(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (total is { } expected && tracker.Downloaded != expected)
                throw new HttpRequestException($"The connection closed after {tracker.Downloaded} of {expected} bytes.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The server stopped sending data.");
        }
        finally
        {
            tracker.Connections = 0;
            await reporter.StopAsync();
        }
    }

    private static async Task DownloadRangesAsync(HttpClient http, Uri url, string partialPath, string statePath,
        long total, string? validator, SegmentedDownloadOptions options, Tracker tracker,
        IProgress<SegmentedDownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var state = LoadState(statePath, partialPath, url, total, validator) ?? CreateState(url, total, validator, options);
        tracker.Total = total;
        tracker.CanResume = true;
        tracker.Add(state.Segments.Sum(segment => segment.Done));

        using var handle = File.OpenHandle(partialPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read,
            FileOptions.Asynchronous);
        if (RandomAccess.GetLength(handle) != total)
            RandomAccess.SetLength(handle, total);
        SaveState(statePath, state);

        using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var reporter = StartReporter(tracker, progress, options, () => SaveState(statePath, state), failure.Token);
        try
        {
            var workers = state.Segments.Where(segment => segment.Done < segment.Length)
                .Select(segment => DownloadSegmentAsync(http, url, handle, segment, validator, options, tracker, failure))
                .ToList();
            await Task.WhenAll(workers);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested && failure.IsCancellationRequested)
        {
            // 一段失败会取消其他段；抛出最先发生的真实错误，而不是其他段因此产生的取消
            throw tracker.FirstError ?? new HttpRequestException("A segment failed.");
        }
        finally
        {
            tracker.Connections = 0;
            await reporter.StopAsync();
            SaveState(statePath, state);
        }
    }

    private static async Task DownloadSegmentAsync(HttpClient http, Uri url, SafeFileHandle handle, SegmentState segment,
        string? validator, SegmentedDownloadOptions options, Tracker tracker, CancellationTokenSource failure)
    {
        var token = failure.Token;
        var failures = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        tracker.ChangeConnections(1);
        try
        {
            while (segment.Done < segment.Length)
            {
                var before = segment.Done;
                try
                {
                    await FetchRangeAsync(http, url, handle, segment, validator, options, tracker, buffer, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested && IsTransient(ex))
                {
                    failures = segment.Done > before ? 1 : failures + 1;
                    if (failures > options.MaxRetries)
                    {
                        tracker.Fail(ex);
                        await failure.CancelAsync();
                        throw;
                    }

                    var delay = Math.Min(30_000, options.RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 30)));
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    tracker.Fail(ex);
                    await failure.CancelAsync();
                    throw;
                }
            }
        }
        finally
        {
            tracker.ChangeConnections(-1);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task FetchRangeAsync(HttpClient http, Uri url, SafeFileHandle handle, SegmentState segment,
        string? validator, SegmentedDownloadOptions options, Tracker tracker, byte[] buffer, CancellationToken token)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
        stall.CancelAfter(options.Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var offset = segment.Start + segment.Done;
        request.Headers.Range = new RangeHeaderValue(offset, segment.End);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            if (response.StatusCode != HttpStatusCode.PartialContent ||
                response.Content.Headers.ContentRange is not { From: { } from } || from != offset)
                throw new InvalidDataException("The server ignored the requested range.");
            var currentValidator = response.Headers.ETag?.ToString() ??
                                   response.Content.Headers.LastModified?.ToString("R");
            if (validator is not null && currentValidator is not null && currentValidator != validator)
                throw new InvalidDataException("The file changed on the server during the download.");

            await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
            while (segment.Done < segment.Length)
            {
                var wanted = (int)Math.Min(buffer.Length, segment.Length - segment.Done);
                var read = await source.ReadAsync(buffer.AsMemory(0, wanted), stall.Token);
                if (read == 0)
                    throw new IOException("The connection closed before the segment was complete.");
                stall.CancelAfter(options.Timeout);
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), segment.Start + segment.Done, stall.Token);
                segment.Advance(read);
                tracker.Add(read);
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("The server stopped sending data.");
        }
    }

    // 网络错误、超时、408、429 与 5xx 可能只是暂时的；其余错误（包括服务器忽略分段、文件被替换）直接失败
    private static bool IsTransient(Exception exception) => exception switch
    {
        HttpRequestException { StatusCode: { } status } =>
            status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500,
        HttpRequestException or IOException or TimeoutException => exception is not InvalidDataException,
        _ => false
    };

    private static async Task<T> WithRetriesAsync<T>(SegmentedDownloadOptions options,
        Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await action(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < options.MaxRetries)
            {
                // 探测超时
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsTransient(ex) && attempt < options.MaxRetries)
            {
            }

            var delay = Math.Min(30_000, options.RetryDelay.TotalMilliseconds * Math.Pow(2, Math.Min(attempt, 30)));
            await Task.Delay(TimeSpan.FromMilliseconds(delay), cancellationToken);
        }
    }

    #region 续传状态

    internal sealed class DownloadState
    {
        public string Url { get; set; } = "";

        public long Total { get; set; }

        public string? Validator { get; set; }

        public List<SegmentState> Segments { get; set; } = [];
    }

    internal sealed class SegmentState
    {
        private long _done;

        public long Start { get; set; }

        /// <summary>最后一个字节的位置（含）。</summary>
        public long End { get; set; }

        public long Done
        {
            get => Interlocked.Read(ref _done);
            set => Interlocked.Exchange(ref _done, value);
        }

        public long Length => End - Start + 1;

        public void Advance(long count) => Interlocked.Add(ref _done, count);
    }

    private static DownloadState CreateState(Uri url, long total, string? validator, SegmentedDownloadOptions options)
    {
        var state = new DownloadState { Url = url.AbsoluteUri, Total = total, Validator = validator };
        if (total == 0)
            return state;
        var count = (int)Math.Clamp((total + options.MinSegmentSize - 1) / options.MinSegmentSize, 1, options.Connections);
        var size = total / count;
        for (var i = 0; i < count; i++)
        {
            var start = i * size;
            var end = i == count - 1 ? total - 1 : start + size - 1;
            state.Segments.Add(new SegmentState { Start = start, End = end });
        }

        return state;
    }

    /// <summary>读取续传状态；地址、大小或服务器版本不一致，或数据文件缺失时返回 null 并清理旧数据。</summary>
    private static DownloadState? LoadState(string statePath, string partialPath, Uri url, long total, string? validator)
    {
        var result = JsonFileStore.Read<DownloadState>(statePath, StateJson);
        var state = result.Value;
        var valid = state is not null && state.Url == url.AbsoluteUri && state.Total == total &&
                    state.Validator == validator && File.Exists(partialPath) &&
                    new FileInfo(partialPath).Length == total &&
                    state.Segments.Count > 0 &&
                    state.Segments.All(s => s.Start >= 0 && s.End < total && s.Start <= s.End + 1 &&
                                            s.Done >= 0 && s.Done <= s.Length);
        if (valid)
            return state;
        DeleteQuietly(partialPath);
        DeleteQuietly(statePath + ".bak");
        DeleteQuietly(statePath);
        return null;
    }

    private static readonly Lock StateGate = new();

    private static void SaveState(string statePath, DownloadState state)
    {
        try
        {
            lock (StateGate)
                AtomicFile.WriteAllText(statePath, JsonSerializer.Serialize(state, StateJson));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 状态写不进去只影响续传，不影响本次下载
        }
    }

    #endregion

    #region 进度

    private sealed class Tracker
    {
        private long _downloaded;
        private int _connections;
        private Exception? _firstError;

        public long Downloaded => Interlocked.Read(ref _downloaded);

        public long? Total { get; set; }

        public bool CanResume { get; set; }

        public int Connections
        {
            get => Volatile.Read(ref _connections);
            set => Volatile.Write(ref _connections, value);
        }

        public Exception? FirstError => Volatile.Read(ref _firstError);

        public void Add(long count) => Interlocked.Add(ref _downloaded, count);

        public void ChangeConnections(int delta) => Interlocked.Add(ref _connections, delta);

        public void Fail(Exception error) => Interlocked.CompareExchange(ref _firstError, error, null);
    }

    private sealed class Reporter(Task loop, CancellationTokenSource stop) : IDisposable
    {
        public async Task StopAsync()
        {
            await stop.CancelAsync();
            try
            {
                await loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        public void Dispose() => stop.Dispose();
    }

    private static Reporter StartReporter(Tracker tracker, IProgress<SegmentedDownloadProgress>? progress,
        SegmentedDownloadOptions options, Action? checkpoint, CancellationToken cancellationToken)
    {
        var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var loop = Task.Run(async () =>
        {
            var samples = new Queue<(long Time, long Bytes)>();
            var lastCheckpoint = Stopwatch.GetTimestamp();
            using var timer = new PeriodicTimer(options.ReportInterval);
            try
            {
                Report();
                while (await timer.WaitForNextTickAsync(stop.Token))
                {
                    Report();
                    if (checkpoint is not null && Stopwatch.GetElapsedTime(lastCheckpoint) >= TimeSpan.FromSeconds(1))
                    {
                        checkpoint();
                        lastCheckpoint = Stopwatch.GetTimestamp();
                    }
                }
            }
            finally
            {
                Report();
            }

            void Report()
            {
                var now = Stopwatch.GetTimestamp();
                var bytes = tracker.Downloaded;
                samples.Enqueue((now, bytes));
                while (samples.Count > 1 && Stopwatch.GetElapsedTime(samples.Peek().Time, now) > TimeSpan.FromSeconds(2))
                    samples.Dequeue();
                var (firstTime, firstBytes) = samples.Peek();
                var seconds = Stopwatch.GetElapsedTime(firstTime, now).TotalSeconds;
                var speed = seconds > 0 ? (bytes - firstBytes) / seconds : 0;
                progress?.Report(new SegmentedDownloadProgress(bytes, tracker.Total, speed, tracker.Connections,
                    tracker.CanResume));
            }
        }, CancellationToken.None);
        return new Reporter(loop, stop);
    }

    #endregion

    private static async Task<string> HashFileAsync(string path, HashAlgorithmName algorithm,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(algorithm);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
                hash.AppendData(buffer, 0, read);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
