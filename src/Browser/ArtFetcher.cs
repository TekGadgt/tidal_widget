namespace TidalNowPlaying;

public class ArtFetcher
{
    private readonly HttpClient http;

    public ArtFetcher(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler());
    }

    public async Task<string?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return null;

        using var resp = await http.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return null;
        byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
        return "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
    }
}
