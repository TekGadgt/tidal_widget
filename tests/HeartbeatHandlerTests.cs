namespace TidalNowPlaying.Tests;

public class HeartbeatHandlerTests
{
    [Fact]
    public void Apply_EmptyBody_RefreshesLastIngestAtAndReturns204()
    {
        var state = new State();
        var prev = new DateTime(2026, 5, 10, 11, 0, 0, DateTimeKind.Utc);
        state.Update(new TrackInfo { Title = "X" }, prev);

        var clock = new TestClock(new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc));
        var handler = new HeartbeatHandler(state, clock);

        var result = handler.Apply(bodyLength: 0);

        Assert.Equal(204, result.StatusCode);
        Assert.Equal(clock.Now, state.LastIngestAt);
        Assert.Equal("X", state.Current.Title); // current unchanged
    }
}
