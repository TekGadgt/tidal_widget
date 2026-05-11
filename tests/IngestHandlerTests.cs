namespace TidalNowPlaying.Tests;

public class IngestHandlerTests
{
    [Fact]
    public async Task ApplyAsync_BodyExceeds16KB_Returns413AndDoesNotTouchState()
    {
        var state = new State();
        var initialTime = new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc);
        state.Update(new TrackInfo { Title = "Untouched" }, initialTime);

        var fetcher = new ArtFetcher(FakeHttpMessageHandler.AlwaysFail(),
                                     retryDelays: new[] { TimeSpan.Zero });
        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new IngestHandler(state, fetcher, clock);

        string huge = new string('x', 17 * 1024); // 17 KB > 16 KB cap

        var result = await handler.ApplyAsync(huge);

        Assert.Equal(413, result.StatusCode);
        Assert.Equal("Untouched", state.Current.Title);
        Assert.Equal(initialTime, state.LastIngestAt);
    }

    [Fact]
    public async Task ApplyAsync_MalformedJson_ClearsStateAndReturns400()
    {
        var state = new State();
        // Pre-populate state with a real track
        state.Update(new TrackInfo { Title = "Old", Artist = "X", IsPlaying = true, Art = "data:..." },
                     new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc));

        var fetcher = new ArtFetcher(FakeHttpMessageHandler.AlwaysFail(),
                                     retryDelays: new[] { TimeSpan.Zero });
        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new IngestHandler(state, fetcher, clock);

        var result = await handler.ApplyAsync("{not valid json");

        Assert.Equal(400, result.StatusCode);
        Assert.Equal("", state.Current.Title);
        Assert.Equal("", state.Current.Artist);
        Assert.False(state.Current.IsPlaying);
        Assert.Equal(clock.Now, state.LastIngestAt);
    }

    [Fact]
    public async Task ApplyAsync_ValidBody_UpdatesStateAndRefreshesLastIngestAt()
    {
        var state = new State();
        var fetcher = new ArtFetcher(FakeHttpMessageHandler.AlwaysReturn(new byte[] { 1 }),
                                     retryDelays: new[] { TimeSpan.Zero });
        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new IngestHandler(state, fetcher, clock);

        string body = """
            {"title":"Spaceman","artist":"Hardwell","album":"Spaceman",
             "is_playing":true,"art_url":""}
            """;

        var result = await handler.ApplyAsync(body);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("Spaceman", state.Current.Title);
        Assert.Equal("Hardwell", state.Current.Artist);
        Assert.True(state.Current.IsPlaying);
        Assert.Equal(clock.Now, state.LastIngestAt);
    }
}
