using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Epic #1168 x #1167: the registered mirror factory takes its ADO resource scope from the
/// registered <see cref="IWorkItemProvider"/>, lazily and only when the neutral store is opted in.
/// </summary>
public sealed class MirrorScopeBindingTests : IDisposable
{
    private const string Scope = "11111111-2222-3333-4444-555555555555";

    private readonly SqliteCacheStore _store = new("Data Source=:memory:");

    public void Dispose() => _store.Dispose();

    private sealed class FakeProvider(string? scope) : IWorkItemProvider
    {
        public int Calls { get; private set; }
        public ProviderCapabilityDescriptor Capabilities => throw new NotSupportedException();

        public Task<string?> ResolveResourceScopeAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(scope);
        }
    }

    private IServiceProvider Services(FakeProvider? provider)
    {
        var services = new ServiceCollection().AddSingleton(_store);
        if (provider is not null)
            services.AddSingleton<IWorkItemProvider>(provider);
        return services.BuildServiceProvider();
    }

    private long CountIdentities()
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM pending.external_identities";
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task OptedIn_ProjectsUnderTheRegisteredProvidersScope_ResolvedOnce()
    {
        var provider = new FakeProvider(Scope);
        var mirror = TwigServiceRegistration.CreateMirrorRepository(Services(provider), neutralStoreOptedIn: true);

        provider.Calls.ShouldBe(0);
        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());
        await mirror.SaveBatchAsync([new WorkItemBuilder(43, "Other").Build()]);

        provider.Calls.ShouldBe(1);
        CountIdentities().ShouldBe(2);
        (await new SqliteLocalWorkItemRepository(_store).FindAsync(new ExternalWorkItemIdentity("ado", Scope, "42"))).ShouldNotBeNull();
    }

    [Fact]
    public async Task NotOptedIn_NeverAsksTheProvider_AndProjectsNothing()
    {
        var provider = new FakeProvider(Scope);
        var mirror = TwigServiceRegistration.CreateMirrorRepository(Services(provider), neutralStoreOptedIn: false);

        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());

        provider.Calls.ShouldBe(0);
        (await mirror.GetByIdAsync(42)).ShouldNotBeNull();
        CountIdentities().ShouldBe(0);
    }

    [Fact]
    public async Task ProviderWithoutAScope_RefusesProjection_ButTheMirrorIsWritten()
    {
        var mirror = TwigServiceRegistration.CreateMirrorRepository(Services(new FakeProvider(null)), neutralStoreOptedIn: true);

        await mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build());

        (await mirror.GetByIdAsync(42)).ShouldNotBeNull();
        CountIdentities().ShouldBe(0);
    }

    [Fact]
    public async Task UnusableConfiguredProvider_Surfaces_NothingIsWrittenOrProjected()
    {
        var services = new ServiceCollection().AddSingleton(_store)
            .AddSingleton<IWorkItemProvider>(_ => throw new TwigConfigurationException(
                "provider-unavailable: github", new NotSupportedException()))
            .BuildServiceProvider();
        var mirror = TwigServiceRegistration.CreateMirrorRepository(services, neutralStoreOptedIn: true);

        var thrown = await Should.ThrowAsync<TwigConfigurationException>(() => mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build()));

        thrown.Message.ShouldStartWith("provider-unavailable");
        (await mirror.GetByIdAsync(42)).ShouldBeNull();
        CountIdentities().ShouldBe(0);
    }

    [Fact]
    public async Task ScopeTransportFailure_StillSurfaces()
    {
        var provider = Substitute.For<IWorkItemProvider>();
        provider.ResolveResourceScopeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<string?>(new HttpRequestException("transport")));
        var services = new ServiceCollection().AddSingleton(_store).AddSingleton(provider).BuildServiceProvider();
        var mirror = TwigServiceRegistration.CreateMirrorRepository(services, neutralStoreOptedIn: true);

        await Should.ThrowAsync<HttpRequestException>(() => mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build()));
        (await mirror.GetByIdAsync(42)).ShouldBeNull();
    }

    [Fact]
    public async Task ConstructionIsLazy_AMissingProviderSurfacesOnlyOnTheFirstOptedInSave()
    {
        var mirror = TwigServiceRegistration.CreateMirrorRepository(Services(provider: null), neutralStoreOptedIn: true);

        await Should.ThrowAsync<InvalidOperationException>(() => mirror.SaveAsync(new WorkItemBuilder(42, "Item").Build()));
        (await mirror.GetByIdAsync(42)).ShouldBeNull();
        CountIdentities().ShouldBe(0);
    }
}
