using Shouldly;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Persistence;
using Xunit;
using CoreModel = Twig.Core;

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
        var (paths, _, _) = await RelationshipWorkspaceAsync(activate: false);
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
        var (paths, connection, _) = await InitializeCurrentWorkspaceFixtureAsync(Path.Combine(_temp, "other-root"));
        connection.ShouldBe(first.Connection);
        RunCoreLink(paths, ["update", "title", "must not replace unresolved effect", "--id", "42", "-o", "json"]).ShouldBe(1);
        _transport.PatchAttempts.ShouldBe(1, "other-root normal save must pass the real global Core fence");
    }

    private async Task ActivateNativeSafetyFixtureAsync(TwigPaths paths)
    {
        var document = System.Text.Json.JsonSerializer.Deserialize(await File.ReadAllTextAsync(
            Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)), CoreWorkspaceJsonContext.Default.CoreWorkspaceDocument)!;
        var source = new NativeCoreStoreReference(document.DatabaseInstance, document.DatabasePath, paths.RepoRoot, document.Connection);
        (await NativeCoreExecution.RegisterAsync(_bindings.RegistryPath, source, default)).IsSuccess.ShouldBeTrue();
        var inventory = await NativeCoreExecution.InventoryAsync(_bindings.RegistryPath, [paths.RepoRoot], [source]);
        inventory.IsSuccess.ShouldBeTrue(inventory.IsSuccess ? "" : inventory.Error.Message);
        var activated = await NativeCoreExecution.ActivateAsync(_bindings.RegistryPath, inventory.Value,
            inventory.Value.Digest, completeInventoryConfirmed: true, oldProcessesQuiesced: true);
        activated.IsSuccess.ShouldBeTrue(activated.IsSuccess ? "" : activated.Error.Message);
    }
}
