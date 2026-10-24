using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class DraftCommandTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();

    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [HostMigrationFact]
    public async Task SetAndShow_UseSharedStableIdentityAndStructuredOutput()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>()
            .SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var setOutput = new StringWriter();
        var set = Command(runtime, setOutput);

        (await set.SetAsync(42, "System.Title", "Updated title", "json")).ShouldBe(0);
        using var setJson = JsonDocument.Parse(setOutput.ToString());
        setJson.RootElement.GetProperty("generation").GetInt64().ShouldBe(1);
        setJson.RootElement.GetProperty("baselineRevision").GetInt64().ShouldBe(3);
        setJson.RootElement.GetProperty("status").GetString().ShouldBe("staged");
        setJson.RootElement.GetProperty("changes")[0].GetProperty("proposedValue").GetString()
            .ShouldBe("Updated title");

        var read = await _fixture.CreateModule(runtime).ReadDetailAsync(new(42, Ansi: false, Document: true));
        read.Failure.ShouldBeNull();
        var draft = await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(read.Observation!.Document!.ItemKey);
        draft.ShouldNotBeNull();
        draft!.ItemKey.ShouldBe(read.Observation.Document.ItemKey);
        draft.Changes.ShouldBe(new[] { new DraftFieldChange("System.Title", "Original", "Updated title") });

        var showOutput = new StringWriter();
        (await Command(runtime, showOutput).ShowAsync(42, "minimal")).ShouldBe(0);
        showOutput.ToString().Trim().ShouldBe("42\t1\t3\t3\t3\tstaged\t1");
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task RevisionDriftIsRecordedAndRefusesFurtherEditsWithoutDroppingIntent()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        (await Command(runtime, new StringWriter()).SetAsync(42, "System.Title", "My title")).ShouldBe(0);

        var changed = NativeBrowserFixture.Item(42, "Server title");
        changed.MarkSynced(4);
        await repository.SaveAsync(changed);
        var output = new StringWriter();
        (await Command(runtime, output).ShowAsync(42, "json")).ShouldBe(0);
        using var json = JsonDocument.Parse(output.ToString());
        json.RootElement.GetProperty("status").GetString().ShouldBe("conflicted");
        json.RootElement.GetProperty("observedRevision").GetInt64().ShouldBe(4);
        json.RootElement.GetProperty("conflicts")[0].GetProperty("observedValue").GetString()
            .ShouldBe("Server title");

        var before = await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(await KeyAsync(runtime, 42));
        before.ShouldNotBeNull();
        before!.Generation.ShouldBe(2);
        before.Changes.ShouldBe(new[] { new DraftFieldChange("System.Title", "Original", "My title") });
        before.Conflicts.ShouldBe(new[]
        {
            new DraftFieldConflict("System.Title", "Original", "Server title", "My title"),
        });

        var error = new StringWriter();
        var context = runtime.GetRequiredService<CommandContext>() with { Stderr = error };
        var command = Command(runtime, new StringWriter(), context);
        (await command.SetAsync(42, "System.AssignedTo", "Someone")).ShouldBe(1);
        error.ToString().ShouldContain("no longer match");
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task Set_RefusesUnsupportedFieldAndEmitsJsonError()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>()
            .SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var output = new StringWriter();
        var error = new StringWriter();
        var context = runtime.GetRequiredService<CommandContext>() with { Stderr = error };

        (await Command(runtime, output, context).SetAsync(42, "Custom.Unavailable", "value", "json"))
            .ShouldBe(1);
        using var json = JsonDocument.Parse(error.ToString());
        var message = json.RootElement.GetProperty("error").GetString();
        message.ShouldNotBeNull();
        message!.ShouldContain("absent from the native form");
        (await runtime.GetRequiredService<IRecoverableWorkItemDraftStore>()
            .GetAsync(await KeyAsync(runtime, 42))).ShouldBeNull();
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task Set_RefusesStaleGenerationWithoutRetryingOrOverwritingConcurrentDraft()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>()
            .SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var realStore = runtime.GetRequiredService<IRecoverableWorkItemDraftStore>();
        var racingStore = Substitute.For<IRecoverableWorkItemDraftStore>();
        racingStore.GetAsync(Arg.Any<QualifiedWorkItemKey>(), Arg.Any<CancellationToken>())
            .Returns(call => realStore.GetAsync(
                call.ArgAt<QualifiedWorkItemKey>(0), call.ArgAt<CancellationToken>(1)));
        racingStore.TrySaveAsync(Arg.Any<RecoverableWorkItemDraft>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(call => RaceWithConcurrentDraftAsync(call, realStore));
        var error = new StringWriter();
        var context = runtime.GetRequiredService<CommandContext>() with { Stderr = error };

        (await Command(runtime, new StringWriter(), context, racingStore)
            .SetAsync(42, "System.Title", "Requested title")).ShouldBe(1);
        error.ToString().ShouldContain("changed in another surface");

        var saved = await realStore.GetAsync(await KeyAsync(runtime, 42));
        saved.ShouldNotBeNull();
        saved!.Generation.ShouldBe(1);
        saved.Changes.ShouldBe(new[] { new DraftFieldChange("System.Title", "Original", "Concurrent title") });
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    private DraftCommand Command(
        ServiceProvider runtime,
        TextWriter output,
        CommandContext? context = null,
        IRecoverableWorkItemDraftStore? draftStore = null)
        => new(
            context ?? runtime.GetRequiredService<CommandContext>(),
            _fixture.CreateModule(runtime),
            runtime.GetRequiredService<BrowserEditingModule>(),
            runtime.GetRequiredService<BrowserOperationModule>(),
            draftStore ?? runtime.GetRequiredService<IRecoverableWorkItemDraftStore>(),
            runtime.GetRequiredService<RendererFactory>(),
            output);

    private static async Task<bool> RaceWithConcurrentDraftAsync(
        NSubstitute.Core.CallInfo call,
        IRecoverableWorkItemDraftStore realStore)
    {
        var requested = call.ArgAt<RecoverableWorkItemDraft>(0);
        var expectedGeneration = call.ArgAt<long?>(1);
        var cancellationToken = call.ArgAt<CancellationToken>(2);
        var concurrent = new RecoverableWorkItemDraft(
            requested.ItemKey,
            requested.BaselineRevision,
            requested.BaselineRevision,
            1,
            [new DraftFieldChange("System.Title", "Original", "Concurrent title")],
            []);
        (await realStore.TrySaveAsync(concurrent, expectedGeneration, cancellationToken)).ShouldBeTrue();
        return await realStore.TrySaveAsync(requested, expectedGeneration, cancellationToken);
    }

    private async Task<QualifiedWorkItemKey> KeyAsync(ServiceProvider runtime, int id)
    {
        var result = await _fixture.CreateModule(runtime).ReadDetailAsync(new(id, Ansi: false, Document: true));
        result.Failure.ShouldBeNull();
        return result.Observation!.Document!.ItemKey;
    }
}
