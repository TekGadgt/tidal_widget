namespace TidalNowPlaying.Tests;

public class IdleTimerTests
{
    [Fact]
    public void Tick_ElapsedExceedsTimeout_ClearsCurrent()
    {
        var state = new State();
        var t0 = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        state.Update(new TrackInfo { Title = "X", IsPlaying = true }, t0);

        var clock = new TestClock(t0);
        var timer = new IdleTimer(state, clock, TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(11));
        timer.Tick();

        Assert.Equal("", state.Current.Title);
        Assert.False(state.Current.IsPlaying);
    }

    [Fact]
    public void Tick_ElapsedWithinTimeout_LeavesCurrentAlone()
    {
        var state = new State();
        var t0 = new DateTime(2026, 5, 10, 12, 0, 0, DateTimeKind.Utc);
        state.Update(new TrackInfo { Title = "X", IsPlaying = true }, t0);

        var clock = new TestClock(t0);
        var timer = new IdleTimer(state, clock, TimeSpan.FromSeconds(10));

        clock.Advance(TimeSpan.FromSeconds(5));
        timer.Tick();

        Assert.Equal("X", state.Current.Title);
    }
}
