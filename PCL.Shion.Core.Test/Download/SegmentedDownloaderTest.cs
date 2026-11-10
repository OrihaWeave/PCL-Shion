using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using PCL.Shion.Core.Download;

namespace PCL.Shion.Core.Test;

[TestClass]
public sealed class SegmentedDownloaderTest
{
    private static readonly Uri Url = new("https://files.invalid/big.bin");

    private FakeMinecraftRoot _root = null!;

    [TestInitialize]
    public void CreateRoot() => _root = new FakeMinecraftRoot();

    [TestCleanup]
    public void DeleteRoot() => _root.Dispose();

    private static byte[] CreateContent(int length)
    {
        var content = new byte[length];
        new Random(42).NextBytes(content);
        return content;
    }

    private static SegmentedDownloadOptions Options(int connections = 4, int maxRetries = 2, string? hash = null) => new()
    {
        Connections = connections,
        MinSegmentSize = 1000,
        MaxRetries = maxRetries,
        ExpectedHash = hash,
        RetryDelay = TimeSpan.Zero,
        ReportInterval = TimeSpan.FromMilliseconds(20),
        Timeout = TimeSpan.FromSeconds(10)
    };

    [TestMethod]
    public async Task SplitsTheFileIntoParallelRanges()
    {
        var content = CreateContent(10_000);
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var target = _root.GetPath("out/big.bin");
        var reports = new ConcurrentQueue<SegmentedDownloadProgress>();

        await SegmentedDownloader.DownloadAsync(http, Url, target, Options(connections: 4),
            new SyncProgress(reports.Enqueue));

        CollectionAssert.AreEqual(content, File.ReadAllBytes(target));
        // 一个探测请求加四段
        Assert.AreEqual(5, server.Ranges.Count);
        Assert.AreEqual(content.Length + 1, server.Ranges.Sum(r => r.To - r.From + 1));
        CollectionAssert.AreEqual(new[] { target }, Directory.GetFiles(Path.GetDirectoryName(target)!));
        var last = reports.Last();
        Assert.AreEqual(content.Length, last.DownloadedBytes);
        Assert.AreEqual(content.Length, last.TotalBytes);
        Assert.IsTrue(last.CanResume);
    }

    [TestMethod]
    public async Task UsesFewerConnectionsForSmallFiles()
    {
        var content = CreateContent(1500);
        var server = new RangeServer(content);
        using var http = new HttpClient(server);
        var target = _root.GetPath("small.bin");

        await SegmentedDownloader.DownloadAsync(http, Url, target, Options(connections: 16));

        CollectionAssert.AreEqual(content, File.ReadAllBytes(target));
        Assert.AreEqual(3, server.Ranges.Count);
    }

    [TestMethod]
    public async Task FallsBackToASingleConnectionWithoutRangeSupport()
    {
        var content = CreateContent(5000);
        var server = new RangeServer(content) { SupportsRanges = false };
        using var http = new HttpClient(server);
        var target = _root.GetPath("single.bin");
        var reports = new ConcurrentQueue<SegmentedDownloadProgress>();

        await SegmentedDownloader.DownloadAsync(http, Url, target, Options(), new SyncProgress(reports.Enqueue));

        CollectionAssert.AreEqual(content, File.ReadAllBytes(target));
        Assert.AreEqual(1, server.RequestCount);
        Assert.IsFalse(reports.Last().CanResume);
        Assert.IsFalse(SegmentedDownloader.HasPartial(target));
    }

    [TestMethod]
    public async Task DownloadsAnEmptyFile()
    {
        var server = new RangeServer([]);
        using var http = new HttpClient(server);
        var target = _root.GetPath("empty.bin");

        await SegmentedDownloader.DownloadAsync(http, Url, target, Options());

        Assert.AreEqual(0, new FileInfo(target).Length);
    }

    [TestMethod]
    public async Task RetriesTransientErrors()
    {
        var content = CreateContent(4000);
        var failures = 0;
        var server = new RangeServer(content)
        {
            Intercept = (from, _) => from > 0 && Interlocked.Increment(ref failures) <= 2
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : null
        };
        using var http = new HttpClient(server);
        var target = _root.GetPath("retry.bin");

        await SegmentedDownloader.DownloadAsync(http, Url, target, Options(maxRetries: 3));

        CollectionAssert.AreEqual(content, File.ReadAllBytes(target));
    }

