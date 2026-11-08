using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativePublicRelationshipIntentReopensWithoutScalarPublication()
    {
        var native = await RelationshipWorkspaceAsync();
        using var services = EditorServices(native.Paths.StartDir);
        var factory = services.GetRequiredService<CoreWorkspaceFactory>();
        using var origin = (await factory.OpenAsync(default)).Value;
        var user = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await user.SaveAsync(new(Guid.NewGuid(), "Relationship reopen", []), 0)).Value;
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "43");
        using (var capture = (await NativeEditor.OpenAsync(factory, user, bench.Id, origin.Source, default)).Value)
        {
            capture.Metadata.Schema.Relations.ShouldContain(new RelationKind("Native.DependencyForward", RelationShape.Dependency, true));
            capture.Workspace.Capabilities.AtomicRelationshipBatches.ShouldBeTrue();
            var endpoint = (await capture.Workspace.ReadAsync(other, default, capture.Metadata)).Value;
            endpoint.RelationshipsComplete.ShouldBeTrue();
            endpoint.Revision.ShouldNotBeNull().Item.ShouldBe(other);
            var selected = (await capture.SelectAsync(key, default)).Value;
            selected.Observation.RelationshipsComplete.ShouldBeTrue();
            selected.Observation.Revision.ShouldNotBeNull().Item.ShouldBe(key);
            var staged = await capture.StageAsync(selected, FieldValues.Empty, null,
                [new(other, "Native.DependencyForward", true)], default);
            staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
            staged.Value.Draft.ShouldNotBeNull().Links.ShouldBeEmpty();
            UserBenchStore.ParseIntent(staged.Value.Capture!.IntentJson).Operations.Single().Links
                .ShouldBe([new CreationLink(other, "Native.DependencyForward", true)]);
        }
        _transport.PatchAttempts.ShouldBe(0);
        using var editor = (await NativeItemEditor.OpenAsync(native.Paths.StartDir, bench.Id, origin.Source, ConfigureHumanEditor, default)).Value;
        var reopened = await editor.SelectAsync(key);
        reopened.IsSuccess.ShouldBeTrue(reopened.IsSuccess ? "" : reopened.Error.Message);
        var scalarSave = await editor.SaveAsync(reopened.Value.Ticket, FieldValues.Empty);
        scalarSave.IsSuccess.ShouldBeFalse("A scalar Save must not publish retained relationship intent.");
        _transport.PatchAttempts.ShouldBe(0);
        (await user.ReadEditorCapturesAsync(default)).Value.Single().Settled.ShouldBeFalse();
    }
}
