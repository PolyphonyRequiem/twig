using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Browser;

[Trait("Category", "HostInventory")]
public sealed class BrowserEditingModuleTests : IAsyncLifetime
{
    private readonly NativeBrowserFixture _fixture = new();
    public Task InitializeAsync() => _fixture.InitializeAsync();
    public async Task DisposeAsync() => await _fixture.DisposeAsync();

    [HostMigrationFact]
    public async Task StateAndAccompanyingFieldsSurviveRuntimeRestartWithoutDuplicateRetry()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var module = Module(runtime);
        var session = await Open(runtime, module);
        var changes = new Dictionary<string, string>
        {
            ["system.title"] = "Retained title",
            ["SYSTEM.STATE"] = "Doing",
            ["system.assignedto"] = "Person",
        };
        var saved = await module.StageAsync(session, changes);
        saved.Failure.ShouldBeNull();
        saved.Value.ShouldBeUnionCase<Saved>();
        (await module.StageAsync(session, changes)).Value.ShouldBeUnionCase<Saved>();
        session.CurrentValues["System.Title"].ShouldBe("Retained title");
        runtime.Dispose();

        var restarted = await _fixture.CreateRuntimeAsync();
        var restored = await Open(restarted, Module(restarted));
        restored.CurrentValues["System.Title"].ShouldBe("Retained title");
        restored.CurrentValues["System.State"].ShouldBe("Doing");
        restored.CurrentValues["System.AssignedTo"].ShouldBe("Person");
        restored.HasLocalChanges.ShouldBeTrue();
        var pending = await restarted.GetRequiredService<IPendingChangeStore>().GetChangesAsync(42);
        pending.ShouldBe(new PendingChangeRecord[]
        {
            new(42, "state", "System.State", "To Do", "Doing"),
            new(42, "field", "System.Title", "Original", "Retained title"),
            new(42, "field", "System.AssignedTo", null, "Person"),
        });
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task UninitializedSeedStagesStateAndFieldsAtomicallyAndRestoresAfterRestart()
    {
        const int seedId = -1;
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(new Twig.Domain.Aggregates.WorkItem
        {
            Id = seedId,
            IsSeed = true,
            Type = WorkItemType.Task,
            Title = "Draft seed",
            State = string.Empty,
        });
        var module = Module(runtime);
        var session = await Open(runtime, module, seedId);
        var changes = new Dictionary<string, string>
        {
            ["System.Title"] = "Retained seed title",
            ["System.State"] = "To Do",
            ["System.AssignedTo"] = "Person",
        };

        var result = await module.StageAsync(session, changes);
        result.Failure.ShouldBeNull();
        result.Value.ShouldBeUnionCase<Saved>();
        (await module.StageAsync(session, changes)).Value.ShouldBeUnionCase<Saved>();
        runtime.Dispose();

        var restarted = await _fixture.CreateRuntimeAsync();
        var restored = await Open(restarted, Module(restarted), seedId);
        restored.CurrentValues["System.Title"].ShouldBe("Retained seed title");
        restored.CurrentValues["System.State"].ShouldBe("To Do");
        restored.CurrentValues["System.AssignedTo"].ShouldBe("Person");
        (await restarted.GetRequiredService<IPendingChangeStore>().GetChangesAsync(seedId))
            .ShouldBe(new PendingChangeRecord[]
            {
                new(seedId, "state", "System.State", string.Empty, "To Do"),
                new(seedId, "field", "System.Title", "Draft seed", "Retained seed title"),
                new(seedId, "field", "System.AssignedTo", null, "Person"),
            });
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task ServedHeaderFieldsStageLocallyWithoutAddingAbsentPageFields()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var provider = Substitute.For<IFormLayoutProvider>();
        provider.GetFormLayoutAsync("Task", Arg.Any<CancellationToken>()).Returns(Task.FromResult<FormLayoutResult>(
            new FormLayoutResult.Served(new FormLayout("Task", "process", [])
            {
                SystemControls =
                [
                    new("System.Title", "", "FieldControl", false, true, false),
                    new("System.State", "State", "FieldControl", false, true, false),
                    new("System.AssignedTo", "Assigned", "FieldControl", false, true, false),
                ],
            })));
        var module = new BrowserEditingModule(runtime.GetRequiredService<BrowserOperationModule>(),
            runtime.GetRequiredService<IWorkItemRepository>(), runtime.GetRequiredService<IPendingChangeStore>(),
            new DetailDocumentSource(provider), runtime.GetRequiredService<IProcessConfigurationProvider>());
        var session = await Open(runtime, module);

        session.Document.Pages.ShouldBeEmpty();
        var result = await module.StageAsync(session, new Dictionary<string, string>
        {
            ["System.Title"] = "Header draft",
            ["System.State"] = "Doing",
            ["System.AssignedTo"] = "Person",
        });

        result.Failure.ShouldBeNull();
        result.Value.ShouldBeUnionCase<Saved>();
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(42)).ShouldBe(new PendingChangeRecord[]
        {
            new(42, "state", "System.State", "To Do", "Doing"),
            new(42, "field", "System.Title", "Original", "Header draft"),
            new(42, "field", "System.AssignedTo", null, "Person"),
        });
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task ConcurrentPendingNoteRefusesWholeSaveWithoutOverwritingOrLosingUserBuffer()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var module = Module(runtime);
        var session = await Open(runtime, module);
        var store = runtime.GetRequiredService<IPendingChangeStore>();
        await store.AddChangeAsync(42, "note", null, null, "Keep this note");
        var buffer = new Dictionary<string, string> { ["System.Title"] = "Unsaved title" };
        var result = await module.StageAsync(session, buffer);
        result.Value.ShouldBeUnionCase<Refused>();
        buffer["System.Title"].ShouldBe("Unsaved title");
        session.CurrentValues["System.Title"].ShouldBe("Original");
        (await store.GetChangesAsync(42)).ShouldBe(new PendingChangeRecord[] { new(42, "note", null, null, "Keep this note") });
    }

    [HostMigrationFact]
    public async Task SameRevisionCacheChangeRefusesInsteadOfAdoptingNewBaseline()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        var repository = runtime.GetRequiredService<IWorkItemRepository>();
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var module = Module(runtime);
        var session = await Open(runtime, module);
        await repository.SaveAsync(NativeBrowserFixture.Item(42, "Changed at same revision"));
        var result = await module.StageAsync(session, new Dictionary<string, string> { ["System.Title"] = "My draft" });
        result.Value.ShouldBeUnionCase<Refused>();
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(42)).ShouldBeEmpty();
        session.CurrentValues["System.Title"].ShouldBe("Original");
    }

    [HostMigrationFact]
    public async Task FailedSaveAndUnsupportedFieldRetainBufferUntilSuccessfulRetry()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var real = runtime.GetRequiredService<IPendingChangeStore>();
        var store = Substitute.For<IPendingChangeStore>();
        store.GetChangesAsync(42, Arg.Any<CancellationToken>()).Returns(call => real.GetChangesAsync(42, call.ArgAt<CancellationToken>(1)));
        var fail = true;
        store.AddChangesBatchAsync(42, Arg.Any<IReadOnlyList<(string ChangeType, string? FieldName, string? OldValue, string? NewValue)>>(), Arg.Any<CancellationToken>())
            .Returns(call => fail ? Task.FromException(new IOException("Durable store unavailable"))
                : real.AddChangesBatchAsync(42, call.ArgAt<IReadOnlyList<(string ChangeType, string? FieldName, string? OldValue, string? NewValue)>>(1), call.ArgAt<CancellationToken>(2)));
        var module = Module(runtime, store);
        var session = await Open(runtime, module);
        var buffer = new Dictionary<string, string> { ["System.Title"] = "Draft title" };
        (await module.StageAsync(session, buffer)).Value.ShouldBeUnionCase<Refused>();
        buffer["System.Title"].ShouldBe("Draft title");
        session.CurrentValues["System.Title"].ShouldBe("Original");
        (await real.GetChangesAsync(42)).ShouldBeEmpty();
        fail = false;
        buffer["Custom.Unsupported"] = "Do not silently drop";
        (await module.StageAsync(session, buffer)).Value.ShouldBeUnionCase<Refused>();
        (await real.GetChangesAsync(42)).ShouldBeEmpty();
        buffer.Remove("Custom.Unsupported");
        (await module.StageAsync(session, buffer)).Value.ShouldBeUnionCase<Saved>();
        session.CurrentValues["System.Title"].ShouldBe("Draft title");
        (await real.GetChangesAsync(42)).ShouldBe(new PendingChangeRecord[] { new(42, "field", "System.Title", "Original", "Draft title") });
    }

    [HostMigrationFact]
    public async Task ChangedAuthorityRefusesLocalStagingAndLeavesExistingPendingWorkIntact()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var store = runtime.GetRequiredService<IPendingChangeStore>();
        await store.AddChangeAsync(42, "field", "System.Title", "Original", "Existing local title");
        var module = Module(runtime);
        var session = await Open(runtime, module);
        var baseline = (await store.GetChangesAsync(42)).ToArray();
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var current = await benches.GetOrCreateDefaultAsync([]);
        await benches.AddSelectorAsync(current.Id, BenchSelector.ForItem(99));
        var result = await module.StageAsync(session, new Dictionary<string, string> { ["System.Title"] = "Wrong origin draft" });
        result.Failure!.Kind.ShouldBe(BrowserReadFailureKind.AuthorityInvalidated);
        var restarted = await _fixture.CreateRuntimeAsync();
        (await restarted.GetRequiredService<IPendingChangeStore>().GetChangesAsync(42)).ShouldBe(baseline);
        _fixture.Transport.Mutations.ShouldBe(0);
    }

    [HostMigrationFact]
    public async Task SuccessfulLocalCommitIsNotRolledBackOrForgottenWhenFinalBenchFenceRefuses()
    {
        var runtime = await _fixture.CreateRuntimeAsync();
        await runtime.GetRequiredService<IWorkItemRepository>().SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var real = runtime.GetRequiredService<IPendingChangeStore>();
        var benches = runtime.GetRequiredService<IBenchRepository>();
        var current = await benches.GetOrCreateDefaultAsync([]);
        var store = Substitute.For<IPendingChangeStore>();
        store.GetChangesAsync(42, Arg.Any<CancellationToken>()).Returns(call => real.GetChangesAsync(42, call.ArgAt<CancellationToken>(1)));
        store.AddChangesBatchAsync(42, Arg.Any<IReadOnlyList<(string ChangeType, string? FieldName, string? OldValue, string? NewValue)>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await real.AddChangesBatchAsync(42, call.ArgAt<IReadOnlyList<(string ChangeType, string? FieldName, string? OldValue, string? NewValue)>>(1), call.ArgAt<CancellationToken>(2));
                await benches.AddSelectorAsync(current.Id, BenchSelector.ForItem(99));
            });
        var module = Module(runtime, store);
        var session = await Open(runtime, module);
        var result = await module.StageAsync(session, new Dictionary<string, string> { ["System.Title"] = "Committed local draft" });
        result.Failure!.Kind.ShouldBe(BrowserReadFailureKind.AuthorityInvalidated);
        session.CurrentValues["System.Title"].ShouldBe("Committed local draft");
        var reopened = await Open(runtime, Module(runtime));
        reopened.CurrentValues["System.Title"].ShouldBe("Committed local draft");
        (await real.GetChangesAsync(42)).ShouldBe(new PendingChangeRecord[] { new(42, "field", "System.Title", "Original", "Committed local draft") });
    }

    private BrowserEditingModule Module(ServiceProvider runtime, IPendingChangeStore? store = null) => new(
        runtime.GetRequiredService<BrowserOperationModule>(), runtime.GetRequiredService<IWorkItemRepository>(),
        store ?? runtime.GetRequiredService<IPendingChangeStore>(), new DetailDocumentSource(null),
        runtime.GetRequiredService<IProcessConfigurationProvider>());

    private async Task<BrowserEditingSession> Open(ServiceProvider runtime, BrowserEditingModule module, int id = 42)
    {
        var capture = (await _fixture.CreateModule(runtime).ReadDetailAsync(new(id))).Capture!;
        var result = await module.OpenAsync(id, capture);
        result.Failure.ShouldBeNull();
        return result.Value!;
    }
}
