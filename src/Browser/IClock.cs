namespace TidalNowPlaying;

public interface IClock
{
    DateTime UtcNow();
}

public class SystemClock : IClock
{
    public DateTime UtcNow() => DateTime.UtcNow;
}
