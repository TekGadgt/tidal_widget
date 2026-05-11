namespace TidalNowPlaying;

public class ArtFetcher
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly HttpClient http;

    public ArtFetcher(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler());
    }

    public async Task<string?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return null;

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        if (!resp.IsSuccessStatusCode) return null;

        using var stream = await resp.Content.ReadAsStreamAsync();
        using var buf = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            if (buf.Length + read > MaxBytes) return null;
            buf.Write(buffer, 0, read);
        }
        return "data:image/jpeg;base64," + Convert.ToBase64String(buf.ToArray());
    }
}
