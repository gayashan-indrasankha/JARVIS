using System.Net.Http.Headers;

namespace Jarvis.Infrastructure.Voice.Local.Llama;

internal static class LoopbackEndpoint
{
    public static Uri Create(string host, int port)
    {
        if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Local inference must bind to 127.0.0.1.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65_535);
        return new Uri($"http://127.0.0.1:{port}/", UriKind.Absolute);
    }
}

internal interface ILoopbackHttpClientFactory
{
    public HttpClient Create(Uri endpoint, string? authenticationToken);
}

internal sealed class LoopbackHttpClientFactory : ILoopbackHttpClientFactory
{
    public HttpClient Create(Uri endpoint, string? authenticationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (endpoint.Scheme != Uri.UriSchemeHttp ||
            !string.Equals(endpoint.Host, "127.0.0.1", StringComparison.Ordinal) ||
            !string.Equals(endpoint.AbsolutePath, "/", StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            !string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException("The local inference endpoint is not safe loopback HTTP.");
        }

        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseCookies = false,
            UseProxy = false,
        };
        HttpClient client = new(handler)
        {
            BaseAddress = endpoint,
            Timeout = Timeout.InfiniteTimeSpan,
        };
        if (!string.IsNullOrEmpty(authenticationToken))
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", authenticationToken);
        }

        return client;
    }
}
