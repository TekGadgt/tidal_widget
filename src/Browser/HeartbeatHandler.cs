namespace TidalNowPlaying;

public record HeartbeatResult(int StatusCode);

public class HeartbeatHandler
{
    private readonly State state;
    private readonly IClock clock;

    public HeartbeatHandler(State state, IClock clock)
    {
        this.state = state;
        this.clock = clock;
    }

    public HeartbeatResult Apply(int bodyLength)
    {
        if (bodyLength != 0) return new HeartbeatResult(400);
        state.TouchHeartbeat(clock.UtcNow());
        return new HeartbeatResult(204);
    }
}
