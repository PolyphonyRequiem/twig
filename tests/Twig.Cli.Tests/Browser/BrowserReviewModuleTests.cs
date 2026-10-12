using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Browser;

[Trait("Category", "HostInventory")]
public sealed class BrowserReviewModuleTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [HostMigrationFact]
    public async Task DetailsAndBackRetainOriginalProposalWhenLatestSelectionMoves()
    {
        var runtime = await Runtime();
        var first = await WriteProposal("first.json", "First proposed title", "FIRST-BODY-TAIL");
        var module = Module(runtime);
        var observation = await Open(runtime, module, first);
        var digest = observation.Digest;
        var second = await WriteProposal("second.json", "Second proposed title", "SECOND-BODY-TAIL");
        await runtime.GetRequiredService<IPlanLifecycleService>().PreviewAsync(second);
        (await module.ValidateAsync(observation)).Failure.ShouldBeNull();
        var full = observation.Render(200, true);
        full.ShouldContain("FIRST-BODY-TAIL");
        full.ShouldNotContain("SECOND-BODY-TAIL");
        observation.Render(200, false).ShouldNotContain("FIRST-BODY-TAIL");
        observation.Digest.ShouldBe(digest);
        observation.Model!.Operations.SelectMany(operation => operation.Consequences)
            .Single(effect => effect.Field == "System.Title").To.ShouldBe("First proposed title");
        _fixture.Transport.Mutations.ShouldBe(0);
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(42)).ShouldBeEmpty();
    }

    [HostMigrationFact]
    public async Task CanceledGestureKeepsCapturedProposalForTheNextQualifiedView()
    {
        var runtime = await Runtime();
        var file = await WriteProposal("cancel.json", "Captured proposal", "CAPTURED-BODY");
        var module = Module(runtime);
        var observation = await Open(runtime, module, file);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var result = await module.ValidateAsync(observation, canceled.Token);

        result.Failure!.Kind.ShouldBe(BrowserReadFailureKind.Canceled);
        observation.Render(200, true).ShouldContain("CAPTURED-BODY");
        (await module.ValidateAsync(observation)).Failure.ShouldBeNull();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task ChangedFileClearsRetainedSemanticContentRatherThanOpeningReplacement()
    {
        var runtime = await Runtime();
        var file = await WriteProposal("review.json", "Captured proposal", "CAPTURED-BODY");
        var module = Module(runtime);
        var observation = await Open(runtime, module, file);
        var digest = observation.Digest;
        await WriteProposal("review.json", "Replacement proposal", "REPLACEMENT-BODY");
        var result = await module.ValidateAsync(observation);
        result.Failure.ShouldNotBeNull();
        observation.IsValid.ShouldBeFalse();
        observation.Model.ShouldBeNull();
        observation.Digest.ShouldBe(digest);
        observation.Render(200, true).ShouldNotContain("CAPTURED-BODY");
        observation.Render(200, true).ShouldNotContain("REPLACEMENT-BODY");
        (await module.ValidateAsync(observation)).Value.ShouldBeFalse();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task NativeJournalOriginChangeClearsReviewEvenWhenBrowserCaptureStillMatches()
    {
        var runtime = await Runtime();
        var file = await WriteProposal("origin.json", "Captured proposal", "CAPTURED-BODY");
        var real = runtime.GetRequiredService<IPlanOriginProvider>();
        var original = await real.GetOriginAsync();
        var changed = false;
        var origins = Substitute.For<IPlanOriginProvider>();
        origins.GetOriginAsync(Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(
            changed ? original with { AttachmentRevision = original.AttachmentRevision + 1 } : original));
        var module = Module(runtime, origins: origins);
        var observation = await Open(runtime, module, file);
        changed = true;
        var result = await module.ValidateAsync(observation);
        result.Failure!.Kind.ShouldBe(BrowserReadFailureKind.AuthorityInvalidated);
        observation.Model.ShouldBeNull();
        observation.Render(200, true).ShouldNotContain("CAPTURED-BODY");
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task FileChangedBetweenValidationAndExpectedPreviewCannotBecomeNewObservation()
    {
        var runtime = await Runtime();
        var file = await WriteProposal("race.json", "Original proposal", "ORIGINAL-BODY");
        var real = runtime.GetRequiredService<IPlanLifecycleService>();
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        lifecycle.ValidateAsync(file, Arg.Any<CancellationToken>()).Returns(call => real.ValidateAsync(file, call.ArgAt<CancellationToken>(1)));
        lifecycle.PreviewExpectedAsync(file, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await WriteProposal("race.json", "Unexpected replacement", "REPLACEMENT-BODY");
            return await real.PreviewExpectedAsync(file, call.ArgAt<string>(1), call.ArgAt<CancellationToken>(2));
        });
        var module = Module(runtime, lifecycle);
        var result = await module.OpenAsync(file, await Capture(runtime));
        result.Failure.ShouldNotBeNull();
        result.Value.ShouldBeNull();
        // Expected-digest refusal occurs before journal import; viewing cannot adopt this replacement.
        (await real.StatusAsync(file)).ShouldBeNull();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task NullFileUsesNativeLatestAndDoesNotFallBackFromChangedLatest()
    {
        var runtime = await Runtime();
        var module = Module(runtime);
        var empty = await Open(runtime, module, null);
        empty.IsEmpty.ShouldBeTrue();
        empty.Model.ShouldBeNull();
        var older = await WriteProposal("older.json", "Older proposal", "OLDER-BODY");
        await runtime.GetRequiredService<IPlanLifecycleService>().PreviewAsync(older);
        var latest = await WriteProposal("latest.json", "Latest proposal", "LATEST-BODY");
        await runtime.GetRequiredService<IPlanLifecycleService>().PreviewAsync(latest);
        var observation = await Open(runtime, module, null);
        observation.FilePath.ShouldBe(latest);
        observation.Render(200, true).ShouldContain("LATEST-BODY");
        await WriteProposal("latest.json", "Do not adopt this", "CHANGED-BODY");
        var refused = await module.OpenAsync(null, await Capture(runtime));
        refused.Failure.ShouldNotBeNull();
        refused.Value.ShouldBeNull();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    private async Task<ServiceProvider> Runtime()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Cached title"));
        return runtime;
    }

    private BrowserReviewModule Module(ServiceProvider runtime, IPlanLifecycleService? lifecycle = null,
        IPlanOriginProvider? origins = null) => new(runtime.GetRequiredService<BrowserOperationModule>(),
        lifecycle ?? runtime.GetRequiredService<IPlanLifecycleService>(), origins ?? runtime.GetRequiredService<IPlanOriginProvider>(),
        runtime.GetRequiredService<RendererFactory>());

    private async Task<BrowserCapture> Capture(ServiceProvider runtime)
    {
        var result = await _fixture.CreateModule(runtime).ReadDetailAsync(new(42));
        result.Failure.ShouldBeNull();
        return result.Capture!;
    }

    private async Task<BrowserReviewObservation> Open(ServiceProvider runtime, BrowserReviewModule module, string? file)
    {
        var result = await module.OpenAsync(file, await Capture(runtime));
        result.Failure.ShouldBeNull();
        return result.Value!;
    }

    private async Task<string> WriteProposal(string name, string title, string body)
    {
        var file = Path.Combine(_fixture.Paths.StartDir!, name);
        // Fixture strings contain no JSON metacharacters. Production parsing/canonicalisation is native.
        await File.WriteAllTextAsync(file, $$"""
            {
              "version": 1,
              "workspace": { "organization": "fixture", "project": "Work" },
              "operations": [
                { "id": "edit", "kind": "batch", "workItemId": 42, "expectedRevision": 3,
                  "fields": { "System.Title": "{{title}}", "System.Description": "<p>{{body}}</p>", "System.AssignedTo": null } }
              ]
            }
            """);
        return file;
    }
}
