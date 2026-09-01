using Jarvis.Infrastructure.Voice.Local.Llama;

namespace Jarvis.Infrastructure.Tests.Voice;

public sealed class LoopbackEndpointTests
{
    [Fact]
    public void CreatesOnlyTheExpectedHttpLoopbackEndpoint()
    {
        Uri endpoint = LoopbackEndpoint.Create("127.0.0.1", 18080);

        Assert.Equal("http://127.0.0.1:18080/", endpoint.AbsoluteUri);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("::1")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    public void RejectsNonExactLoopbackHosts(string host) =>
        Assert.Throws<InvalidOperationException>(() => LoopbackEndpoint.Create(host, 18080));

    [Theory]
    [InlineData("https://127.0.0.1:18080/")]
    [InlineData("http://localhost:18080/")]
    [InlineData("http://127.0.0.1:18080/path")]
    [InlineData("http://127.0.0.1:18080/?redirect=https://example.com")]
    [InlineData("http://user@127.0.0.1:18080/")]
    public void HttpFactoryRejectsEndpointVariantsThatCouldEscapeTheBoundary(string endpoint)
    {
        LoopbackHttpClientFactory factory = new();

        Assert.Throws<InvalidOperationException>(() =>
            factory.Create(new Uri(endpoint), authenticationToken: null));
    }
}
