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
    [Theory]
    [InlineData("Native.Association", false)]
    [InlineData("Native.DependencyForward", true)]
    public async Task NativeCompoundEditorFieldsAndRelatedDeltaUseOnePatchAndVerifiedInverseReadback(string relation, bool policy)
    {
        var native = await RelationshipWorkspaceAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(native.Paths.StartDir, []);
        using var root = services.BuildServiceProvider();
        var opened = await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default);
        opened.IsSuccess.ShouldBeTrue(opened.IsSuccess ? "" : opened.Error.Message);
        using var handle = opened.Value;
        if (policy)
            await File.WriteAllTextAsync(native.Paths.PolicyPath,
                """{"version":1,"rules":[{"id":"acyclic","kind":"dependency-integrity","enforcement":"refuse","maxNodes":20}]}""");
        var key = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(native.Connection.Provider, native.Connection.ResourceScope, "43");
        var observed = await handle.Workspace.ReadAsync(key, default);
        observed.IsSuccess.ShouldBeTrue();
        (await handle.Workspace.ReadAsync(other, default)).IsSuccess.ShouldBeTrue();
        var prepared = await handle.Workspace.PrepareAsync([new("compound", OpKind.Batch, key, observed.Value.Revision,
            FieldValues.Empty.Set("System.Title", FieldValue.String("compound title")),
            links: [new(other, relation, true)])], default);
        prepared.IsSuccess.ShouldBeTrue(prepared.IsSuccess ? "" : prepared.Error.Message);
        var applied = await handle.Workspace.ApplyAsync(prepared.Value, prepared.Value.Digest, default);
        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? "" : applied.Error.Message);
        applied.Value.Error.ShouldBeNull();
        applied.Value.EffectsVerified.ShouldBeTrue();
        _transport.PatchAttempts.ShouldBe(1);
        _transport.CommittedWrites.ShouldBe(1);
        _transport.TitleOf(42).ShouldBe("compound title");
        var snapshot = (await native.Store.ReadAsync(native.Connection, default)).Value;
        snapshot.Relationships.ShouldContain(edge => edge.From == key && edge.To == other && edge.Kind == relation);
        if (policy)
        {
            var fresh = (await handle.Workspace.ReadAsync(other, default)).Value;
            var cycle = await handle.Workspace.PrepareAsync([new("cycle", OpKind.Batch, other, fresh.Revision,
                FieldValues.Empty.Set("System.Title", FieldValue.String("must not publish")),
                links: [new(key, relation, true)])], default);
            cycle.IsSuccess.ShouldBeFalse("native mechanical policy must evaluate the realized compound graph");
            _transport.PatchAttempts.ShouldBe(1);
        }
    }
}
