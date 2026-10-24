using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
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
    public async Task WorkItemProvider_UsesAdoDescriptorAndDefaultsToNotProbed()
    {
        var services = new ServiceCollection();
        var config = new TwigConfiguration
        {
            Organization = "testorg",
            Project = "testproj",
        };
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);
        services.AddSingleton(Substitute.For<IAuthenticationProvider>());

        using var provider = services.BuildServiceProvider();
        var adapter = provider.GetRequiredService<IWorkItemProvider>();

        adapter.Capabilities.ShouldBe(ProviderCapabilityCatalog.Ado);
        adapter.ShouldBeSameAs(provider.GetRequiredService<IAdoWorkItemService>());

        var evidence = await adapter.ProbeCapabilitiesAsync();
        evidence.ShouldBeEmpty();
        CapabilityReport.Create(adapter.Capabilities, evidence).Relationships
            .ShouldAllBe(entry => entry.Availability == CapabilityAvailability.Unknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ado")]
    public void WorkItemProvider_AdoSetting_ResolvesAdoAdapter(string? stored)
    {
        using var provider = BuildWithProvider(stored);

        provider.GetRequiredService<IWorkItemProvider>()
            .ShouldBeSameAs(provider.GetRequiredService<IAdoWorkItemService>());
    }

    [Fact]
    public void WorkItemProvider_GitHubSetting_FailsClosedInsteadOfFallingBackToAdo()
    {
        using var provider = BuildWithProvider("github");

        var ex = Should.Throw<TwigConfigurationException>(() => provider.GetRequiredService<IWorkItemProvider>());
        ex.Message.ShouldStartWith("provider-unavailable:");
    }

    [Fact]
    public void WorkItemProvider_UnknownSetting_FailsClosed()
    {
        using var provider = BuildWithProvider("jira");

        var ex = Should.Throw<TwigConfigurationException>(() => provider.GetRequiredService<IWorkItemProvider>());
        ex.Message.ShouldStartWith("provider-unknown:");
    }

    private static ServiceProvider BuildWithProvider(string? stored)
    {
        var services = new ServiceCollection();
        var config = new TwigConfiguration { Organization = "testorg", Project = "testproj", Provider = stored };
        services.AddSingleton(config);
        services.AddTwigNetworkServices(config);
        services.AddSingleton(Substitute.For<IAuthenticationProvider>());
        return services.BuildServiceProvider();
    }

    [Fact]
    public void SocketsHandler_AutomaticDecompression_IsGZipAndBrotli()
    {
        var handler = NetworkServiceModule.CreateSocketsHandler();

        handler.AutomaticDecompression.ShouldBe(
            DecompressionMethods.GZip | DecompressionMethods.Brotli);
    }

}
