namespace TidalNowPlaying;

public record TrackInfo
{
    public string Title     { get; init; } = "";
    public string Artist    { get; init; } = "";
    public string Album     { get; init; } = "";
    public bool   IsPlaying { get; init; } = false;
    public string Art       { get; init; } = "";
}
