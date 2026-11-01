using System.Net;

namespace Twig.Infrastructure.DependencyInjection;

internal static class NativeHttpClient
{
    internal static HttpClient Create()
        => new(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Brotli,
        })
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };
}
