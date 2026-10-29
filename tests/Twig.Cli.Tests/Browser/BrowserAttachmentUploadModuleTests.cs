using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Cli.Tests.Browser;

[Trait("Category", "HostInventory")]
public sealed class BrowserAttachmentUploadModuleTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture fixture = new();
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync().AsTask();

    [HostMigrationFact]
    public async Task PreparationBindsExactFileTargetAndDigestBeforeTheConfirmedWriterCall()
    {
        var (runtime, prepare) = await PrepareAsync();
        var reader = Substitute.For<IWorkItemAttachmentReader>();
        reader.ReadAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemAttachmentInventory(42, 7, []));
        var content = new byte[] { 1, 2, 3 };
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var attachmentId = Guid.NewGuid();
        var writer = Substitute.For<IWorkItemAttachmentWriter>();
        writer.UploadAndLinkAsync(42, 7, "report.txt", "text/plain", content, sha256,
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemAttachmentUploadReceipt(42, 8, attachmentId, "report.txt", content.Length));
        var module = Module(runtime, reader, writer);

        var prepared = await module.PrepareAsync(prepare);
        prepared.Status.ShouldBe("prepared");
        prepared.IntentDigest.ShouldNotBeNull();
        prepared.Context!.ItemKey.ShouldBe(prepare.ItemKey);
        prepared.Context.WorkItemRevision.ShouldBe(7);
        var reprepared = await module.PrepareAsync(prepare);
        reprepared.Context.ShouldBe(prepared.Context);
        reprepared.IntentDigest.ShouldBe(prepared.IntentDigest);
        await writer.DidNotReceiveWithAnyArgs().UploadAndLinkAsync(default, default, default!, default!,
            default!, default!, default!, default);

        var request = new AttachmentUploadRequest(1, prepare.ItemKey, prepare.Bench, prepare.ExpectBench,
            prepare.ExpectIdentity, 7, "report.txt", "text/plain", content.Length, sha256, new string('b', 64));
        var refused = await module.UploadAsync(request, content);
        refused.Status.ShouldBe("refused");
        refused.Error!.Code.ShouldBe("confirmation-mismatch");
        await writer.DidNotReceiveWithAnyArgs().UploadAndLinkAsync(default, default, default!, default!,
            default!, default!, default!, default);

        var uploaded = await module.UploadAsync(request with { Confirm = prepared.IntentDigest }, content);
        uploaded.Status.ShouldBe("uploaded", uploaded.Error?.Code + ": " + uploaded.Error?.Message);
        uploaded.Context!.ItemKey.ShouldBe(prepare.ItemKey);
        uploaded.AttachmentId.ShouldBe(attachmentId.ToString("D"));
        await writer.Received(1).UploadAndLinkAsync(42, 7, "report.txt", "text/plain", content,
            sha256, prepared.IntentDigest, Arg.Any<CancellationToken>());
    }

    [HostMigrationTheory]
    [InlineData("non-member")]
    [InlineData("revision")]
    [InlineData("binding")]
    [InlineData("identity")]
    public async Task ChangedTargetOrAuthorityRefusesBeforeAttachmentWrite(string change)
    {
        var (runtime, request) = await PrepareAsync();
        var reader = Substitute.For<IWorkItemAttachmentReader>();
        reader.ReadAsync(42, Arg.Any<CancellationToken>())
            .Returns(new WorkItemAttachmentInventory(42, 7, []));
        var writer = Substitute.For<IWorkItemAttachmentWriter>();
        var module = Module(runtime, reader, writer);
        var content = new byte[] { 1, 2, 3 };
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var modified = change switch
        {
            "non-member" => request with
            {
                ItemKey = new(request.ItemKey.CheckoutRoot, request.ItemKey.ConnectionId,
                    request.ItemKey.BindingId, 99),
            },
            "revision" => request with { ExpectedRevision = 8 },
            "binding" => request with
            {
                ItemKey = new(request.ItemKey.CheckoutRoot, request.ItemKey.ConnectionId,
                    "different-binding", request.ItemKey.WorkItemId),
            },
            "identity" => request with { ExpectIdentity = "different-identity" },
            _ => throw new InvalidOperationException(change),
        };
        var result = await module.PrepareAsync(modified);

        result.Status.ShouldBe("refused");
        result.Error!.Code.ShouldBe(change switch
        {
            "non-member" => "bench-item-not-member",
            "revision" => "attachment-stale",
            _ => "authority-invalidated",
        });
        if (change == "revision")
            await reader.Received(1).ReadAsync(42, Arg.Any<CancellationToken>());
        else
            await reader.DidNotReceiveWithAnyArgs().ReadAsync(default, default);
        await writer.DidNotReceiveWithAnyArgs().UploadAndLinkAsync(default, default, default!, default!,
            default!, default!, default!, default);
    }

    private async Task<(ServiceProvider Runtime, AttachmentUploadPrepareRequest Request)> PrepareAsync()
    {
        var runtime = await fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Upload target"));
        await repository.SaveAsync(NativeBrowserFixture.Item(99, "Other item"));
        var bench = (await runtime.GetRequiredService<IBenchRepository>().CreateAsync("Upload target"))!;
        await runtime.GetRequiredService<IBenchRepository>().AddSelectorAsync(bench.Id, BenchSelector.ForItem(42));
        var origin = await fixture.Bindings.ResolveAsync(fixture.Configuration, fixture.CurrentPaths());
        var content = new byte[] { 1, 2, 3 };
        return (runtime, new AttachmentUploadPrepareRequest(1,
            new(origin.WorktreeRoot, origin.Binding.ConnectionRef, origin.Binding.BindingId, 42),
            bench.Name, bench.Id.ToString(CultureInfo.InvariantCulture), origin.Identity.IdentityId,
            7, "report.txt", "text/plain", content.Length,
            Convert.ToHexStringLower(SHA256.HashData(content))));
    }

    private BrowserAttachmentUploadModule Module(
        ServiceProvider runtime, IWorkItemAttachmentReader reader, IWorkItemAttachmentWriter writer) => new(
        new BrowserOperationModule(runtime.GetRequiredService<CommandContext>(),
            runtime.GetRequiredService<CurrentBenchResolver>(),
            runtime.GetRequiredService<IWorkItemRepository>(),
            runtime.GetRequiredService<IAuthenticationProvider>(),
            fixture.Bindings,
            runtime.GetRequiredService<Twig.Infrastructure.Config.TwigPaths>()),
        runtime.GetRequiredService<BenchEvaluator>(),
        runtime.GetRequiredService<IWorkItemRepository>(), reader, writer);
}
