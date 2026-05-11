using System.Net;

namespace TidalNowPlaying.Tests;

public class FakeHttpMessageHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Calls { get; } = new();

    private readonly Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, int, Task<HttpResponseMessage>> respond)
    {
        this.respond = respond;
    }

    public static FakeHttpMessageHandler AlwaysReturn(byte[] body, string mediaType = "image/jpeg")
        => new((req, _) =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
            return Task.FromResult(resp);
        });

    public static FakeHttpMessageHandler AlwaysFail()
        => new((_, _) => throw new HttpRequestException("simulated failure"));

    public static FakeHttpMessageHandler FailThenSucceed(int failTimes, byte[] body)
        => new((req, callIndex) =>
        {
            if (callIndex < failTimes) throw new HttpRequestException("simulated failure");
            var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
            return Task.FromResult(resp);
        });

    public static FakeHttpMessageHandler Slow(TimeSpan delay)
        => new(async (req, _) =>
        {
            await Task.Delay(delay);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
        });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        int idx = Calls.Count;
        Calls.Add(request);
        var responseTask = respond(request, idx);
        var cancelTask = Task.Delay(Timeout.Infinite, cancellationToken);
        var completed = await Task.WhenAny(responseTask, cancelTask);
        if (completed == cancelTask)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }
        return await responseTask;
    }
}
