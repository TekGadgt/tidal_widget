namespace TidalNowPlaying;

public class ArtFetcher
{
    private const long MaxBytes = 5 * 1024 * 1024;

    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    private readonly TimeSpan[] retryDelays;

    private readonly object gate = new();
    private string? cachedUrl;
    private string? cachedResult;
    private CancellationTokenSource? inFlight;

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

        CancellationTokenSource? oldInFlight = null;
        CancellationToken outerToken;
        lock (gate)
        {
            if (url == cachedUrl && cachedResult != null) return cachedResult;
            oldInFlight = inFlight;
            inFlight = new CancellationTokenSource();
            outerToken = inFlight.Token;
        }
        if (oldInFlight != null)
        {
            try { oldInFlight.Cancel(); } catch { }
            oldInFlight.Dispose();
        }

        foreach (var delay in retryDelays)
        {
            if (outerToken.IsCancellationRequested) return null;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, outerToken); }
                catch (OperationCanceledException) { return null; }
            }

            var result = await AttemptOnceAsync(url, outerToken);
            if (outerToken.IsCancellationRequested) return null;
            if (result != null)
            {
                lock (gate)
                {
                    cachedUrl    = url;
                    cachedResult = result;
                }
                return result;
            }
        }
        return null;
    }

    private async Task<string?> AttemptOnceAsync(string url, CancellationToken outerToken)
    {
        using var perAttempt = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        perAttempt.CancelAfter(timeout);
        var token = perAttempt.Token;

        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (!resp.IsSuccessStatusCode) return null;

            // Derive media type from response; default to image/jpeg if missing or non-image.
            string mediaType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                mediaType = "image/jpeg";

            using var stream = await resp.Content.ReadAsStreamAsync(token);
            using var buf = new MemoryStream();
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer, token)) > 0)
            {
                if (buf.Length + read > MaxBytes) return null;
                buf.Write(buffer, 0, read);
            }
            return $"data:{mediaType};base64,{Convert.ToBase64String(buf.ToArray())}";
        }
        catch
        {
            return null;
        }
    }
}
