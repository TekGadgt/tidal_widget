namespace TidalNowPlaying;

public class ArtFetcher
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    private readonly TimeSpan[] retryDelays;

    public ArtFetcher(
        HttpMessageHandler? handler = null,
        TimeSpan? timeout = null,
        TimeSpan[]? retryDelays = null)
    {
        http = new HttpClient(handler ?? new HttpClientHandler());
        this.timeout = timeout ?? TimeSpan.FromSeconds(5);
        this.retryDelays = retryDelays ?? new[]
        {
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(3),
        };
    }

    public async Task<string?> FetchAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            return null;

        foreach (var delay in retryDelays)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay);
            var result = await AttemptOnceAsync(url);
            if (result != null) return result;
        }
        return null;
    }

    private async Task<string?> AttemptOnceAsync(string url)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            using var buf = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                if (buf.Length + read > MaxBytes) return null;
                buf.Write(buffer, 0, read);
            }
            return "data:image/jpeg;base64," + Convert.ToBase64String(buf.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
