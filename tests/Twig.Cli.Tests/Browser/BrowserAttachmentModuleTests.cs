using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Cli.Tests.Browser;

[Trait("Category", "HostInventory")]
public sealed class BrowserAttachmentModuleTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync().AsTask();

    [HostMigrationFact]
    public async Task InventoryAndDownloadRequireTheExactPublishedCachedBenchMember()
    {
        var (runtime, request) = await PrepareAsync();
        var attachment = new WorkItemAttachment(Guid.NewGuid(), "report.pdf", 12);
        var reader = Substitute.For<IWorkItemAttachmentReader>();
        reader.ReadAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemAttachmentInventory(42, 7, [attachment]));
        reader.DownloadAsync(42, 7, attachment.Id, Arg.Any<CancellationToken>())
            .Returns(new WorkItemAttachmentDownload(42, 7, attachment, "application/pdf",
                [0, 255, 13, 10], "hash"));
        var module = Module(runtime, reader);

        var inventory = await module.ReadAsync(request);
        inventory.Result.Status.ShouldBe("inventory");
        inventory.Result.Context!.WorkItemRevision.ShouldBe(7);
        inventory.Result.Attachments.ShouldHaveSingleItem().Id.ShouldBe(attachment.Id.ToString("D"));
        inventory.Content.ShouldBeNull();

        var download = await module.ReadAsync(request with
        {
            Action = "download",
            ExpectedRevision = 7,
            AttachmentId = attachment.Id,
        });
        download.Result.Status.ShouldBe("download");
        download.Result.ContentLength.ShouldBe(4);
        download.Content.ShouldBe(new byte[] { 0, 255, 13, 10 });
        await reader.Received(1).ReadAsync(42, Arg.Any<CancellationToken>());
        await reader.Received(1).DownloadAsync(42, 7, attachment.Id, Arg.Any<CancellationToken>());
        (await runtime.GetRequiredService<IBenchRepository>().GetCurrentAsync())!.Name.ShouldBe("Current stays here");
        (await runtime.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(99);
    }

    [HostMigrationFact]
    public async Task NonmemberAndMalformedRequestsNeverReachTheAttachmentReader()
    {
        var (runtime, request) = await PrepareAsync();
        var reader = Substitute.For<IWorkItemAttachmentReader>();
        var module = Module(runtime, reader);

        var malformed = await module.ReadAsync(request with { Action = "download", ExpectedRevision = null, AttachmentId = null });
        malformed.Result.Error!.Code.ShouldBe("usage");
        var nonmember = await module.ReadAsync(request with
        {
            ItemKey = new QualifiedWorkItemKey(
                request.ItemKey.CheckoutRoot,
                request.ItemKey.ConnectionId,
                request.ItemKey.BindingId,
                99),
        });
        nonmember.Result.Error!.Code.ShouldBe("bench-item-not-member");
        await reader.DidNotReceiveWithAnyArgs().ReadAsync(default);
        await reader.DidNotReceiveWithAnyArgs().DownloadAsync(default, default, default);
    }

    [HostMigrationFact]
    public async Task ProviderStaleMembershipFailureIsReturnedAsARefusalWithoutBytes()
    {
        var (runtime, request) = await PrepareAsync();
        var reader = Substitute.For<IWorkItemAttachmentReader>();
        reader.DownloadAsync(42, 7, Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns<Task<WorkItemAttachmentDownload>>(_ => throw new AdoException("attachment-stale: changed"));
        var result = await Module(runtime, reader).ReadAsync(request with
        {
            Action = "download", ExpectedRevision = 7, AttachmentId = Guid.NewGuid(),
        });
        result.Result.Error!.Code.ShouldBe("attachment-stale");
        result.Content.ShouldBeNull();
    }

    private async Task<(ServiceProvider Runtime, AttachmentReadRequest Request)> PrepareAsync()
    {
        var runtime = await fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Cached target"));
        await repository.SaveAsync(NativeBrowserFixture.Item(99, "Active item"));
        await runtime.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(99);
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var target = (await benches.CreateAsync("Attachment target"))!;
        await benches.AddSelectorAsync(target.Id, BenchSelector.ForItem(42));
        var current = (await benches.CreateAsync("Current stays here"))!;
        await benches.SetCurrentAsync(current.Id);
        var origin = await fixture.Bindings.ResolveAsync(fixture.Configuration, fixture.CurrentPaths());
        return (runtime, new AttachmentReadRequest(1, "inventory",
            new(origin.WorktreeRoot, origin.Binding.ConnectionRef, origin.Binding.BindingId, 42),
            target.Name, target.Id.ToString(CultureInfo.InvariantCulture), origin.Identity.IdentityId, null, null));
    }

    private static BrowserAttachmentModule Module(ServiceProvider runtime, IWorkItemAttachmentReader reader) => new(
        runtime.GetRequiredService<BrowserOperationModule>(), runtime.GetRequiredService<BenchEvaluator>(),
        runtime.GetRequiredService<IWorkItemRepository>(), reader);
}
