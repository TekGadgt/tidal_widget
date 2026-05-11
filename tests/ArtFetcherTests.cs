using System.Text;

namespace TidalNowPlaying.Tests;

public class ArtFetcherTests
{
    [Fact]
    public async Task FetchAsync_ValidHttpsUrl_ReturnsBase64DataUrl()
    {
        byte[] body = Encoding.UTF8.GetBytes("fake-image-bytes");
        var handler = FakeHttpMessageHandler.AlwaysReturn(body);
        var fetcher = new ArtFetcher(handler);

        string? result = await fetcher.FetchAsync("https://example.com/art.jpg");

        Assert.NotNull(result);
        Assert.StartsWith("data:image/jpeg;base64,", result);
        string expectedBase64 = Convert.ToBase64String(body);
        Assert.Equal($"data:image/jpeg;base64,{expectedBase64}", result);
    }

    [Theory]
    [InlineData("http://example.com/art.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/art.jpg")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task FetchAsync_NonHttpsScheme_ReturnsNullWithoutFetching(string url)
    {
        var handler = FakeHttpMessageHandler.AlwaysReturn(new byte[] { 1, 2, 3 });
        var fetcher = new ArtFetcher(handler);

        string? result = await fetcher.FetchAsync(url);

        Assert.Null(result);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task FetchAsync_ResponseExceedsSizeCap_ReturnsNull()
    {
        byte[] big = new byte[6 * 1024 * 1024]; // 6 MB > 5 MB cap
        var handler = FakeHttpMessageHandler.AlwaysReturn(big);
        var fetcher = new ArtFetcher(handler);

        string? result = await fetcher.FetchAsync("https://example.com/big.jpg");

        Assert.Null(result);
    }

    [Fact]
    public async Task FetchAsync_RequestExceedsTimeout_ReturnsNull()
    {
        var handler = FakeHttpMessageHandler.Slow(TimeSpan.FromSeconds(10));
        // single zero-delay retry slot so AttemptOnce actually runs once and engages the timeout
        var fetcher = new ArtFetcher(handler, timeout: TimeSpan.FromMilliseconds(200), retryDelays: new[] { TimeSpan.Zero });

        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? result = await fetcher.FetchAsync("https://example.com/slow.jpg");
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Took {sw.Elapsed} — timeout did not trip.");
    }

    [Fact]
    public async Task FetchAsync_FailsTwiceThenSucceeds_ReturnsResult()
    {
        byte[] body = new byte[] { 0xFF, 0xD8, 0xFF };
        var handler = FakeHttpMessageHandler.FailThenSucceed(failTimes: 2, body: body);
        // tight retry delays so test finishes quickly
        var fetcher = new ArtFetcher(handler,
            timeout: TimeSpan.FromSeconds(5),
            retryDelays: new[] { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1) });

        string? result = await fetcher.FetchAsync("https://example.com/flaky.jpg");

        Assert.NotNull(result);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task FetchAsync_AllAttemptsFail_ReturnsNull()
    {
        var handler = FakeHttpMessageHandler.AlwaysFail();
        var fetcher = new ArtFetcher(handler,
            timeout: TimeSpan.FromSeconds(5),
            retryDelays: new[] { TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1) });

        string? result = await fetcher.FetchAsync("https://example.com/dead.jpg");

        Assert.Null(result);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task FetchAsync_SameUrlTwice_OnlyFetchesOnce()
    {
        byte[] body = new byte[] { 1, 2, 3 };
        var handler = FakeHttpMessageHandler.AlwaysReturn(body);
        var fetcher = new ArtFetcher(handler, retryDelays: new[] { TimeSpan.Zero });

        string? first  = await fetcher.FetchAsync("https://example.com/art.jpg");
        string? second = await fetcher.FetchAsync("https://example.com/art.jpg");

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task FetchAsync_DifferentUrl_FetchesAgain()
    {
        byte[] body = new byte[] { 1, 2, 3 };
        var handler = FakeHttpMessageHandler.AlwaysReturn(body);
        var fetcher = new ArtFetcher(handler, retryDelays: new[] { TimeSpan.Zero });

        await fetcher.FetchAsync("https://example.com/a.jpg");
        await fetcher.FetchAsync("https://example.com/b.jpg");

        Assert.Equal(2, handler.Calls.Count);
    }

    [Fact]
    public async Task FetchAsync_NewUrlWhileOldInFlight_CancelsOldAndProceeds()
    {
        // Slow handler simulates an in-flight fetch. The second FetchAsync call
        // cancels the first via the in-flight CTS, then proceeds to completion
        // with its own URL. Both tasks are awaited so nothing leaks.
        var slowHandler = FakeHttpMessageHandler.Slow(TimeSpan.FromMilliseconds(500));
        var fetcher = new ArtFetcher(slowHandler,
            timeout: TimeSpan.FromSeconds(5),
            retryDelays: new[] { TimeSpan.Zero });

        var firstTask = fetcher.FetchAsync("https://example.com/slow-a.jpg");
        await Task.Delay(50); // let first call start
        var secondTask = fetcher.FetchAsync("https://example.com/slow-b.jpg");

        string? firstResult = await firstTask;
        string? secondResult = await secondTask;

        Assert.Null(firstResult);     // first cancelled by second
        Assert.NotNull(secondResult); // second proceeded to completion
    }
}
