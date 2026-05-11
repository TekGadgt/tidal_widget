namespace TidalNowPlaying;

public class State
{
    private readonly object gate = new();
    private TrackInfo current = new();
    private DateTime lastIngestAt = DateTime.MinValue;

    public TrackInfo Current
    {
        get { lock (gate) { return current; } }
    }

    public DateTime LastIngestAt
    {
        get { lock (gate) { return lastIngestAt; } }
    }

    public void Update(TrackInfo info, DateTime now)
    {
        lock (gate)
        {
            current = info;
            lastIngestAt = now;
        }
    }

    public void TouchHeartbeat(DateTime now)
    {
        lock (gate) { lastIngestAt = now; }
    }

    public void Clear()
    {
        lock (gate) { current = new TrackInfo(); }
    }

    public void ClearArt()
    {
        lock (gate) { current = current with { Art = "" }; }
    }

    public void SetArt(string art)
    {
        lock (gate) { current = current with { Art = art }; }
    }
}
