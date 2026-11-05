using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Persistence;
using Twig.Cli.Tests.TestSupport;
using Xunit;
using CoreModel = Twig.Core;
using CleanStore = Twig.Store.Sqlite.SqliteStore;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData(409, true)]
    [InlineData(412, true)]
    [InlineData(503, false)]
    public async Task NativeHttpRejectionPermitsOnlyFreshReviewedNoEffectEdits(int status, bool definitive)
    {
        var native = await RelationshipWorkspaceAsync();
        _transport.RejectNextPatchStatus = (System.Net.HttpStatusCode)status;
        RunCoreLink(native.Paths, ["update", "title", "original rejected request", "--id", "42", "-o", "json"]).ShouldBe(1);
        var before = (await native.Store.ReadAsync(native.Connection, default)).Value;
        before.Journal.Single().Status.ShouldBe(definitive ? CoreModel.OpStatus.Failed : CoreModel.OpStatus.Indeterminate);
        var originalDigest = before.Journal.Single().Digest;
        RunCoreLink(native.Paths, ["update", "title", "fresh reviewed request", "--id", "42", "-o", "json"]).ShouldBe(definitive ? 0 : 1);
        _transport.PatchAttempts.ShouldBe(definitive ? 2 : 1);
        var after = (await native.Store.ReadAsync(native.Connection, default)).Value;
        var retained = after.Journal.Single(row => row.Digest == originalDigest);
        retained.Status.ShouldBe(before.Journal.Single().Status);
        retained.Attempt.ShouldBe(before.Journal.Single().Attempt);
        if (definitive) after.Journal.ShouldContain(row => row.Digest != originalDigest && row.Status == CoreModel.OpStatus.Verified);
    }

    [Fact]
    public async Task NativePublishingBeforeActivationIsRefusedByTheActualCliComposition()
    {
        var native = await AttachAsync();
        var config = native.GetRequiredService<TwigConfiguration>();
        var paths = native.GetRequiredService<TwigPaths>();
        _transport.CoreMetadataEnabled = true;
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(new("ado"), "11111111-1111-1111-1111-111111111111",
            "22222222-2222-2222-2222-222222222222", CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "activation-gate-core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        _transport.PatchAttempts.ShouldBe(0);
        RunCoreLink(paths, ["update", "title", "must stay unpublished before safety check", "--id", "42", "-o", "json"])
            .ShouldBe(1);
        _transport.PatchAttempts.ShouldBe(0, "no mutation HTTP before the real native activation gate");
    }

    [Fact]
    public async Task NativeUnknownWriteBlocksAnotherInitializedRootNormalSaveWithoutMutationHttp()
    {
        var first = await RelationshipWorkspaceAsync();
        _transport.FailReadbackAfterCommit = true;
        RunCoreLink(first.Paths, ["update", "title", "original unverified effect", "--id", "42", "-o", "json"]).ShouldBe(1);
        _transport.FailReadbackAfterCommit = false;
        (await first.Store.ReadAsync(first.Connection, default)).Value.Journal.Single().Status.ShouldBe(CoreModel.OpStatus.Indeterminate);
        _transport.PatchAttempts.ShouldBe(1);
        var root = Path.Combine(_temp, "other-root");
        Directory.CreateDirectory(root);
        InitCommandTestFixture.InitTempWorktree(root).ShouldBeTrue();
        var config = new TwigConfiguration { Organization = "fixture", Project = "Project" };
        var paths = TwigPaths.BuildPaths(Path.Combine(root, ".twig"), config, root);
        await config.SaveSplitAsync(paths);
        using (var attachment = new WorktreeLocalAttachmentStore(paths, config, TimeProvider.System))
            (await attachment.InitializeAsync()).IsSuccess.ShouldBeTrue();
        var anchor = WorktreeAnchorDetector.Detect(root)!.Value;
        (await _systemRegistry.UpsertWorktreeAsync(WorktreeFingerprintProvider.CanonicalJson(anchor),
            ConnectionRefResolver.Compute(config), root)).IsSuccess.ShouldBeTrue();
        var origin = await _bindings.ResolveAsync(config, paths);
        var connection = new CoreModel.Connection(first.Connection.Provider, first.Connection.ResourceScope,
            first.Connection.Destination, CleanCoreAdoAccess.Identity(origin, Actor));
        var database = Path.Combine(paths.TwigDir, "core.db");
        var store = (await CleanStore.CreateAsync(database)).Value;
        (await CoreWorkspaceFactory.RecordNewAsync(paths, store, database, connection, Actor, default)).IsSuccess.ShouldBeTrue();
        await ActivateNativeSafetyFixtureAsync(paths);
        RunCoreLink(paths, ["update", "title", "must not replace unresolved effect", "--id", "42", "-o", "json"]).ShouldBe(1);
        _transport.PatchAttempts.ShouldBe(1, "other-root normal save must pass the real global Core fence");
    }

    private async Task ActivateNativeSafetyFixtureAsync(TwigPaths paths, CleanStore? explicitStore = null, CoreModel.Connection? connection = null)
    {
        NativeCoreStoreReference source;
        if (explicitStore is not null && connection is not null)
            source = new(explicitStore.InstanceId, explicitStore.DatabasePath, paths.RepoRoot, connection);
        else
        {
            var document = System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllTextAsync(
                Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)), CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
            source = new(document.DatabaseInstance, document.DatabasePath, paths.RepoRoot, document.Connection);
        }
        (await NativeCoreExecution.RegisterAsync(_bindings.RegistryPath, source, default)).IsSuccess.ShouldBeTrue();
        var inventory = await NativeCoreExecution.InventoryAsync(_bindings.RegistryPath, [paths.RepoRoot], [source]);
        inventory.IsSuccess.ShouldBeTrue(inventory.IsSuccess ? "" : inventory.Error.Message);
        var activated = await NativeCoreExecution.ActivateAsync(_bindings.RegistryPath, inventory.Value,
            inventory.Value.Digest, completeInventoryConfirmed: true, oldProcessesQuiesced: true);
        activated.IsSuccess.ShouldBeTrue(activated.IsSuccess ? "" : activated.Error.Message);
    }
}
