using System.Net;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubProviderDurableCompositionTests
{
    private static readonly ExternalWorkItemIdentity Identity =
        new("github", "https://api.github.com", "I_caseSensitive");

    [Fact]
    public async Task ReadByIdentity_ComposesGitHubWithDurableSqliteIdentityAndLocalOverlay()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"twig-github-composition-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var connectionString = $"Data Source={Path.Combine(directory, "twig.db")}";
        var localId = default(LocalWorkItemId);
        var adoId = default(LocalWorkItemId);
        var otherGitHubId = default(LocalWorkItemId);
        var adoIdentity = new ExternalWorkItemIdentity("ado", "https://dev.azure.com/org", Identity.NativeId);
        var otherGitHubIdentity = new ExternalWorkItemIdentity("github", "https://ghe.example", Identity.NativeId);

        try
        {
            using var targetFixture = new GitHubTargetFixture();
            await targetFixture.BindAsync();

            using (var store = new SqliteCacheStore(connectionString))
            using (var transport = new GitHubWorkItemProviderTests.RecordingTransport())
            using (var services = BuildProvider(targetFixture, store, transport, new GitHubReadOnlyAuthHandlerTests.FakeTokenSource()))
            {
                var tokens = services.GetRequiredService<IGitHubTokenSource>()
                    .ShouldBeOfType<GitHubReadOnlyAuthHandlerTests.FakeTokenSource>();
                var reader = services.GetRequiredService<IProviderWorkItemReadService>();
                var localStore = services.GetRequiredService<ILocalWorkItemStore>();
                var identities = services.GetRequiredService<IExternalIdentityMap>();

                tokens.Calls.ShouldBe(0);
                transport.Requests.ShouldBeEmpty();
                var first = await reader.ReadByIdentityAsync(Identity);

                first.Identity.ShouldBe(Identity);
                first.Local.Revision.ShouldBe(1);
                first.ProviderFields["System.Title"].Value.ShouldBe(new TextValue("Issue title"));
                first.Fields["System.State"].Value.ShouldBe(new TextValue("OPEN"));
                first.Relationships.ShouldNotBeEmpty();
                localId = first.Local.Id;
                (await identities.FindAsync(Identity)).ShouldBe(localId);

                var localFields = FieldValues.Empty.Set("System.State", new TextValue("locally held"));
                (await localStore.UpdateAsync(localId, first.Local.Revision, localFields))
                    .Value.ShouldBeOfType<LocalWriteApplied>().Record.Revision.ShouldBe(2);

                var refreshed = await reader.ReadByIdentityAsync(Identity);

                refreshed.Local.Id.ShouldBe(localId);
                refreshed.Local.Revision.ShouldBe(2);
                refreshed.ProviderFields["System.State"].Value.ShouldBe(new TextValue("OPEN"));
                refreshed.Fields["System.State"].Value.ShouldBe(new TextValue("locally held"));
                refreshed.Local.Fields.ShouldBe(localFields);

                adoId = (await identities.ResolveAsync(adoIdentity, null)).Id;
                otherGitHubId = (await identities.ResolveAsync(otherGitHubIdentity, null)).Id;
                adoId.ShouldNotBe(localId);
                otherGitHubId.ShouldNotBe(localId);
                adoId.ShouldNotBe(otherGitHubId);
                tokens.Calls.ShouldBeGreaterThan(0);
                transport.Requests.ShouldNotBeEmpty();
                Should.Throw<TwigConfigurationException>(() => services.GetRequiredService<IAdoWorkItemService>())
                    .Message.ShouldStartWith("provider-unavailable:");
            }

            using (var reopenedStore = new SqliteCacheStore(connectionString))
            {
                var durable = new SqliteLocalWorkItemRepository(reopenedStore);
                (await durable.FindAsync(Identity)).ShouldBe(localId);
                (await durable.FindAsync(adoIdentity)).ShouldBe(adoId);
                (await durable.FindAsync(otherGitHubIdentity)).ShouldBe(otherGitHubId);

                var reopened = await durable.GetAsync(localId);
                reopened.ShouldNotBeNull();
                reopened.Revision.ShouldBe(2);
                reopened.Fields.ShouldBe(FieldValues.Empty.Set("System.State", new TextValue("locally held")));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReadByIdentity_TransportAndCredentialFailuresSurfaceWithoutCreatingIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"twig-github-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var connectionString = $"Data Source={Path.Combine(directory, "twig.db")}";

        try
        {
            using var targetFixture = new GitHubTargetFixture();
            await targetFixture.BindAsync();

            using (var store = new SqliteCacheStore(connectionString))
            using (var transport = new GitHubWorkItemProviderTests.RecordingTransport { Status = HttpStatusCode.Forbidden })
            using (var services = BuildProvider(targetFixture, store, transport, new GitHubReadOnlyAuthHandlerTests.FakeTokenSource()))
            {
                await Should.ThrowAsync<HttpRequestException>(() =>
                    services.GetRequiredService<IProviderWorkItemReadService>().ReadByIdentityAsync(Identity));
                (await services.GetRequiredService<IExternalIdentityMap>().FindAsync(Identity)).ShouldBeNull();
            }

            using (var store = new SqliteCacheStore(connectionString))
            using (var transport = new GitHubWorkItemProviderTests.RecordingTransport())
            using (var services = BuildProvider(targetFixture, store, transport, new FailingTokenSource()))
            {
                await Should.ThrowAsync<GitHubCredentialUnavailableException>(() =>
                    services.GetRequiredService<IProviderWorkItemReadService>().ReadByIdentityAsync(Identity));
                transport.Requests.ShouldBeEmpty();
                (await services.GetRequiredService<IExternalIdentityMap>().FindAsync(Identity)).ShouldBeNull();
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceProvider BuildProvider(
        GitHubTargetFixture targetFixture,
        SqliteCacheStore store,
        GitHubWorkItemProviderTests.RecordingTransport transport,
        IGitHubTokenSource tokens)
    {
        var config = new TwigConfiguration
        {
            Provider = "github",
            GitHub = new() { Host = "github.com", Repository = "new/repo" },
        };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        services.AddSingleton(store);
        services.AddSingleton<IGitHubTokenSource>(tokens);
        services.AddSingleton(new GitHubTransportOverride(() => transport));
        services.AddSingleton(Substitute.For<IAuthenticationProvider>());
        targetFixture.Register(services);
        services.AddTwigNetworkServices(config);
        return services.BuildServiceProvider();
    }

    private sealed class FailingTokenSource : IGitHubTokenSource
    {
        public ValueTask<string> GetTokenAsync(CancellationToken ct)
            => ValueTask.FromException<string>(new GitHubCredentialUnavailableException("No selected credential."));
    }
}
