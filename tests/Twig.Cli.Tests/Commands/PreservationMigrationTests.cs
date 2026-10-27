using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.Services.Plan;
using Twig.Domain.Capabilities;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Serialization;
using System.Text.Json;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class PreservationMigrationTests
{
    [Fact]
    public async Task SuccessfulMigrationPreservesSeedAndStandingThroughProductionReadersAfterReopen()
    {
        var persistentRoot = CanonicalTempRoot.Create("twig-preservation-deep-");
        Console.WriteLine($"retained-migration-fixture={persistentRoot}");
        await using var fixture = new NativeBrowserFixture(persistentRoot);
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var registry = runtime.GetServices<ISystemWorktreeRegistry>().ShouldHaveSingleItem();
        registry.ShouldBeSameAs(runtime.GetRequiredService<ISystemWorktreeRegistry>());
        runtime.GetRequiredService<IProviderTargetBindingStore>().ShouldBeSameAs(registry);
        runtime.GetRequiredService<IConnectionBindingService>().RegistryPath
            .ShouldBe(Path.Combine(fixture.Home, "system.db"));
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = runtime.GetRequiredService<IWorkItemRepository>();
        var context = runtime.GetRequiredService<IContextStore>();
        var history = runtime.GetRequiredService<INavigationHistoryStore>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Parent"));
        await items.SaveAsync(NativeBrowserFixture.Item(84, "Forward target"));
        var minted = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var created = new SeedFactory().CreateUnparented(
            "Retained local work", WorkItemType.Task,
            AreaPath.Parse(@"Work\Private").Value,
            IterationPath.Parse(@"Work\Later").Value, minted, "Author", parentId: 42);
        created.IsSuccess.ShouldBeTrue(created.Error);
        var seed = created.Value.WithSeedFields("Edited local title", new Dictionary<string, string?>
        {
            ["System.Description"] = "<p>Unpublished body</p>",
            ["Custom.Empty"] = "",
            ["Custom.Null"] = null,
            ["Custom.Value"] = "retained",
        });
        await items.SaveAsync(seed);
        await runtime.GetRequiredService<IPendingChangeStore>()
            .AddChangeAsync(seed.Id, "note", null, null, "Unpublished note");
        await runtime.GetRequiredService<ISeedLinkRepository>().AddLinkAsync(
            new SeedLink(seed.Id, 42, SeedLinkTypes.Related, DateTimeOffset.UtcNow));
        var historical = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var intents = runtime.GetRequiredService<IPublishIntentRepository>();
        await intents.RecordIntentAsync(historical.Identity, "Previously published", "Task");
        await intents.CompleteIntentAsync(historical.Identity, 84);
        await runtime.GetRequiredService<IPublishIdMapRepository>().RecordMappingAsync(historical.Identity, 84);
        await runtime.GetRequiredService<IStagedIdentityRegistry>().RetireAsync(historical.Identity);
        var edited = NativeBrowserFixture.Item(77, "Pending edit");
        edited.UpdateField("Custom.Value", "unpublished");
        await items.SaveAsync(edited);
        var localStore = runtime.GetRequiredService<ILocalWorkItemStore>();
        var rich = await localStore.CreateAsync(FieldValues.Empty.Set("Local.Content", new TextValue("original")));
        var richUpdated = (await localStore.UpdateAsync(rich.Id, rich.Revision,
            rich.Fields.Set("Local.Content", new TextValue("retained")))).Value.ShouldBeOfType<LocalWriteApplied>().Record;
        var toDelete = await localStore.CreateAsync(FieldValues.Empty.Set("Local.Content", new TextValue("tombstone")));
        var tombstone = (await localStore.DeleteAsync(toDelete.Id, toDelete.Revision))
            .Value.ShouldBeOfType<LocalWriteApplied>().Record;
        var originalLocals = new List<LocalWorkItemRecord> { richUpdated, tombstone };
        var identities = runtime.GetRequiredService<IExternalIdentityMap>();
        foreach (var id in new[] { 42, 84, 77 })
        {
            var local = await identities.FindAsync(new ExternalWorkItemIdentity(
                "ado", ((SqliteWorkItemRepository)items).DurableScope!, id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            local.ShouldNotBeNull();
            var record = await localStore.GetAsync(local.Value);
            record.ShouldNotBeNull();
            originalLocals.Add(record);
        }
        var pending = runtime.GetRequiredService<IPendingChangeStore>();
        await pending.AddChangeAsync(77, "field", "Custom.Value", null, "");
        await pending.AddChangeAsync(77, "field", "Custom.Value", "", null);
        var proposal = Path.Combine(fixture.Paths.TwigDir, "retained-proposal.json");
        await File.WriteAllTextAsync(proposal,
            "{\"version\":1,\"workspace\":{\"organization\":\"fixture\",\"project\":\"Work\"},\"operations\":[{\"id\":\"retained\",\"kind\":\"batch\",\"workItemId\":42,\"expectedRevision\":3,\"fields\":{\"System.Title\":\"Retained intent\"}}]}");
        var journal = (await runtime.GetRequiredService<IPlanLifecycleService>().PreviewAsync(proposal)).Digest!;
        await runtime.GetRequiredService<IPlanJournalRepository>().SaveOperationErrorAsync(
            journal, "retained", "Preserved unknown outcome", PlanOperationState.Indeterminate, DateTimeOffset.UtcNow);
        await context.SetActiveWorkItemIdAsync(seed.Id);
        await context.SetValueAsync("workspace_mode", "Manual");
        await context.SetValueAsync("area_tree_json", "{\"opaque\":[null,\"\",7]}");
        await context.SetValueAsync("custom.standing.preference", "{\"opaque\":[false,null,\"user value\"]}");
        await history.RecordVisitAsync(42);
        await history.RecordVisitAsync(seed.Id);
        await history.RecordVisitAsync(seed.Id);
        await history.RecordVisitAsync(84);
        (await history.GoBackAsync()).ShouldBe(seed.Id);
        var expectedHistory = await history.GetHistoryAsync();
        var expectedSeed = await ((SqliteWorkItemRepository)items).GetSnapshotByIdAsync(seed.Id);
        var scope = ((SqliteWorkItemRepository)items).DurableScope!;
        var before = new StoreParityHarness(store, scope, preserveOpaqueContext: true).Inspect();
        before.IsConsistent.ShouldBeTrue();
        before.Classes.Single(entry => entry.ClassName == "pending.local_work_items").RowCount.ShouldBe(originalLocals.Count);
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(),
            scope);

        var preview = migration.Preview();
        var result = await migration.ApplyAsync(preview.Digest);

        result.State.ShouldBe("active");
        Directory.Exists(result.BackupDirectory).ShouldBeTrue();
        var localId = migration.FindSeedLocalId(minted.Identity);
        localId.ShouldNotBeNull();
        localId.Value.Value.ShouldNotBe(minted.Identity.Value);
        (await migration.ApplyAsync(preview.Digest)).ShouldBe(result);
        migration.FindSeedLocalId(minted.Identity).ShouldBe(localId);
        foreach (var original in originalLocals)
            (await localStore.GetAsync(original.Id)).ShouldBe(original);
        var after = new StoreParityHarness(store, scope, preserveOpaqueContext: true).Inspect();
        after.IsConsistent.ShouldBeTrue();
        foreach (var original in before.Classes.Where(entry =>
                     entry.ClassName != "pending.local_work_items"
                     && entry.ClassName != "pending.seed_work_items"
                     && entry.ClassName != "pending.seed_local_identities"
                     && entry.ClassName != "pending.workspace_context"
                     && entry.ClassName != "pending.workspace_navigation_history"
                     && entry.ClassName != "pending.preservation_migrations"
                     && entry.ClassName != "pending.workspace-navigation-sequence"))
        {
            var retained = after.Classes.Single(entry => entry.ClassName == original.ClassName);
            retained.RowCount.ShouldBe(original.RowCount);
            retained.CanonicalDigest.ShouldBe(original.CanonicalDigest);
        }
        runtime.Dispose();

        var reopened = await fixture.CreateRuntimeAsync(initializeBench: false);
        var reopenedItems = reopened.GetRequiredService<IWorkItemRepository>();
        var reopenedContext = reopened.GetRequiredService<IContextStore>();
        var reopenedHistory = reopened.GetRequiredService<INavigationHistoryStore>();
        foreach (var original in originalLocals)
            (await reopened.GetRequiredService<ILocalWorkItemStore>().GetAsync(original.Id)).ShouldBe(original);
        var actualSeed = await ((SqliteWorkItemRepository)reopenedItems).GetSnapshotByIdAsync(seed.Id);
        actualSeed.ShouldNotBeNull();
        expectedSeed.ShouldNotBeNull();
        actualSeed.Fields.ShouldBe(expectedSeed.Fields);
        (actualSeed with { Fields = expectedSeed.Fields }).ShouldBe(expectedSeed);
        (await reopenedItems.GetSeedsAsync()).ShouldHaveSingleItem().Id.ShouldBe(seed.Id);
        (await reopenedContext.GetActiveWorkItemIdAsync()).ShouldBe(seed.Id);
        (await reopenedContext.GetValueAsync("workspace_mode")).ShouldBe("Manual");
        (await reopenedContext.GetValueAsync("area_tree_json")).ShouldBe("{\"opaque\":[null,\"\",7]}");
        (await reopenedContext.GetValueAsync("custom.standing.preference"))
            .ShouldBe("{\"opaque\":[false,null,\"user value\"]}");
        var actualHistory = await reopenedHistory.GetHistoryAsync();
        actualHistory.Entries.ShouldBe(expectedHistory.Entries);
        actualHistory.CursorEntryId.ShouldBe(expectedHistory.CursorEntryId);
        (await reopenedHistory.GoForwardAsync()).ShouldBe(84);
        (await reopened.GetRequiredService<IPendingChangeStore>().GetChangesAsync(seed.Id))
            .ShouldHaveSingleItem().NewValue.ShouldBe("Unpublished note");
        new PreservationMigrationService(reopened.GetRequiredService<SqliteCacheStore>(), fixture.CurrentPaths(), scope)
            .Rollback(preview.Digest).State.ShouldBe("rolled-back");
        reopened.Dispose();
        var rolledBack = await fixture.CreateRuntimeAsync(initializeBench: false);
        (await rolledBack.GetRequiredService<IContextStore>().GetValueAsync("custom.standing.preference"))
            .ShouldBe("{\"opaque\":[false,null,\"user value\"]}");
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

}
