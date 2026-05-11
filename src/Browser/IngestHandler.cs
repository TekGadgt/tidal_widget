using System.Text.Json;

namespace TidalNowPlaying;

public record IngestResult(int StatusCode);

public class IngestHandler
{
    private const int MaxBodyBytes = 16 * 1024;

    private readonly State state;
    private readonly ArtFetcher fetcher;
    private readonly IClock clock;
    private string lastSeenArtUrl = "";

    public IngestHandler(State state, ArtFetcher fetcher, IClock clock)
    {
        this.state   = state;
        this.fetcher = fetcher;
        this.clock   = clock;
    }

    public async Task<IngestResult> ApplyAsync(string body)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(body) > MaxBodyBytes)
            return new IngestResult(413);

        IngestPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<IngestPayload>(body,
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
        }
        catch
        {
            state.Clear();
            state.TouchHeartbeat(clock.UtcNow());
            return new IngestResult(400);
        }
        if (payload is null)
        {
            state.Clear();
            state.TouchHeartbeat(clock.UtcNow());
            return new IngestResult(400);
        }

        string newArtUrl = payload.ArtUrl ?? "";
        bool   artChanged = newArtUrl != lastSeenArtUrl;

        var info = new TrackInfo
        {
            Title     = payload.Title  ?? "",
            Artist    = payload.Artist ?? "",
            Album     = payload.Album  ?? "",
            IsPlaying = payload.IsPlaying,
            Art       = artChanged ? "" : state.Current.Art,
        };
        state.Update(info, clock.UtcNow());

        if (artChanged)
        {
            lastSeenArtUrl = newArtUrl;
            if (!string.IsNullOrEmpty(newArtUrl))
            {
                _ = Task.Run(async () =>
                {
                    string? art = await fetcher.FetchAsync(newArtUrl);
                    if (art != null) state.SetArt(art);
                });
            }
        }

        return new IngestResult(200);
    }

    private class IngestPayload
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public bool IsPlaying { get; set; }
        public string? ArtUrl { get; set; }
    }
}
