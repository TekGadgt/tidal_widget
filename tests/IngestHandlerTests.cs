namespace TidalNowPlaying.Tests;

public class IngestHandlerTests
{
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
