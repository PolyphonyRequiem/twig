using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Commands;
using Twig.Cli.Tests.Browser;
using Twig.Cli.Tests.TestSupport;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Serialization;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

[Trait("Category", "HostInventory")]
public sealed class WorkspaceMigrationCommandTests : IDisposable
{
    private readonly string _root = CanonicalTempRoot.Create("twig-workspace-migration-");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ProductionCommandReportsMovementThenExplicitlyCutsOverAndRollsBack()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Parent"));
        var staged = await runtime.GetRequiredService<IStagedIdentityRegistry>().MintAsync();
        var seed = new SeedFactory().CreateUnparented("Retained seed", WorkItemType.Task,
            AreaPath.Parse("Work").Value, IterationPath.Parse("Work").Value, staged, parentId: 42);
        seed.IsSuccess.ShouldBeTrue(seed.Error);
        await items.SaveAsync(seed.Value);
        var context = runtime.GetRequiredService<IContextStore>();
        await context.SetActiveWorkItemIdAsync(seed.Value.Id);
        await context.SetValueAsync("opaque.preference", "retained");
        await runtime.GetRequiredService<IPendingChangeStore>().AddChangeAsync(
            seed.Value.Id, "note", null, null, "Unpublished note");
        var history = runtime.GetRequiredService<INavigationHistoryStore>();
        await history.RecordVisitAsync(42);
        await history.RecordVisitAsync(seed.Value.Id);
        await history.RecordVisitAsync(seed.Value.Id);
        await history.GoBackAsync();
        var expectedHistory = await history.GetHistoryAsync();
        var scope = items.DurableScope.ShouldNotBeNull();
        runtime.Dispose();
        runtime = await fixture.CreateRuntimeAsync(initializeBench: false);
        store = runtime.GetRequiredService<SqliteCacheStore>();
        items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        items.DurableScope.ShouldBeNull();
        context = runtime.GetRequiredService<IContextStore>();
        history = runtime.GetRequiredService<INavigationHistoryStore>();
        var command = runtime.GetRequiredService<WorkspaceMigrationCommand>();
        var before = new StoreParityHarness(store, scope, preserveOpaqueContext: true).Inspect();

        var (humanExit, humanText) = await StdoutCapture.RunAsync(() => command.ExecuteAsync());
        humanExit.ShouldBe(0);
        humanText.ShouldContain("Preservation migration");
        var (previewExit, previewJson) = await StdoutCapture.RunAsync(() => command.ExecuteAsync(output: "json"));

        previewExit.ShouldBe(0);
        var preview = JsonSerializer.Deserialize(previewJson, TwigJsonContext.Default.PreservationMigrationReport);
        preview.ShouldNotBeNull();
        preview.Migration.State.ShouldBe("preview");
        preview.Migration.IntentId.ShouldBeNull();
        preview.CanApply.ShouldBeTrue();
        preview.UnsupportedWork.ShouldBeEmpty();
        preview.Movements.Single(row => row.Destination == "pending.seed_work_items").Rows.ShouldBe(1);
        preview.Movements.Single(row => row.Destination == "pending.workspace_navigation_history").Rows.ShouldBe(3);
        preview.Movements.ShouldContain(row => row.Source == "pending.pending_changes");
        preview.RollbackPreconditions.ShouldNotBeEmpty();
        Directory.Exists(preview.Migration.BackupDirectory).ShouldBeFalse();
        var after = new StoreParityHarness(store, scope, preserveOpaqueContext: true).Inspect();
        after.Classes.Select(row => (row.ClassName, row.RowCount, row.CanonicalDigest))
            .ShouldBe(before.Classes.Select(row => (row.ClassName, row.RowCount, row.CanonicalDigest)));

