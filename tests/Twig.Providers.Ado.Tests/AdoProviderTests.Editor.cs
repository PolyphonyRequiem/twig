using Shouldly;
using Twig.Core;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Twig.Providers.Ado.Tests;

public sealed partial class AdoProviderTests
{
    [Fact]
    public async Task CompoundIncomingHierarchyReparentUsesTheChildRevisionAndBothVerifiedParents()
    {
        using var fixture = await Fixture.Create();
        fixture.Http.Items[3] = fixture.Http.Items[2].DeepClone().AsObject();
        fixture.Http.Items[3]["id"] = 3;
        static JsonObject Edge(string relation, int id) => new() { ["rel"] = relation,
            ["url"] = $"https://example.test/organization/_apis/wit/workItems/{id}" };
        fixture.Http.Items[1]["relations"] = new JsonArray(Edge("Native.ParentReverse", 2));
        fixture.Http.Items[2]["relations"] = new JsonArray(Edge("Native.ParentForward", 1));
        var source = (await fixture.Read(1, 2, 3)).Items.First(item => item.Key == fixture.Key(1));
        var proposal = await fixture.Proposal(new("reparent", OpKind.Batch, source.Key, source.Revision,
            FieldValues.Empty.Set("Custom.Empty", FieldValue.String("same atomic reparent")), links:
                [new(fixture.Key(2), "Native.ParentForward", false, true),
                 new(fixture.Key(3), "Native.ParentForward", false)]));
        (await fixture.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var applied = (await fixture.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        applied.Status.ShouldBe(OpStatus.Applied);
        (await fixture.Provider.VerifyAsync(proposal, applied, default)).Value.Status.ShouldBe(OpStatus.Verified);
        fixture.Http.Writes.ShouldBe(1);
        fixture.Http.Items[2]["relations"]!.AsArray().ShouldBeEmpty();
        fixture.Http.Items[3]["relations"]!.AsArray().Single()!["rel"]!.GetValue<string>().ShouldBe("Native.ParentForward");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompoundCloseGateRequiresExplicitAtomicFieldAlongsideRelationship(bool includeGate)
    {
        using var fixture = await Fixture.Create();
        fixture.Http.Defect = "creation-links";
        var source = (await fixture.Read(1, 2)).Items.First(item => item.Key == fixture.Key(1));
        var fields = FieldValues.Empty.Set("System.State", FieldValue.String(fixture.Http.Completed));
        if (includeGate) fields = fields.Set("Custom.Closure", FieldValue.String("exact authored close judgment"));
        var proposal = await fixture.Proposal(new("compound-close", OpKind.Batch, source.Key, source.Revision, fields,
            links: [new(fixture.Key(2), "Native.Association", true)]));
        var imported = await fixture.Provider.ImportAsync(proposal, proposal.Digest);
        imported.IsSuccess.ShouldBe(includeGate);
        if (!includeGate) { fixture.Http.Writes.ShouldBe(0); return; }
        var applied = (await fixture.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        applied.Status.ShouldBe(OpStatus.Applied);
        (await fixture.Provider.VerifyAsync(proposal, applied, default)).Value.Status.ShouldBe(OpStatus.Verified);
        fixture.Http.Writes.ShouldBe(1);
        fixture.Http.Items[1]["fields"]!["Custom.Closure"]!.GetValue<string>().ShouldBe("exact authored close judgment");
    }

    [Fact]
    public async Task CompoundDirectedMultiRemoveAndAddRetainsFieldsAndDescendingRawIndices()
    {
        using var fixture = await Fixture.Create();
        fixture.Http.Defect = "creation-links";
        fixture.Http.Items[3] = fixture.Http.Items[2].DeepClone().AsObject();
        fixture.Http.Items[3]["id"] = 3;
        static JsonObject Edge(string relation, int id) => new() { ["rel"] = relation,
            ["url"] = $"https://example.test/organization/_apis/wit/workItems/{id}" };
        fixture.Http.Items[1]["relations"] = new JsonArray(Edge("Native.DependencyForward", 2), Edge("Native.Association", 2));
        fixture.Http.Items[2]["relations"] = new JsonArray(Edge("Native.DependencyReverse", 1), Edge("Native.Association", 1));
        var source = (await fixture.Read(1, 2, 3)).Items.First(item => item.Key == fixture.Key(1));
        var fields = FieldValues.Empty.Set("Custom.Empty", FieldValue.String("compound edited"));
        var proposal = await fixture.Proposal(new("compound", OpKind.Batch, source.Key, source.Revision, fields, links:
            [new(fixture.Key(2), "Native.DependencyForward", true, true),
             new(fixture.Key(2), "Native.Association", true, true),
             new(fixture.Key(3), "Native.DependencyForward", false)]));
        (await fixture.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var applied = await fixture.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        applied.IsSuccess.ShouldBeTrue(applied.IsSuccess ? "" : applied.Error.Message);
        applied.Value.Status.ShouldBe(OpStatus.Applied);
        (await fixture.Provider.VerifyAsync(proposal, applied.Value, default)).Value.Status.ShouldBe(OpStatus.Verified);
        fixture.Http.Writes.ShouldBe(1);
        using var patch = JsonDocument.Parse(fixture.Http.LastPayload!);
        patch.RootElement.EnumerateArray().Where(entry => entry.GetProperty("op").GetString() == "remove")
            .Select(entry => entry.GetProperty("path").GetString()).ShouldBe(["/relations/1", "/relations/0"]);
        fixture.Http.Items[1]["fields"]!["Custom.Empty"]!.GetValue<string>().ShouldBe("compound edited");
        fixture.Http.Items[2]["relations"]!.AsArray().ShouldBeEmpty();
        fixture.Http.Items[3]["relations"]!.AsArray().Single()!["rel"]!.GetValue<string>().ShouldBe("Native.DependencyForward");
    }

    [Fact]
    public async Task CompoundBatchCannotHideMissingInverseBehindTheSourceReadback()
    {
        using var fixture = await Fixture.Create();
        fixture.Http.Defect = "creation-links";
        var source = (await fixture.Read(1, 2)).Items.First(item => item.Key == fixture.Key(1));
        var proposal = await fixture.Proposal(new("compound", OpKind.Batch, source.Key, source.Revision,
            FieldValues.Empty.Set("Custom.Empty", FieldValue.String("edited")),
            links: [new(fixture.Key(2), "Native.Association", true)]));
        (await fixture.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        fixture.Http.AfterWrite = () => { fixture.Http.Items[2]["relations"] = new JsonArray(); return Task.CompletedTask; };
        var applied = (await fixture.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        applied.Status.ShouldBe(OpStatus.Applied);
        var verified = (await fixture.Provider.VerifyAsync(proposal, applied, default)).Value;
        verified.Status.ShouldBe(OpStatus.Indeterminate);
        verified.Evidence.ShouldContain(value => value.Contains("incident completeness", StringComparison.Ordinal));
        fixture.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task EditorIdentityChoicesUseCanonicalIdsAndCachedNativeSchemaWithoutWrites()
    {
        using var fixture = await Fixture.Create();
        var captured = await fixture.Provider.CaptureEditorMetadataAsync(fixture.Connection, default);
        captured.IsSuccess.ShouldBeTrue(captured.IsSuccess ? "" : captured.Error.Message);
        var layouts = fixture.Http.LayoutRequests.Count;
        var paths = fixture.Http.ReadAccessPaths.Count;
        var choices = await fixture.Provider.EditorChoicesAsync(captured.Value, fixture.Http.Type, "Custom.Owner", "Same display", 10, default);
        choices.IsSuccess.ShouldBeTrue(choices.IsSuccess ? "" : choices.Error.Message);
        choices.Value.Items.Single().Value.ShouldBe(FieldValue.Identity(Fixture.Actor));
        choices.Value.Items.Single().Label.ShouldBe("Same display");
        fixture.Http.LayoutRequests.Count.ShouldBe(layouts);
        fixture.Http.ReadAccessPaths.Skip(paths).ShouldAllBe(path =>
            path.EndsWith("/connectionData") || path.Contains("/_apis/projects/") || path.EndsWith("/_apis/identities"));
        fixture.Http.Writes.ShouldBe(0);
        (await fixture.Stored()).Version.ShouldBe(0);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("denied")]
    [InlineData("invalid")]
    [InlineData("bounds")]
    [InlineData("foreign-capture")]
    public async Task EditorChoicesRefuseChangedAuthorityInvalidIdsAndForgedContext(string defect)
    {
        using var fixture = await Fixture.Create();
        var captured = (await fixture.Provider.CaptureEditorMetadataAsync(fixture.Connection, default)).Value;
        using var other = await Fixture.Create();
        if (defect == "actor") fixture.Access.Principal = fixture.Http.NativeActor = Fixture.Process;
        else fixture.Http.ChoiceDefect = defect;
        var choices = await fixture.Provider.EditorChoicesAsync(defect == "foreign-capture"
            ? (await other.Provider.CaptureEditorMetadataAsync(other.Connection, default)).Value : captured,
            fixture.Http.Type, "Custom.Owner", "query", defect == "bounds" ? 51 : 5, default);
        choices.IsSuccess.ShouldBeFalse();
        fixture.Http.Writes.ShouldBe(0);
    }
}
