using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Xunit;

namespace Twig.Infrastructure.Tests.DependencyInjection;

public sealed class NetworkServiceModuleTests
{
    private static HttpClient ResolveHttpClient()
    {
        var services = new ServiceCollection();
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
        };
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<HttpClient>();
    }

    [Fact]
    public void HttpClient_Http2Settings_AreConfigured()
    {
        var client = ResolveHttpClient();

        client.DefaultRequestVersion.ShouldBe(HttpVersion.Version20);
        client.DefaultVersionPolicy.ShouldBe(HttpVersionPolicy.RequestVersionOrLower);
    }

    [Fact]
    public void HttpClient_IsSingleton()
    {
        var services = new ServiceCollection();
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
        };
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);

        var provider = services.BuildServiceProvider();
        var client1 = provider.GetRequiredService<HttpClient>();
        var client2 = provider.GetRequiredService<HttpClient>();

        client1.ShouldBeSameAs(client2);
    }

    [Fact]
    public void SocketsHandler_AutomaticDecompression_IsGZipAndBrotli()
    {
        var handler = NetworkServiceModule.CreateSocketsHandler();

        handler.AutomaticDecompression.ShouldBe(
            DecompressionMethods.GZip | DecompressionMethods.Brotli);
    }

    [Fact]
    public void AddTwigNetworkServices_ResolvesRevisionBoundAdoService_AsSameInstanceAsAdoWorkItemService()
    {
        // ISP contract: IRevisionBoundAdoWorkItemService MUST resolve to the same singleton
        // as IAdoWorkItemService — segregating the strict-CAS surface must not spin a second
        // AdoRestClient with its own HttpClient / throttle state.
        var services = new ServiceCollection();
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
        };
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);

        var provider = services.BuildServiceProvider();
        var ado = provider.GetRequiredService<IAdoWorkItemService>();
        var revisionBound = provider.GetRequiredService<IRevisionBoundAdoWorkItemService>();

        revisionBound.ShouldBeSameAs(ado);
    }
}
