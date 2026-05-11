namespace TidalNowPlaying;

public class IdleTimer
{
    private readonly State state;
    private readonly IClock clock;
    private readonly TimeSpan timeout;

    public IdleTimer(State state, IClock clock, TimeSpan timeout)
    {
        this.state   = state;
        this.clock   = clock;
        this.timeout = timeout;
    }

    public void Tick()
    {
        if (clock.UtcNow() - state.LastIngestAt > timeout)
        {
            state.Clear();
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct); }
            catch (OperationCanceledException) { return; }
            Tick();
        }
    }
}
