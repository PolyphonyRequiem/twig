using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Services;
using Twig.Providers.Native;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class NativeTwigSpineTests
{
    [Fact]
    public async Task Fresh_native_setup_edits_and_publishes_through_guarded_workspace_then_reopens()
    {
        var root = Path.Combine(Path.GetTempPath(), "twig-native-spine-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        var work = Path.Combine(root, "work");
        Directory.CreateDirectory(work);
        try
        {
            InitCommandTestFixture.InitTempWorktree(work).ShouldBeTrue();
            var initialized = await new NativeConnectionSetup(home).InitializeFreshAsync(new(work, new NativeTwigSetupTarget()));
            initialized.IsSuccess.ShouldBeTrue(initialized.IsSuccess ? "" : initialized.Error.Message);
            var stamp = initialized.Value.Source;
            stamp.Connection.Provider.Value.ShouldBe("twig");
            var configuration = new TwigConfiguration { Provider = "twig" };
            var paths = TwigPaths.BuildPaths(Path.Combine(work, ".twig"), configuration, work);
            using var bindings = new ConnectionBindingService(home);
            var inventory = await NativeCoreExecution.InventoryAsync(bindings.RegistryPath);
            inventory.IsSuccess.ShouldBeTrue(inventory.IsSuccess ? "" : inventory.Error.Message);
            var activated = await NativeCoreExecution.ActivateAsync(bindings.RegistryPath, inventory.Value,
                inventory.Value.Digest, completeInventoryConfirmed: true, oldProcessesQuiesced: true);
            activated.IsSuccess.ShouldBeTrue(activated.IsSuccess ? "" : activated.Error.Message);
            var factory = new CoreWorkspaceFactory(configuration, paths, bindings, Substitute.For<IAuthenticationProvider>());
            var opened = await factory.OpenAsync(default);
            opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
            using var handle = opened.Value;
            handle.Source.ShouldBe(stamp);
            var fields = FieldValues.Empty.Set(NativeProvider.Title, FieldValue.String("First native item"))
                .Set(NativeProvider.StateField, FieldValue.String("Open"));
            var seed = await handle.Workspace.StageSeedAsync(NativeProvider.ItemType, fields, default);
            seed.IsSuccess.ShouldBeTrue(seed.IsSuccess ? "" : seed.Error.Message);
            seed.Value.Key.IsLocal.ShouldBeTrue();
            var preview = await handle.Workspace.PreviewDraftAsync(seed.Value.Key, default);
            preview.IsSuccess.ShouldBeTrue(preview.IsSuccess ? "" : preview.Error.Message);
            var saved = await handle.Workspace.SaveDraftAsync(preview.Value, preview.Value.Proposal!.Digest, default);
            saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
            saved.Value.Error.ShouldBeNull();
            saved.Value.EffectsVerified.ShouldBeTrue();
            var snapshot = (await handle.Workspace.SnapshotAsync(default)).Value;
            var published = snapshot.Mappings.Single().Remote;
            published.IsLocal.ShouldBeFalse();
            using var reopened = (await factory.OpenAsync(default)).Value;
            reopened.Source.ShouldBe(stamp);
            var read = await reopened.Workspace.ReadAsync(published, default);
            read.IsSuccess.ShouldBeTrue(read.IsSuccess ? "" : read.Error.Message);
            read.Value.Fields[NativeProvider.Title].Text.ShouldBe("First native item");
            var editor = await reopened.Workspace.CaptureEditorMetadataAsync(default);
            editor.IsSuccess.ShouldBeTrue();
            editor.Value.Schema.Connection.ShouldBe(stamp.Connection);
            var edited = await reopened.Workspace.StageEditAsync(published,
                FieldValues.Empty.Set(NativeProvider.StateField, FieldValue.String("Closed")), default);
            edited.IsSuccess.ShouldBeTrue();
            var editPreview = (await reopened.Workspace.PreviewDraftAsync(published, default)).Value;
            var edit = await reopened.Workspace.SaveDraftAsync(editPreview, editPreview.Proposal!.Digest, default);
            edit.IsSuccess.ShouldBeTrue(edit.IsSuccess ? "" : edit.Error.Message);
            edit.Value.EffectsVerified.ShouldBeTrue();
            (await reopened.Workspace.ReadAsync(published, default)).Value.Fields[NativeProvider.StateField].Text.ShouldBe("Closed");
            var adapter = new CoreCommandAdapter(factory, new RendererFactory(),
                Substitute.For<IConsoleInput>(), Substitute.For<IEditorLauncher>());
            var selected = await StdoutCapture.RunAsync(() => adapter.SetAsync("First native item", "human", default));
            selected.result.ShouldBe(0);
            selected.stdout.ShouldContain("First native item");
            var queried = await StdoutCapture.RunAsync(() => adapter.QueryAsync(null, "First", null, null,
                "Closed", null, null, null, null, null, 10, "json", default));
            queried.result.ShouldBe(0);
            queried.stdout.ShouldContain("First native item");
            var unsupported = await StdoutCapture.RunAsync(() => adapter.QueryAsync(null, null, null, null,
                null, null, "tracker-path", null, null, null, 10, "json", default));
            unsupported.result.ShouldBe(2);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, true);
        }
    }
}