        var (applyExit, applyJson) = await StdoutCapture.RunAsync(() =>
            command.ExecuteAsync(confirm: preview.Migration.Digest, output: "json"));
        applyExit.ShouldBe(0);
        var applied = JsonSerializer.Deserialize(applyJson, TwigJsonContext.Default.PreservationMigrationReport)!;
        applied.Migration.State.ShouldBe("active");
        applied.CanApply.ShouldBeFalse();
        var local = new PreservationMigrationService(store, fixture.CurrentPaths(), scope)
            .FindSeedLocalId(staged.Identity);
        local.ShouldNotBeNull();
        local.Value.Value.ShouldNotBe(staged.Identity.Value);
        (await context.GetActiveWorkItemIdAsync()).ShouldBe(seed.Value.Id);
        (await context.GetValueAsync("opaque.preference")).ShouldBe("retained");
        (await history.GetHistoryAsync()).Entries.ShouldBe(expectedHistory.Entries);
        (await history.GetHistoryAsync()).CursorEntryId.ShouldBe(expectedHistory.CursorEntryId);
        (await runtime.GetRequiredService<IPendingChangeStore>().GetChangesAsync(seed.Value.Id))
            .ShouldHaveSingleItem().NewValue.ShouldBe("Unpublished note");

        var (rollbackExit, _) = await StdoutCapture.RunAsync(() =>
            command.ExecuteAsync(confirm: preview.Migration.Digest, rollback: true, output: "minimal"));
        rollbackExit.ShouldBe(0);
        new PreservedWorkspaceState(store).IsActive.ShouldBeFalse();
        File.Exists(Path.Combine(preview.Migration.BackupDirectory, "manifest.json")).ShouldBeTrue();
        (await context.GetValueAsync("opaque.preference")).ShouldBe("retained");
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Fact]
    public async Task InterruptedPreparationReportsIntentAndRequiresExactCancellationBeforeRereview()
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeAsync();
        var runtime = await fixture.CreateRuntimeAsync();
        var store = runtime.GetRequiredService<SqliteCacheStore>();
        var items = (SqliteWorkItemRepository)runtime.GetRequiredService<IWorkItemRepository>();
        await items.SaveAsync(NativeBrowserFixture.Item(42, "Original"));
        var migration = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!, phase =>
        {
            if (phase == "backup-qualified") throw new IOException("Owned interruption");
        });
        var preview = migration.Describe();
        await Should.ThrowAsync<IOException>(() => migration.ApplyAsync(preview.Migration.Digest));
        var preparing = migration.Describe();
        preparing.Migration.State.ShouldBe("preparing");
        preparing.Migration.IntentId.ShouldNotBeNull();
        await runtime.GetRequiredService<IContextStore>().SetValueAsync("opaque.preference", "new work");
        var command = runtime.GetRequiredService<WorkspaceMigrationCommand>();
        var (blockedExit, blockedJson) = await StdoutCapture.RunAsync(() => command.ExecuteAsync(output: "json"));
        blockedExit.ShouldBe(1);
        var blocked = JsonSerializer.Deserialize(blockedJson, TwigJsonContext.Default.PreservationMigrationReport)!;
        blocked.CanApply.ShouldBeFalse();
        blocked.UnsupportedWork.ShouldHaveSingleItem().ShouldStartWith("preservation-source-changed:");
        var (exit, json) = await StdoutCapture.RunAsync(() =>
            command.ExecuteAsync(confirm: preparing.Migration.Digest,
                cancelIntent: preparing.Migration.IntentId, output: "json"));
        exit.ShouldBe(0);
        JsonSerializer.Deserialize(json, TwigJsonContext.Default.PreservationMigrationReport)!
            .Migration.State.ShouldBe("cancelled");
        var reviewed = new PreservationMigrationService(store, fixture.CurrentPaths(), items.DurableScope!).Describe();
        reviewed.Migration.Digest.ShouldNotBe(preview.Migration.Digest);
        File.Exists(Path.Combine(preview.Migration.BackupDirectory, "manifest.json")).ShouldBeTrue();
        fixture.Transport.WorkMutationAttempts.ShouldBe(0);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "not-an-intent")]
    public async Task InvalidMutationArgumentsRefuseBeforeResolvingStorage(bool rollback, string? cancel)
    {
        var paths = new TwigPaths(_root, Path.Combine(_root, "config"), Path.Combine(_root, "twig.db"));
        var command = new WorkspaceMigrationCommand(
            () => throw new InvalidOperationException("Storage must remain unopened"),
            paths, new RendererFactory());
        var (exit, _) = await StdoutCapture.RunAsync(() =>
            command.ExecuteAsync(rollback: rollback, cancelIntent: cancel, output: "json"));
        exit.ShouldBe(2);
        File.Exists(paths.DbPath).ShouldBeFalse();
    }

    [Theory]
    [InlineData(0, "preservation-scope-unavailable:")]
    [InlineData(2, "preservation-scope-ambiguous:")]
    public async Task ColdScopeRequiresOneDurableObservationRatherThanAnInMemoryWarmup(int scopes, string code)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var identities = new SqliteLocalWorkItemRepository(store);
        for (var index = 0; index < scopes; index++)
            await identities.ResolveAsync(new ExternalWorkItemIdentity("ado",
                index == 0 ? "11111111-2222-3333-4444-555555555555" : "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                "42"), new ProviderRevisionToken("3"));
        Should.Throw<InvalidOperationException>(() => PreservationMigrationService.ReadCachedResourceScope(store))
            .Message.ShouldStartWith(code);
    }

    [Fact]
    public async Task UnadmittedStorageRefusesBeforeOpeningOrRebuildingOriginals()
    {
        var cache = Path.Combine(_root, "cache");
        Directory.CreateDirectory(cache);
        var paths = new TwigPaths(_root, Path.Combine(_root, "config"), Path.Combine(cache, "twig.db"));
        await File.WriteAllTextAsync(paths.DbPath, "Protected unadmitted original");
        var resolutions = 0;
        var command = new WorkspaceMigrationCommand(() =>
        {
            resolutions++;
            throw new InvalidOperationException("Legacy storage must remain unopened");
        }, paths, new RendererFactory());
        var (exit, _) = await StdoutCapture.RunAsync(() => command.ExecuteAsync(output: "json"));
        exit.ShouldBe(1);
        resolutions.ShouldBe(0);
        (await File.ReadAllTextAsync(paths.DbPath)).ShouldBe("Protected unadmitted original");
        File.Exists(Path.Combine(cache, "pending.db")).ShouldBeFalse();
    }

    [Fact]
    public async Task ProviderTargetRefusesWithoutOpeningAdoStorageOrCallingTheProvider()
    {
        InitCommandTestFixture.InitTempWorktree(_root).ShouldBeTrue();
        var twigDir = Path.Combine(_root, ".twig");
        Directory.CreateDirectory(twigDir);
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"), _root);
        var anchor = WorktreeAnchorDetector.Detect(_root)!.Value;
        var document = new ProviderTargetAttachmentDocument(
            ProviderTargetAttachmentDocument.CurrentSchema, 1, 1,
            WorktreeFingerprintProvider.CanonicalJson(anchor), DateTimeOffset.UtcNow.ToString("O"),
            ProviderTargetAttachmentDocument.StateAttached, ProviderTargetBindingId.New().ToString());
        await File.WriteAllTextAsync(Path.Combine(twigDir, ProviderTargetAttachmentDocument.FileName),
            JsonSerializer.Serialize(document, TwigJsonContext.Default.ProviderTargetAttachmentDocument));
        var resolutions = 0;
        var command = new WorkspaceMigrationCommand(() =>
        {
            resolutions++;
            throw new InvalidOperationException("ADO storage must remain unopened");
        }, paths, new RendererFactory());
        using var error = new StringWriter();
        var previous = Console.Error;
        Console.SetError(error);
        try
        {
            var (exit, _) = await StdoutCapture.RunAsync(() => command.ExecuteAsync(output: "json"));
            exit.ShouldBe(1);
            error.ToString().ShouldContain("preservation-provider-generation-unsupported:");
            using var errorJson = JsonDocument.Parse(error.ToString());
            errorJson.RootElement.GetProperty("error").GetString()
                .ShouldStartWith("preservation-provider-generation-unsupported:");
            resolutions.ShouldBe(0);
            File.Exists(paths.DbPath).ShouldBeFalse();
        }
        finally { Console.SetError(previous); }
    }
}
