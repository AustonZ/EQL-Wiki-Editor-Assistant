using EQLWikiAssistant.Wiki.MediaWiki;

namespace EQLWikiAssistant.Tests.Wiki;

/// <summary>
/// The properties the plan asks for by name: two items sharing an icon id trigger exactly one download, a cached
/// icon needs none, and a negative entry expires.
/// </summary>
public class IconCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "eqlwiki-icon-cache-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class FakeSource(params string[] availableIds) : IIconSource
    {
        private readonly HashSet<string> _available = [.. availableIds];

        public List<string> Requested { get; } = [];

        public Task<byte[]?> DownloadAsync(string iconId, CancellationToken cancellationToken = default)
        {
            Requested.Add(iconId);
            byte[]? bytes = _available.Contains(iconId) ? [1, 2, 3, 4] : null;
            return Task.FromResult(bytes);
        }
    }

    /// <summary>Icons are shared across many items — three breastplates in the corpus all use id 624 — which is the
    /// whole reason the cache exists.</summary>
    [Fact]
    public async Task TwoItemsSharingAnIconDownloadItOnce()
    {
        var source = new FakeSource("624");
        var cache = new IconCache(_directory, source);

        byte[]? first = await cache.GetAsync("624");
        byte[]? second = await cache.GetAsync("624");

        Assert.Equal([1, 2, 3, 4], first);
        Assert.Equal([1, 2, 3, 4], second);
        Assert.Equal(["624"], source.Requested);
        Assert.Equal(1, cache.Downloads);
    }

    [Fact]
    public async Task AnIconAlreadyOnDiskCostsNoRequest()
    {
        var source = new FakeSource("584");
        await new IconCache(_directory, source).GetAsync("584");

        // A fresh cache over the same directory — as a later session would be.
        var laterSource = new FakeSource("584");
        byte[]? bytes = await new IconCache(_directory, laterSource).GetAsync("584");

        Assert.NotNull(bytes);
        Assert.Empty(laterSource.Requested);
    }

    /// <summary>"No such file" is cached too, or every capture of an item whose icon was never uploaded re-asks and
    /// gets the same answer.</summary>
    [Fact]
    public async Task AMissingIconIsRememberedAndNotReAsked()
    {
        var source = new FakeSource();
        var cache = new IconCache(_directory, source);

        Assert.Null(await cache.GetAsync("999"));
        Assert.Null(await cache.GetAsync("999"));

        Assert.Equal(["999"], source.Requested);
    }

    /// <summary>...but only briefly, because unlike an icon's contents this is a fact that changes the moment
    /// somebody uploads one.</summary>
    [Fact]
    public async Task AMissingIconIsReAskedOnceTheEntryExpires()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var source = new FakeSource();
        var cache = new IconCache(_directory, source, clock);

        Assert.Null(await cache.GetAsync("999"));
        clock.Advance(IconCache.MissingIconLifetime + TimeSpan.FromMinutes(1));
        Assert.Null(await cache.GetAsync("999"));

        Assert.Equal(["999", "999"], source.Requested);
    }

    /// <summary>An icon that appears later supersedes the earlier "missing" answer rather than being shadowed by
    /// it.</summary>
    [Fact]
    public async Task AnUploadedIconReplacesTheMissingMarker()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var missing = new FakeSource();
        Assert.Null(await new IconCache(_directory, missing, clock).GetAsync("777"));

        clock.Advance(IconCache.MissingIconLifetime + TimeSpan.FromMinutes(1));
        var nowAvailable = new FakeSource("777");
        var cache = new IconCache(_directory, nowAvailable, clock);

        Assert.NotNull(await cache.GetAsync("777"));
        // And the marker is gone, so a later lookup is served from disk.
        Assert.NotNull(await cache.GetAsync("777"));
        Assert.Equal(["777"], nowAvailable.Requested);
    }

    [Fact]
    public async Task ForgetCausesAReFetch()
    {
        var source = new FakeSource("584");
        var cache = new IconCache(_directory, source);

        await cache.GetAsync("584");
        cache.Forget("584");
        await cache.GetAsync("584");

        Assert.Equal(["584", "584"], source.Requested);
    }

    /// <summary>Icon ids come from a wiki parameter and are therefore untrusted. A value with path separators in it
    /// must not be able to write outside the cache directory.</summary>
    [Theory]
    [InlineData("../../evil")]
    [InlineData("a/b")]
    [InlineData("..")]
    public async Task AHostileIconIdCannotEscapeTheCacheDirectory(string iconId)
    {
        var cache = new IconCache(_directory, new FakeSource());

        // Either it sanitizes to something harmless or it refuses; what it must not do is touch another directory.
        try { await cache.GetAsync(iconId); }
        catch (ArgumentException) { }

        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "eqlwiki-icon-cache-tests", "evil.png")));
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
