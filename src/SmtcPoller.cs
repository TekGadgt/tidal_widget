using Windows.Media.Control;
using Windows.Storage.Streams;

namespace TidalNowPlaying;

public class SmtcPoller
{
    private readonly int    pollMs;
    private readonly string appFilter;
    private readonly bool   filterMode;
    private readonly Action<TrackInfo> onChange;
    private TrackInfo current = new();
    private readonly object trackLock = new();

    public SmtcPoller(int pollMs, string appFilter, bool filterMode, Action<TrackInfo> onChange)
    {
        this.pollMs     = pollMs;
        this.appFilter  = appFilter;
        this.filterMode = filterMode;
        this.onChange   = onChange;
    }

    public TrackInfo Current
    {
        get { lock (trackLock) { return current; } }
    }

    public async Task RunAsync()
    {
        while (true)
        {
            try
            {
                var info = await GetMediaInfo();
                lock (trackLock)
                {
                    if (info.Title != current.Title || info.Artist != current.Artist || info.IsPlaying != current.IsPlaying)
                    {
                        current = info;
                        onChange(info);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Poll error: {ex.Message}");
            }
            await Task.Delay(pollMs);
        }
    }

    private async Task<TrackInfo> GetMediaInfo()
    {
        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

        GlobalSystemMediaTransportControlsSession? session = null;

        if (filterMode)
        {
            foreach (var s in manager.GetSessions())
            {
                string id = s.SourceAppUserModelId ?? "";
                if (id.Contains(appFilter, StringComparison.OrdinalIgnoreCase))
                {
                    session = s;
                    break;
                }
            }
        }
        else
        {
            session = manager.GetCurrentSession();
        }

        if (session == null) return new();

        var props = await session.TryGetMediaPropertiesAsync();
        if (props == null) return new();

        var pb = session.GetPlaybackInfo();
        bool playing = pb?.PlaybackStatus ==
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

        string art = "";
        try
        {
            var thumb = props.Thumbnail;
            if (thumb != null)
            {
                using var stream = await thumb.OpenReadAsync();
                var reader = new DataReader(stream);
                await reader.LoadAsync((uint)stream.Size);
                byte[] bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
                art = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
            }
        }
        catch { }

        return new TrackInfo
        {
            Title     = props.Title      ?? "",
            Artist    = props.Artist     ?? "",
            Album     = props.AlbumTitle ?? "",
            IsPlaying = playing,
            Art       = art,
        };
    }
}
