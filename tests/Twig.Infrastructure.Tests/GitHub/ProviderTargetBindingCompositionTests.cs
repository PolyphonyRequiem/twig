using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.DependencyInjection;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class ProviderTargetBindingCompositionTests
{
    private static readonly ExternalWorkItemIdentity Item = new("github", "https://api.github.com", "I_caseSensitive");

    [Theory]
    [InlineData("snapshot", "detach")]
    [InlineData("blocking", "rebind")]
    [InlineData("blocking", "detach-reattach")]
    public async Task NormalDi_SelectionChangesDuringFetchRefuseBeforeLocalWrite(string stage, string mutation)
    {
        using var fixture = new GitHubTargetFixture();
        await fixture.BindAsync();
        using var cache = new SqliteCacheStore("Data Source=:memory:");
        var local = new SqliteLocalWorkItemRepository(cache);
        var changed = false;
        using var network = new GitHubWorkItemProviderTests.RecordingTransport
        {
            BeforeResponseAsync = async query =>
            {
                if (changed || (stage == "snapshot" ? query != GitHubIssueQueries.Snapshot
                    : !query.Contains(stage + "(", StringComparison.Ordinal)))
                    return;
                changed = true;
                var current = (await fixture.Attachment.ReadWithRevisionAsync()).Value;
                var id = current.Envelope!.BindingId!.Value;
                if (mutation == "rebind")
                {
                    var result = await new ProviderTargetAttachmentService(fixture.Registry, fixture.Attachment)
                        .AttachAsync(new GitHubRepositoryTarget("O_other", "R_other")
                            .ToIdentity(new("https://api.github.com/graphql")), "other/repo", null, current.Revision);
                    result.IsSuccess.ShouldBeTrue(result.Error);
                }
                else
                {
                    var cleared = await fixture.Attachment.ClearAsync(id, current.Revision);
                    cleared.IsSuccess.ShouldBeTrue(cleared.Error);
                    if (mutation == "detach-reattach")
                        (await fixture.Attachment.WriteAsync(id, cleared.Value.WrittenRevision)).IsSuccess.ShouldBeTrue();
                }
            },
        };
        using var provider = Compose(fixture, local, new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), network);

        await Should.ThrowAsync<InvalidDataException>(() =>
            provider.GetRequiredService<IProviderWorkItemReadService>().ReadByIdentityAsync(Item));

        changed.ShouldBeTrue();
        (await local.FindAsync(Item)).ShouldBeNull();
        network.Requests.Count.ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task NormalDi_DisplayMetadataCasDuringReadDoesNotChangeSelection()
    {
        using var fixture = new GitHubTargetFixture();
        await fixture.BindAsync();
        using var cache = new SqliteCacheStore("Data Source=:memory:");
        var changed = false;
        using var network = new GitHubWorkItemProviderTests.RecordingTransport
        {
            BeforeResponseAsync = async query =>
            {
                if (changed || !query.Contains("blocking(", StringComparison.Ordinal))
                    return;
                changed = true;
                var identity = GitHubTargetFixture.Target.ToIdentity(new("https://api.github.com/graphql"));
                var row = (await fixture.Registry.FindByIdentityAsync(identity)).Value!;
                var updated = await fixture.Registry.UpdateMetadataAfterIdentityConfirmationAsync(
                    row.Id, identity, row.Revision, "renamed/repo", "Renamed");
                updated.IsSuccess.ShouldBeTrue(updated.Error);
            },
        };
        using var provider = Compose(fixture, new(cache), new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), network);

        var result = await provider.GetRequiredService<IProviderWorkItemReadService>().ReadByIdentityAsync(Item);

        changed.ShouldBeTrue();
        result.Identity.ShouldBe(Item);
        (await fixture.Attachment.ReadWithRevisionAsync()).Value.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task NormalDi_RegistryFailureIsPropagatedWithoutTokenOrFallback()
    {
        using var fixture = new GitHubTargetFixture();
        await fixture.BindAsync();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.SystemDbPath, Pooling = false,
        }.ToString()))
        {
            await connection.OpenAsync();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE layout_meta SET version = 11 WHERE id = 1;";
            await command.ExecuteNonQueryAsync();
        }
        using var newer = new SqliteSystemWorktreeRegistry(fixture.SystemDbPath, TimeProvider.System);
        using var attachment = new WorktreeLocalProviderTargetAttachmentStore(fixture.Paths, newer, TimeProvider.System);
        using var cache = new SqliteCacheStore("Data Source=:memory:");
        var tokens = new GitHubReadOnlyAuthHandlerTests.FakeTokenSource();
        using var network = new GitHubWorkItemProviderTests.RecordingTransport();
        using var provider = Compose(fixture, new(cache), tokens, network, services =>
        {
            services.AddSingleton<IProviderTargetBindingStore>(newer);
            services.AddSingleton<IProviderTargetAttachmentStore>(attachment);
        });

        var error = await Should.ThrowAsync<InvalidDataException>(() =>
            provider.GetRequiredService<IProviderWorkItemReadService>().ReadByIdentityAsync(Item));

        error.Message.ShouldBe(Twig.Domain.Services.Attachment.AttachmentStorageFailure.SystemStoreSchemaMismatch);
        tokens.Calls.ShouldBe(0);
        network.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task NormalDi_BoundIdentityAcceptsRename_AndKeepsMetadataUnchanged()
    {
        using var fixture = new GitHubTargetFixture();
        await fixture.BindAsync();
        using var cache = new SqliteCacheStore("Data Source=:memory:");
        var local = new SqliteLocalWorkItemRepository(cache);
        var tokens = new GitHubReadOnlyAuthHandlerTests.FakeTokenSource();
        using var network = new GitHubWorkItemProviderTests.RecordingTransport();
        using var provider = Compose(fixture, local, tokens, network);

        var result = await provider.GetRequiredService<IProviderWorkItemReadService>().ReadByIdentityAsync(Item);

        result.Identity.ShouldBe(Item);
        (await local.FindAsync(Item)).ShouldNotBeNull();
        var row = await fixture.Registry.FindByIdentityAsync(
            GitHubTargetFixture.Target.ToIdentity(new("https://api.github.com/graphql")));
        row.Value!.Locator.ShouldBe("original/repo");
        row.Value.Revision.ShouldBe(1);
        tokens.Calls.ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("O_transferred", "R_repo")]
    [InlineData("O_owner", "R_recreated")]
    [InlineData("o_owner", "R_repo")]
    public async Task NormalDi_TransferOrReusedSlugFailsBeforeLocalIdentityWrite(string owner, string repo)
    {
        using var fixture = new GitHubTargetFixture();
        await fixture.BindAsync();
        using var cache = new SqliteCacheStore("Data Source=:memory:");
        var local = new SqliteLocalWorkItemRepository(cache);
        using var network = new GitHubWorkItemProviderTests.RecordingTransport
        {
            Metadata = GitHubWorkItemProviderTests.RecordingTransport.DefaultMetadata
                .Replace("O_owner", owner, StringComparison.Ordinal)
                .Replace("R_repo", repo, StringComparison.Ordinal),
        };
        using var provider = Compose(fixture, local, new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), network);

        var error = await Should.ThrowAsync<JsonException>(() =>
            provider.GetRequiredService<IProviderWorkItemReadService>().ReadByIdentityAsync(Item));

        error.Message.ShouldContain("rebind");
        (await local.FindAsync(Item)).ShouldBeNull();
        network.Requests.Count.ShouldBe(1);
        (await fixture.Attachment.ReadWithRevisionAsync()).Value.Revision.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalDi_UnboundOrDetachedLegacySlugNeverAcquiresToken(bool detached)
    {
        using var fixture = new GitHubTargetFixture();
        if (detached)
        {
            await fixture.BindAsync();
            var read = await fixture.Attachment.ReadWithRevisionAsync();
            (await fixture.Attachment.ClearAsync(read.Value.Envelope!.BindingId!.Value, read.Value.Revision))
                .IsSuccess.ShouldBeTrue();
        }
        using var cache = new SqliteCacheStore("Data Source=:memory:");
        var local = new SqliteLocalWorkItemRepository(cache);
        var tokens = new GitHubReadOnlyAuthHandlerTests.FakeTokenSource();
        using var network = new GitHubWorkItemProviderTests.RecordingTransport();
        using var provider = Compose(fixture, local, tokens, network);

        var error = await Should.ThrowAsync<InvalidDataException>(() =>
            provider.GetRequiredService<IProviderWorkItemReadService>().ReadByIdentityAsync(Item));

        error.Message.ShouldContain("provider-target-unbound");
        tokens.Calls.ShouldBe(0);
        network.Requests.ShouldBeEmpty();
        (await local.FindAsync(Item)).ShouldBeNull();
    }

    [Theory]
    [InlineData("invalid-document")]
    [InlineData("directory")]
    [InlineData("missing-row")]
    [InlineData("wrong-scope")]
    public async Task NormalDi_InvalidAttachmentOrBindingFailsBeforeToken(string defect)
    {
        using var fixture = new GitHubTargetFixture();
        if (defect == "wrong-scope")
        {
            var identity = new GitHubRepositoryTarget("O_owner", "R_repo").ToIdentity(new("https://other.example/api/graphql"));
            (await new ProviderTargetAttachmentService(fixture.Registry, fixture.Attachment)
                .AttachAsync(identity, "new/repo", null, 0)).IsSuccess.ShouldBeTrue();
        }
        else
        {
            await fixture.BindAsync();
            var path = Path.Combine(fixture.Paths.TwigDir, "provider-target.json");
            if (defect == "invalid-document")
                await File.WriteAllTextAsync(path, "{}");
            else if (defect == "directory")
            {
                File.Delete(path);
                Directory.CreateDirectory(path);
            }
            else
            {
                var text = await File.ReadAllTextAsync(path);
                var read = await fixture.Attachment.ReadWithRevisionAsync();
                await File.WriteAllTextAsync(path, text.Replace(read.Value.Envelope!.BindingId!.Value.ToString(),
                    ProviderTargetBindingId.New().ToString(), StringComparison.Ordinal));
            }
        }
        using var cache = new SqliteCacheStore("Data Source=:memory:");
        var tokens = new GitHubReadOnlyAuthHandlerTests.FakeTokenSource();
        using var network = new GitHubWorkItemProviderTests.RecordingTransport();
        using var provider = Compose(fixture, new(cache), tokens, network);

        await Should.ThrowAsync<InvalidDataException>(() =>
            provider.GetRequiredService<IProviderWorkItemReadService>().ReadByIdentityAsync(Item));

        tokens.Calls.ShouldBe(0);
        network.Requests.ShouldBeEmpty();
    }

    private static ServiceProvider Compose(GitHubTargetFixture fixture, SqliteLocalWorkItemRepository local,
        IGitHubTokenSource tokens, GitHubWorkItemProviderTests.RecordingTransport network,
        Action<IServiceCollection>? overrides = null)
    {
        var config = new TwigConfiguration
        {
            Provider = "github",
            GitHub = new() { Host = "github.com", Repository = "old/configured-slug" },
            GitHubAccount = "selected-account",
        };
        var services = new ServiceCollection();
        services.AddSingleton(config);
        fixture.Register(services);
        services.AddSingleton(tokens);
        services.AddSingleton(new GitHubTransportOverride(() => network));
        services.AddSingleton<ILocalWorkItemStore>(local);
        services.AddSingleton<IExternalIdentityMap>(local);
        services.AddTwigNetworkServices(config);
        overrides?.Invoke(services);
        return services.BuildServiceProvider();
    }
}
