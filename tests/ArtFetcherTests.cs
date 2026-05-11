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

    [Theory]
    [InlineData("http://example.com/art.jpg")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/art.jpg")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public async Task FetchAsync_NonHttpsScheme_ReturnsNullWithoutFetching(string url)
    {
        var handler = FakeHttpMessageHandler.AlwaysReturn(new byte[] { 1, 2, 3 });
        var fetcher = new ArtFetcher(handler);

        string? result = await fetcher.FetchAsync(url);

        Assert.Null(result);
        Assert.Empty(handler.Calls);
    }

    [Fact]
    public async Task FetchAsync_ResponseExceedsSizeCap_ReturnsNull()
    {
        byte[] big = new byte[6 * 1024 * 1024]; // 6 MB > 5 MB cap
        var handler = FakeHttpMessageHandler.AlwaysReturn(big);
        var fetcher = new ArtFetcher(handler);

        string? result = await fetcher.FetchAsync("https://example.com/big.jpg");

        Assert.Null(result);
    }
}
