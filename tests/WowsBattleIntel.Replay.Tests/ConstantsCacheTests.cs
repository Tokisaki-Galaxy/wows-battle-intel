using System.Net;

namespace WowsBattleIntel.Replay.Tests;

/// <summary>
/// ConstantsCache 的解析、落盘、修剪与失败分类测试。
/// </summary>
public sealed class ConstantsCacheTests : IDisposable
{
    private readonly string cacheDir;

    public ConstantsCacheTests()
    {
        cacheDir = Path.Combine(
            Path.GetTempPath(),
            $"wbi-constants-test-{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        if (Directory.Exists(cacheDir))
        {
            Directory.Delete(cacheDir, recursive: true);
        }
    }

    private static string ConstantsBody =>
        """{"INIT_ECONOMICS_INDICES": {"credits": 0}}""";

    private static string ListingJson(string[] names) =>
        System.Text.Json.JsonSerializer.Serialize(new { files = names.Select(n => new { name = n }) });

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    [Fact]
    public async Task ResolveAsync_DownloadsAndCaches()
    {
        uint requested = 0;
        var cache = new ConstantsCache(cacheDir, handler: new StubHandler(req =>
        {
            var file = req.RequestUri!.AbsolutePath.Split('/').Last();
            requested = uint.Parse(Path.GetFileNameWithoutExtension(file));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ConstantsBody),
            };
        }));

        var path = await cache.ResolveAsync(13357625);

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(ConstantsBody, File.ReadAllText(path));
        Assert.Equal(13357625u, requested);
        Assert.Null(cache.LastError);
    }

    [Fact]
    public async Task ResolveAsync_SecondCallHitsCacheWithoutNetwork()
    {
        int hits = 0;
        var cache = new ConstantsCache(cacheDir, handler: new StubHandler(_ =>
        {
            hits++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ConstantsBody),
            };
        }));

        var first = await cache.ResolveAsync(13357625);
        var second = await cache.ResolveAsync(13357625);

        Assert.Equal(first, second);
        Assert.Equal(1, hits);
    }

    [Fact]
    public async Task ResolveAsync_BuildNotPublished_ReportsSourceLag()
    {
        var cache = new ConstantsCache(cacheDir, handler: new StubHandler(req =>
        {
            var body = req.RequestUri!.AbsolutePath.Contains("data.jsdelivr.com")
                ? ListingJson(["data/versions/1.json"])
                : ConstantsBody;
            return new HttpResponseMessage(
                req.RequestUri!.AbsolutePath.EndsWith(".json") && !req.RequestUri.Host.Contains("data.jsdelivr")
                    ? HttpStatusCode.NotFound
                    : HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            };
        }));

        var path = await cache.ResolveAsync(99999999);

        Assert.Null(path);
        Assert.Contains("尚未收录", cache.LastError);
    }

    [Fact]
    public async Task ResolveAsync_MirrorUnavailable_ReportsTransport()
    {
        var cache = new ConstantsCache(cacheDir, handler: new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var path = await cache.ResolveAsync(13357625);

        Assert.Null(path);
        Assert.Contains("HTTP 503", cache.LastError);
    }

    [Fact]
    public async Task ResolveAsync_NonJsonBody_Rejected()
    {
        var cache = new ConstantsCache(cacheDir, handler: new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html>error page</html>"),
            }));

        var path = await cache.ResolveAsync(13357625);

        Assert.Null(path);
        Assert.False(File.Exists(cache.CachedPath(13357625)));
        Assert.Contains("非 JSON", cache.LastError);
    }

    [Fact]
    public async Task Prune_KeepsNewestTwentyVersions()
    {
        var cache = new ConstantsCache(cacheDir, handler: new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ConstantsBody),
            }));

        // 预置 21 个旧版本文件，写入时间递增，最老的一个应被裁掉。
        Directory.CreateDirectory(cacheDir);
        for (uint i = 1; i <= ConstantsCache.MaxCachedVersions + 1; i++)
        {
            var path = cache.CachedPath(i);
            await File.WriteAllTextAsync(path, ConstantsBody);
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-i));
        }

        // 下载一个新版本触发修剪。
        await cache.ResolveAsync(99999999);

        Assert.False(File.Exists(cache.CachedPath(ConstantsCache.MaxCachedVersions + 1)));
        Assert.True(File.Exists(cache.CachedPath(1)));
        Assert.True(File.Exists(cache.CachedPath(99999999)));
        Assert.Equal(
            ConstantsCache.MaxCachedVersions,
            Directory.EnumerateFiles(cacheDir, "*.json").Count());
    }

    [Fact]
    public async Task Prune_IgnoresFilesWithNonNumericNames()
    {
        var stray = Path.Combine(cacheDir, "readme.json");
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(stray, "{}");

        var cache = new ConstantsCache(cacheDir, handler: new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        await cache.ResolveAsync(1);

        // 非版本命名的文件不属于缓存管理范围，不删。
        Assert.True(File.Exists(stray));
    }
}
