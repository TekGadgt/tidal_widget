using System.Text;

namespace TidalNowPlaying.Tests;

public class ArtFetcherTests
{
    [Fact]
    public async Task FetchAsync_ValidHttpsUrl_ReturnsBase64DataUrl()
    {
        byte[] body = Encoding.UTF8.GetBytes("fake-image-bytes");
        var handler = FakeHttpMessageHandler.AlwaysReturn(body);
        var fetcher = new ArtFetcher(handler);

        string? result = await fetcher.FetchAsync("https://example.com/art.jpg");

        Assert.NotNull(result);
        Assert.StartsWith("data:image/jpeg;base64,", result);
        string expectedBase64 = Convert.ToBase64String(body);
        Assert.Equal($"data:image/jpeg;base64,{expectedBase64}", result);
    }
}
