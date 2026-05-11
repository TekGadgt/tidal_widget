namespace TidalNowPlaying.Tests;

public class TestClock : TidalNowPlaying.IClock
{
    public DateTime Now { get; set; }
    public TestClock(DateTime initial) { Now = initial; }
    public DateTime UtcNow() => Now;
    public void Advance(TimeSpan d) => Now = Now.Add(d);
}
