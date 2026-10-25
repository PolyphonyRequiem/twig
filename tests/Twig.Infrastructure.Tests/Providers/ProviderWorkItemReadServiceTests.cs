using NSubstitute;
using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Providers;
using Xunit;

namespace Twig.Infrastructure.Tests.Providers;

public sealed class ProviderWorkItemReadServiceTests
{
    private const string Scope = "https://api.github.com";
    private static readonly ExternalWorkItemIdentity Identity = new("github", Scope, "I_issue");

    [Fact]
    public async Task ReadByIdentity_ResolvesLocalIdAndOverlaysLocalFields_WithoutLosingProviderIdentity()
    {
        var snapshotReader = CreateSnapshotReader();
        var identities = Substitute.For<IExternalIdentityMap>();
        var localStore = Substitute.For<ILocalWorkItemStore>();
        var id = LocalWorkItemId.New();
        var token = new ProviderRevisionToken("native-revision");
        var relationship = new ProviderWorkItemRelationship(
            new ExternalWorkItemIdentity("github", Scope, "I_parent"),
            Identity,
            LinkKind.ParentChild);
        var snapshot = Snapshot(Identity, token, Fields(("System.Title", new TextValue("remote")),
            ("System.State", new TextValue("OPEN"))), [relationship], relationshipsComplete: true);
        var local = Local(id, 7, Fields(("System.State", new TextValue("locally held"))));

        snapshotReader.FetchSnapshotAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(snapshot));
        identities.ResolveAsync(Identity, token, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ExternalIdentityResolution(id, Created: false)));
        identities.RecordObservedTokenAsync(Identity, token, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));
        localStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LocalWorkItemRecord?>(local));

        var result = await new ProviderWorkItemReadService(snapshotReader, identities, localStore)
            .ReadByIdentityAsync(Identity);

        result.Identity.ShouldBe(Identity);
        result.Local.Id.ShouldBe(id);
        result.Local.Revision.ShouldBe(7);
        result.Fields["System.Title"].Value.ShouldBe(new TextValue("remote"));
        result.Fields["System.State"].Value.ShouldBe(new TextValue("locally held"));
        result.ProviderFields["System.State"].Value.ShouldBe(new TextValue("OPEN"));
        result.RevisionToken.ShouldBe(token);
        result.Relationships.ShouldBe([relationship]);
        await identities.Received(1).RecordObservedTokenAsync(Identity, token, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadByLocalId_SelectsOnlyIdentityInActiveProviderAndScope()
    {
        var snapshotReader = CreateSnapshotReader();
        var identities = Substitute.For<IExternalIdentityMap>();
        var localStore = Substitute.For<ILocalWorkItemStore>();
        var id = LocalWorkItemId.New();
        var local = Local(id, 2, FieldValues.Empty);
        var snapshot = Snapshot(
            Identity, null, Fields(("System.Title", new TextValue("issue"))), [], relationshipsComplete: true);

        localStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LocalWorkItemRecord?>(local));
        identities.GetExternalIdentitiesAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExternalWorkItemIdentity>>([
                new("ado", "https://dev.azure.com/org", "42"),
                new("github", "https://ghe.example", "I_other"),
                Identity,
            ]));
        snapshotReader.FetchSnapshotAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(snapshot));
        identities.ResolveAsync(Identity, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ExternalIdentityResolution(id, Created: false)));
        localStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LocalWorkItemRecord?>(local));

        var result = await new ProviderWorkItemReadService(snapshotReader, identities, localStore)
            .ReadByLocalIdAsync(id);

        result.ShouldNotBeNull();
        result.Identity.ShouldBe(Identity);
        result.RevisionToken.ShouldBeNull();
        await snapshotReader.Received(1).FetchSnapshotAsync(Identity, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadByLocalId_WithNoActiveProviderIdentity_ReturnsNullWithoutFetching()
    {
        var snapshotReader = CreateSnapshotReader();
        var identities = Substitute.For<IExternalIdentityMap>();
        var localStore = Substitute.For<ILocalWorkItemStore>();
        var id = LocalWorkItemId.New();

        localStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LocalWorkItemRecord?>(Local(id, 1, FieldValues.Empty)));
        identities.GetExternalIdentitiesAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExternalWorkItemIdentity>>([]));

        var result = await new ProviderWorkItemReadService(snapshotReader, identities, localStore)
            .ReadByLocalIdAsync(id);

        result.ShouldBeNull();
        await snapshotReader.DidNotReceive().FetchSnapshotAsync(
            Arg.Any<ExternalWorkItemIdentity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadByLocalId_WithMultipleActiveIdentities_FailsClosed()
    {
        var snapshotReader = CreateSnapshotReader();
        var identities = Substitute.For<IExternalIdentityMap>();
        var localStore = Substitute.For<ILocalWorkItemStore>();
        var id = LocalWorkItemId.New();

        localStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LocalWorkItemRecord?>(Local(id, 1, FieldValues.Empty)));
        identities.GetExternalIdentitiesAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ExternalWorkItemIdentity>>([
                Identity,
                new("github", Scope, "I_another"),
            ]));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            new ProviderWorkItemReadService(snapshotReader, identities, localStore).ReadByLocalIdAsync(id));

        exception.Message.ShouldStartWith("provider-identity-ambiguous:");
        await snapshotReader.DidNotReceive().FetchSnapshotAsync(
            Arg.Any<ExternalWorkItemIdentity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadByIdentity_RejectsForeignProviderOrScopeBeforeFetching()
    {
        var snapshotReader = CreateSnapshotReader();
        var identities = Substitute.For<IExternalIdentityMap>();
        var localStore = Substitute.For<ILocalWorkItemStore>();
        var service = new ProviderWorkItemReadService(snapshotReader, identities, localStore);

        await Should.ThrowAsync<ArgumentException>(() =>
            service.ReadByIdentityAsync(new ExternalWorkItemIdentity("ado", Scope, "42")));
        await Should.ThrowAsync<ArgumentException>(() =>
            service.ReadByIdentityAsync(new ExternalWorkItemIdentity("github", "https://ghe.example", "I_issue")));

        await snapshotReader.DidNotReceive().FetchSnapshotAsync(
            Arg.Any<ExternalWorkItemIdentity>(), Arg.Any<CancellationToken>());
        await identities.DidNotReceive().ResolveAsync(
            Arg.Any<ExternalWorkItemIdentity>(), Arg.Any<ProviderRevisionToken?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadByIdentity_RejectsProviderSnapshotForDifferentIdentity()
    {
        var snapshotReader = CreateSnapshotReader();
        var identities = Substitute.For<IExternalIdentityMap>();
        var localStore = Substitute.For<ILocalWorkItemStore>();
        var otherIdentity = new ExternalWorkItemIdentity("github", Scope, "I_other");
        snapshotReader.FetchSnapshotAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Snapshot(
                otherIdentity, null, FieldValues.Empty, [], relationshipsComplete: true)));

        var exception = await Should.ThrowAsync<InvalidOperationException>(() =>
            new ProviderWorkItemReadService(snapshotReader, identities, localStore).ReadByIdentityAsync(Identity));

        exception.Message.ShouldStartWith("provider-identity-mismatch:");
        await identities.DidNotReceive().ResolveAsync(
            Arg.Any<ExternalWorkItemIdentity>(), Arg.Any<ProviderRevisionToken?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingProviderToken_RemainsNullAndNeverUsesLocalCasRevision()
    {
        var snapshotReader = CreateSnapshotReader();
        var identities = Substitute.For<IExternalIdentityMap>();
        var localStore = Substitute.For<ILocalWorkItemStore>();
        var id = LocalWorkItemId.New();
        var local = Local(id, 91, FieldValues.Empty);
        snapshotReader.FetchSnapshotAsync(Identity, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Snapshot(
                Identity, null, FieldValues.Empty, [], relationshipsComplete: true)));
        identities.ResolveAsync(Identity, null, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ExternalIdentityResolution(id, Created: true)));
        localStore.GetAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<LocalWorkItemRecord?>(local));

        var result = await new ProviderWorkItemReadService(snapshotReader, identities, localStore)
            .ReadByIdentityAsync(Identity);

        result.Local.Revision.ShouldBe(91);
        result.RevisionToken.ShouldBeNull();
        await identities.Received(1).ResolveAsync(Identity, null, Arg.Any<CancellationToken>());
        await identities.DidNotReceive().RecordObservedTokenAsync(
            Arg.Any<ExternalWorkItemIdentity>(), Arg.Any<ProviderRevisionToken?>(), Arg.Any<CancellationToken>());
    }

    private static IWorkItemSnapshotReader CreateSnapshotReader()
    {
        var reader = Substitute.For<IWorkItemSnapshotReader>();
        reader.Capabilities.Returns(ProviderCapabilityCatalog.GitHub);
        reader.ResolveResourceScopeAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(Scope));
        return reader;
    }

    private static ProviderWorkItemSnapshot Snapshot(
        ExternalWorkItemIdentity identity,
        ProviderRevisionToken? token,
        FieldValues fields,
        IReadOnlyList<ProviderWorkItemRelationship> relationships,
        bool relationshipsComplete)
        => new(identity, token, fields, relationships, relationshipsComplete);

    private static LocalWorkItemRecord Local(LocalWorkItemId id, long revision, FieldValues fields)
    {
        var now = DateTimeOffset.UtcNow;
        return new LocalWorkItemRecord(id, revision, fields, now, now, DeletedAt: null);
    }

    private static FieldValues Fields(params (string Name, FieldValue Value)[] values)
        => FieldValues.Create(values.Select(value =>
            new KeyValuePair<string, FieldValue>(value.Name, value.Value)));
}
