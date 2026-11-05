using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativeNormalRelationshipCaptureSurvivesReopenAndSavePublishesOneExactCompound()
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Editor", []), 0)).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "43");
        var opened = await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default);
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        using (var editor = opened.Value)
        {
            var item = await editor.SelectAsync(key, default);
            item.IsSuccess.ShouldBeTrue(item.IsSuccess ? "" : item.Error.Message);
            var staged = await editor.StageAsync(item.Value,
                FieldValues.Empty.Set("System.Title", FieldValue.String("exact normal title")), null,
                [new(other, "Native.Association", true)], default);
            staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
            _transport.PatchAttempts.ShouldBe(0);
            staged.Value.Capture.ShouldNotBeNull().Ready.ShouldBeTrue();
        }
        using var reopened = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var captured = await reopened.SelectAsync(key, default);
        captured.IsSuccess.ShouldBeTrue(captured.IsSuccess ? "" : captured.Error.Message);
        UserBenchStore.ParseIntent(captured.Value.Capture!.IntentJson).Operations.Single().Links.Length.ShouldBe(1);
        var saved = await reopened.SaveAsync(captured.Value, default);
        saved.IsSuccess.ShouldBeTrue(saved.IsSuccess ? "" : saved.Error.Message);
        saved.Value.Error.ShouldBeNull();
        saved.Value.EffectsVerified.ShouldBeTrue();
        _transport.PatchAttempts.ShouldBe(1);
        _transport.TitleOf(42).ShouldBe("exact normal title");
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.ShouldBeEmpty();
    }

    [Fact]
    public async Task NativePlanCaptureIsIsolatedAndModeExitFencesStaleHandleBeforeWrite()
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Isolation", []), 0)).Value;
        var library = (await user.ReadAsync()).Value;
        using var origin = (await factory.OpenAsync(default)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        using var normal = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var first = (await normal.SelectAsync(key, default)).Value;
        (await normal.StageAsync(first, FieldValues.Empty.Set("System.Title", FieldValue.String("normal only")), null, null, default))
            .IsSuccess.ShouldBeTrue();
        var mode = (await user.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, 0, true)).Value;
        var planned = await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default);
        planned.IsSuccess.ShouldBeTrue(planned.IsSuccess ? "" : planned.Error.Message);
        using var plan = planned.Value;
        var item = (await plan.SelectAsync(key, default)).Value;
        var staged = await plan.StageAsync(item, FieldValues.Empty.Set("System.Title", FieldValue.String("Plan only")), null, null, default);
        staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
        (await native.Store.ReadAsync(native.Connection, default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe("normal only");
        (await plan.Workspace.SnapshotAsync(default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe("Plan only");
        _transport.PatchAttempts.ShouldBe(0);
        (await plan.SaveAsync(staged.Value, default)).IsSuccess.ShouldBeFalse();
        (await user.SetPlanModeAsync(library.InstanceId, bench.Id, bench.Revision, plan.Plan.Revision, false)).IsSuccess.ShouldBeTrue();
        var refused = await plan.StageAsync(staged.Value, FieldValues.Empty.Set("System.Title", FieldValue.String("stale")), null, null, default);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Code.ShouldBe(ErrorCode.Conflict);
        _transport.PatchAttempts.ShouldBe(0);
        using var fresh = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value;
        var normalItem = (await fresh.SelectAsync(key, default)).Value;
        var save = await fresh.SaveAsync(normalItem, default);
        save.IsSuccess.ShouldBeTrue(save.IsSuccess ? "" : save.Error.Message);
        save.Value.Error.ShouldBeNull();
        _transport.TitleOf(42).ShouldBe("normal only");
        (await plan.Workspace.SnapshotAsync(default)).Value.Drafts.Single().Changes["System.Title"].Text.ShouldBe("Plan only");
    }

    private ServiceProvider EditorServices(string root)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(root, []);
        return services.BuildServiceProvider();
    }
}
