using System.Net;
using System.Text;
using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.GitHub;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Xunit;

namespace Twig.Infrastructure.Tests.GitHub;

public sealed class GitHubProviderSetupServiceTests
{
    private const string Host = "github.com";
    private const string Locator = "original/repo";

    [Fact]
    public async Task Discovery_ReportsRevisionWithoutCreatingDatabaseOrPointer()
    {
        using var fixture = new GitHubTargetFixture();
        var service = Setup(fixture);

        var result = await service.DiscoverAsync(Host, Locator);

        result.OwnerId.ShouldBe("O_owner");
        result.RepositoryId.ShouldBe("R_repo");
        result.AttachmentRevision.ShouldBe(0);
        result.CurrentBindingId.ShouldBeNull();
        File.Exists(fixture.SystemDbPath).ShouldBeFalse();
        File.Exists(Path.Combine(fixture.Paths.TwigDir, ProviderTargetAttachmentDocument.FileName)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("wrong", "R_repo")]
    [InlineData("O_owner", "wrong")]
    public async Task ExpectedIdentityMismatch_DoesNotCreateRowOrPointer(string owner, string repository)
    {
        using var fixture = new GitHubTargetFixture();

        var error = await Should.ThrowAsync<InvalidDataException>(() =>
            Setup(fixture).BindAsync(Host, Locator, owner, repository, 0, null));

        error.Message.ShouldStartWith("provider-discovery-identity-mismatch:");
        File.Exists(fixture.SystemDbPath).ShouldBeFalse();
        (await fixture.Attachment.ReadWithRevisionAsync()).Value.Revision.ShouldBe(0);
    }

    [Fact]
    public async Task FirstBind_ThenSameIdentityRename_KeepsBindingAndMetadata()
    {
        using var fixture = new GitHubTargetFixture();
        var first = await Setup(fixture).BindAsync(Host, Locator, "O_owner", "R_repo", 0, null);
        var renamed = await Setup(fixture, locator: "renamed/repository")
            .BindAsync(Host, "renamed/repository", "O_owner", "R_repo", 1, null);

        renamed.BindingId.ShouldBe(first.BindingId);
        renamed.AttachmentRevision.ShouldBe(2);
        var row = (await fixture.Registry.FindByIdAsync(
            Twig.Domain.ValueObjects.ProviderTargetBindingId.TryParse(first.BindingId, out var id)
                ? id : throw new InvalidOperationException())).Value!;
        row.Locator.ShouldBe(Locator);
        row.Revision.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000000000000000000000000001")]
    public async Task DifferentIdentity_RequiresExactCurrentBinding(string? replacement)
    {
        using var fixture = new GitHubTargetFixture();
        await fixture.BindAsync();

        var error = await Should.ThrowAsync<InvalidDataException>(() =>
            Setup(fixture, owner: "O_other").BindAsync(Host, Locator, "O_other", "R_repo", 1, replacement));

        error.Message.ShouldStartWith("provider-target-replacement-required:");
        (await fixture.Attachment.ReadWithRevisionAsync()).Value.Revision.ShouldBe(1);
        (await fixture.Registry.FindByIdentityAsync(new GitHubRepositoryTarget("O_other", "R_repo")
            .ToIdentity(new("https://api.github.com/graphql")))).Value.ShouldBeNull();
    }

    [Fact]
    public async Task ExplicitReplacement_RetainsOldRow_AndDetachedRebindRequiresTombstoneRevision()
    {
        using var fixture = new GitHubTargetFixture();
        await fixture.BindAsync();
        var old = (await fixture.Attachment.ReadWithRevisionAsync()).Value.Envelope!.BindingId!.Value;
        var replaced = await Setup(fixture, owner: "O_other")
            .BindAsync(Host, Locator, "O_other", "R_repo", 1, old.ToString());
        (await fixture.Registry.FindByIdAsync(old)).Value.ShouldNotBeNull();
        Twig.Domain.ValueObjects.ProviderTargetBindingId.TryParse(replaced.BindingId, out var current).ShouldBeTrue();
        (await fixture.Attachment.ClearAsync(current, 2)).IsSuccess.ShouldBeTrue();

        await Should.ThrowAsync<InvalidDataException>(() =>
            Setup(fixture).BindAsync(Host, Locator, "O_owner", "R_repo", 1, null));
        var rebound = await Setup(fixture).BindAsync(Host, Locator, "O_owner", "R_repo", 3, null);
        rebound.BindingId.ShouldBe(old.ToString());
        rebound.AttachmentRevision.ShouldBe(4);
    }

    [Fact]
    public async Task SameOpaqueIdsAtDifferentHost_StillRequireReplacement()
    {
        using var fixture = new GitHubTargetFixture();
        await fixture.BindAsync();

        await Should.ThrowAsync<InvalidDataException>(() =>
            Setup(fixture).BindAsync("github.example.com", Locator, "O_owner", "R_repo", 1, null));

        (await fixture.Attachment.ReadWithRevisionAsync()).Value.Revision.ShouldBe(1);
    }

    [Fact]
    public async Task SelectionChangesDuringFreshLookup_RefuseBeforeTargetRowCreation()
    {
        using var fixture = new GitHubTargetFixture();
        await fixture.BindAsync();
        var id = (await fixture.Attachment.ReadWithRevisionAsync()).Value.Envelope!.BindingId!.Value;
        var service = Setup(fixture, owner: "O_other", duringLookup: async () =>
        {
            (await fixture.Attachment.ClearAsync(id, 1)).IsSuccess.ShouldBeTrue();
        });

        var error = await Should.ThrowAsync<InvalidDataException>(() =>
            service.BindAsync(Host, Locator, "O_other", "R_repo", 1, id.ToString()));

        error.Message.ShouldBe(ProviderTargetAttachmentFailures.RevisionMismatch);
        (await fixture.Registry.FindByIdentityAsync(new GitHubRepositoryTarget("O_other", "R_repo")
            .ToIdentity(new("https://api.github.com/graphql")))).Value.ShouldBeNull();
    }

    [Fact]
    public async Task SameIdentityAfterDetachAndReattach_StillRejectsOldObservation()
    {
        using var fixture = new GitHubTargetFixture();
        await fixture.BindAsync();
        var original = (await fixture.Attachment.ReadWithRevisionAsync()).Value;
        var id = original.Envelope!.BindingId!.Value;
        (await fixture.Attachment.ClearAsync(id, original.Revision)).IsSuccess.ShouldBeTrue();
        var detached = (await fixture.Attachment.ReadWithRevisionAsync()).Value;
        (await new ProviderTargetAttachmentService(fixture.Registry, fixture.Attachment).AttachAsync(
            GitHubTargetFixture.Target.ToIdentity(new("https://api.github.com/graphql")),
            Locator, null, detached.Revision)).IsSuccess.ShouldBeTrue();
        var reattached = (await fixture.Attachment.ReadWithRevisionAsync()).Value;
        reattached.Envelope!.BindingId.ShouldBe(id);
        reattached.Revision.ShouldBeGreaterThan(original.Revision);
        var lookupCalls = 0;

        var error = await Should.ThrowAsync<InvalidDataException>(() =>
            Setup(fixture, duringLookup: () =>
            {
                lookupCalls++;
                return Task.CompletedTask;
            }).BindAsync(Host, Locator, "O_owner", "R_repo", original.Revision, null));

        error.Message.ShouldBe(ProviderTargetAttachmentFailures.RevisionMismatch);
        lookupCalls.ShouldBe(1);
        (await fixture.Attachment.ReadWithRevisionAsync()).Value.Revision.ShouldBe(reattached.Revision);
    }

    [Fact]
    public async Task LookupFailure_DoesNotCreateTargetState()
    {
        using var fixture = new GitHubTargetFixture();
        var service = new GitHubProviderSetupService(() => fixture.Registry, () => fixture.Attachment,
            ct => ObserveAttachmentAsync(fixture, ct), _ =>
            new HttpClient(new DiscoveryHandler("""{"data":{"repository":null}}""")));

        await Should.ThrowAsync<System.Text.Json.JsonException>(() =>
            service.BindAsync(Host, Locator, "O_owner", "R_repo", 0, null));

        File.Exists(fixture.SystemDbPath).ShouldBeFalse();
        (await fixture.Attachment.ReadWithRevisionAsync()).Value.Revision.ShouldBe(0);
    }

    private static GitHubProviderSetupService Setup(GitHubTargetFixture fixture,
        string owner = "O_owner", string locator = Locator, Func<Task>? duringLookup = null) =>
        new(() => fixture.Registry, () => fixture.Attachment, ct => ObserveAttachmentAsync(fixture, ct),
            scope => GitHubReadOnlyAuthHandler.CreateClient(
            new GitHubReadOnlyAuthHandlerTests.FakeTokenSource(), scope.GraphQlEndpoint,
            new DiscoveryHandler(
                $$$$$"""{"data":{"repository":{"id":"R_repo","nameWithOwner":"{{{{{locator}}}}}","owner":{"id":"{{{{{owner}}}}}","login":"original"}}}}""",
                duringLookup)));

    private static async Task<VersionedProviderTargetAttachment> ObserveAttachmentAsync(
        GitHubTargetFixture fixture, CancellationToken ct)
    {
        var result = await WorktreeLocalProviderTargetAttachmentStore.ReadObservationWithRevisionAsync(fixture.Paths, ct);
        result.IsSuccess.ShouldBeTrue(result.Error);
        return result.Value;
    }

    private sealed class DiscoveryHandler(string body, Func<Task>? callback = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (callback is not null)
                await callback();
            return new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