    [TestMethod]
    public async Task ResumesFromThePartialDataAfterAFailure()
    {
        var content = CreateContent(8000);
        var target = _root.GetPath("resume.bin");
        // 第一次：最后一段传到一半就断开，并且不允许重试
        var broken = new RangeServer(content) { CutAfter = (from, _) => from >= 6000 ? 500 : null };
        using (var http = new HttpClient(broken))
            await Assert.ThrowsAsync<IOException>(() =>
                SegmentedDownloader.DownloadAsync(http, Url, target, Options(maxRetries: 0)));

        Assert.IsFalse(File.Exists(target));
        Assert.IsTrue(SegmentedDownloader.HasPartial(target));

        var healthy = new RangeServer(content);
        using (var http = new HttpClient(healthy))
            await SegmentedDownloader.DownloadAsync(http, Url, target, Options(maxRetries: 0));

        CollectionAssert.AreEqual(content, File.ReadAllBytes(target));
        Assert.IsFalse(SegmentedDownloader.HasPartial(target));
        // 第二次只补未完成的部分：断开的最后一段从第 6500 字节继续
        CollectionAssert.Contains(healthy.Ranges.ToList(), (6500L, 7999L));
        Assert.IsTrue(healthy.Ranges.Sum(r => r.To - r.From + 1) < content.Length);
    }

    [TestMethod]
    public async Task CancellationKeepsTheDataForResuming()
    {
        var content = CreateContent(8000);
        var target = _root.GetPath("cancel.bin");
        using var cancel = new CancellationTokenSource();
        var stalled = new RangeServer(content)
        {
            CutAfter = (from, _) => from >= 4000 ? 100 : null,
            StallInsteadOfCut = true,
            OnStall = () => cancel.Cancel()
        };
        using (var http = new HttpClient(stalled))
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                SegmentedDownloader.DownloadAsync(http, Url, target, Options(connections: 2), null, cancel.Token));

        Assert.IsTrue(SegmentedDownloader.HasPartial(target));
        var healthy = new RangeServer(content);
        using (var http = new HttpClient(healthy))
            await SegmentedDownloader.DownloadAsync(http, Url, target, Options(connections: 2));

        CollectionAssert.AreEqual(content, File.ReadAllBytes(target));
        Assert.IsTrue(healthy.Ranges.Sum(r => r.To - r.From + 1) < content.Length);
    }

    [TestMethod]
    public async Task RestartsWhenTheFileChangedOnTheServer()
    {
        var oldContent = CreateContent(8000);
        var target = _root.GetPath("changed.bin");
        var broken = new RangeServer(oldContent) { ETag = "\"v1\"", CutAfter = (from, _) => from >= 6000 ? 500 : null };
        using (var http = new HttpClient(broken))
            await Assert.ThrowsAsync<IOException>(() =>
                SegmentedDownloader.DownloadAsync(http, Url, target, Options(maxRetries: 0)));

        var newContent = CreateContent(8000).Reverse().ToArray();
        var updated = new RangeServer(newContent) { ETag = "\"v2\"" };
        using (var http = new HttpClient(updated))
            await SegmentedDownloader.DownloadAsync(http, Url, target, Options());

        CollectionAssert.AreEqual(newContent, File.ReadAllBytes(target));
        Assert.AreEqual(newContent.Length + 1, updated.Ranges.Sum(r => r.To - r.From + 1));
    }

    [TestMethod]
    public async Task VerifiesTheExpectedHash()
    {
        var content = CreateContent(3000);
        using var http = new HttpClient(new RangeServer(content));
        var target = _root.GetPath("hash.bin");

        await SegmentedDownloader.DownloadAsync(http, Url, target,
            Options(hash: Convert.ToHexString(SHA256.HashData(content))));
        CollectionAssert.AreEqual(content, File.ReadAllBytes(target));

        var wrong = _root.GetPath("wrong.bin");
        var error = await Assert.ThrowsExactlyAsync<DownloadHashMismatchException>(() =>
            SegmentedDownloader.DownloadAsync(http, Url, wrong, Options(hash: new string('0', 40))));
        Assert.AreEqual(Convert.ToHexStringLower(SHA1.HashData(content)), error.Actual);
        Assert.IsFalse(File.Exists(wrong));
        Assert.IsFalse(SegmentedDownloader.HasPartial(wrong));
    }

    [TestMethod]
    [DataRow("d41d8cd98f00b204e9800998ecf8427e", "MD5")]
    [DataRow("da39a3ee5e6b4b0d3255bfef95601890afd80709", "SHA1")]
    [DataRow("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", "SHA256")]
    public void RecognisesHashAlgorithmsByLength(string hash, string algorithm)
    {
        Assert.AreEqual(algorithm, SegmentedDownloader.AlgorithmOf(hash).Name);
    }

    [TestMethod]
    public void RejectsUnknownHashes()
    {
        Assert.ThrowsExactly<ArgumentException>(() => SegmentedDownloader.AlgorithmOf("abc"));
        Assert.ThrowsExactly<ArgumentException>(() => SegmentedDownloader.AlgorithmOf(new string('z', 40)));
    }

    [TestMethod]
    public async Task ReportsHttpErrors()
    {
        var server = new RangeServer(CreateContent(100))
        {
            Intercept = (_, _) => new HttpResponseMessage(HttpStatusCode.Forbidden)
        };
        using var http = new HttpClient(server);

        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() =>
            SegmentedDownloader.DownloadAsync(http, Url, _root.GetPath("403.bin"), Options()));
        Assert.AreEqual(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.AreEqual(1, server.RequestCount);
    }

    private sealed class SyncProgress(Action<SegmentedDownloadProgress> report) : IProgress<SegmentedDownloadProgress>
    {
        public void Report(SegmentedDownloadProgress value) => report(value);
    }

    /// <summary>按 Range 请求返回内容的假服务器。</summary>
    private sealed class RangeServer(byte[] content) : HttpMessageHandler
    {
        private int _requests;

        public bool SupportsRanges { get; init; } = true;

        public string ETag { get; init; } = "\"etag\"";

        /// <summary>返回非 null 时直接作为响应。参数为请求的起止位置。</summary>
        public Func<long, long, HttpResponseMessage?>? Intercept { get; init; }

        /// <summary>返回非 null 时，响应体只发送这么多字节就断开（或停住）。</summary>
        public Func<long, long, int?>? CutAfter { get; init; }

        public bool StallInsteadOfCut { get; init; }

        public Action? OnStall { get; init; }

        public ConcurrentQueue<(long From, long To)> RangesQueue { get; } = new();

        public IReadOnlyList<(long From, long To)> Ranges => RangesQueue.ToList();

        public int RequestCount => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var range = request.Headers.Range?.Ranges.Single();
            long from = range?.From ?? 0, to = range?.To ?? content.Length - 1;
            if (Intercept?.Invoke(from, to) is { } intercepted)
                return Task.FromResult(intercepted);

            HttpResponseMessage response;
            if (SupportsRanges && range is not null)
            {
                if (from >= content.Length)
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));
                to = Math.Min(to, content.Length - 1);
                RangesQueue.Enqueue((from, to));
                response = new HttpResponseMessage(HttpStatusCode.PartialContent);
                response.Content = Body(content[(int)from..(int)(to + 1)], from, to);
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, content.Length);
            }
            else
            {
                response = new HttpResponseMessage(HttpStatusCode.OK) { Content = Body(content, 0, content.Length - 1) };
            }

            response.Headers.ETag = EntityTagHeaderValue.Parse(ETag);
            return Task.FromResult(response);
        }

        private HttpContent Body(byte[] bytes, long from, long to)
        {
            HttpContent body = CutAfter?.Invoke(from, to) is { } cut
                ? new StreamContent(new CutStream(bytes[..Math.Min(cut, bytes.Length)], StallInsteadOfCut, OnStall))
                : new ByteArrayContent(bytes);
            body.Headers.ContentLength = bytes.Length;
            return body;
        }
    }

    /// <summary>发送一段内容后抛出 IOException，或一直等待到被取消。</summary>
    private sealed class CutStream(byte[] head, bool stall, Action? onStall) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < head.Length)
            {
                var count = Math.Min(buffer.Length, head.Length - _position);
                head.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            if (!stall)
                throw new IOException("Connection reset.");
            onStall?.Invoke();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
