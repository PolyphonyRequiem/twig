using System.Globalization;
using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Core;
using Twig.Domain.Interfaces;
using Twig.Infrastructure.Services;
using Twig.Providers.GitHub;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class GitHubCoreSpineTests
{
    [Fact]
    public async Task LocalRelatedUsesNativeNumbersAndVerifiedTombstoneCasWithoutNativeWrites()
    {
        using var f = new Fixture();
        await f.Initialize();
        f.Run("show", "42", "--refresh", "-o", "json").ShouldBe(0, f.Error);
        var seed = await LocalSeed(f, "neighbor");
        var uuid = seed.Key.LocalId.ToString("D");
        var initial = await f.Snapshot();
        initial.Items.Single().Key.NativeId.ShouldBe("I_opaque");
        initial.Drafts.Single().Key.ShouldBe(seed.Key);
        initial.Relationships.ShouldBeEmpty();
        initial.Journal.ShouldBeEmpty();
        Dictionary<string, string>? telemetry = null;
        f.Telemetry = Substitute.For<ITelemetryClient>();
        f.Telemetry.When(client => client.TrackEvent(Arg.Any<string>(),
            Arg.Any<Dictionary<string, string>?>(), Arg.Any<Dictionary<string, double>?>()))
            .Do(call =>
            {
                telemetry = call.ArgAt<Dictionary<string, string>>(1);
                throw new InvalidOperationException("optional telemetry unavailable");
            });
        f.Run("link", "local", "related", CoreHostContext.LocalAlias(seed.Key).ToString(CultureInfo.InvariantCulture),
            "--id", "42", "-o", "json").ShouldBe(0, f.Error);
        var added = await f.Snapshot();
        var original = added.Relationships.Single();
        original.Origin.ShouldBe(RelationshipOrigin.Local);
        original.LocalVersion.ShouldBe(1);
        original.Deleted.ShouldBeFalse();
        added.Proposals.Single().Operations.Single().ExpectedLocalVersion.ShouldBeNull();
        added.Journal.Single().Status.ShouldBe(OpStatus.Verified);
        using (var result = JsonDocument.Parse(f.Output))
        {
            result.RootElement.GetProperty("status").GetString().ShouldBe("Verified");
            AssertLocalRelationship(result.RootElement.GetProperty("relationship"), original);
        }
        var requests = f.Http.Requests;
        var credentials = f.CredentialCalls;
        foreach (var format in new[] { "human", "json", "json-full", "json-compact", "minimal", "ids" })
        {
            f.Run("link", "local", "list", "42", "-o", format).ShouldBe(0, f.Error);
            if (format == "ids") f.Output.Trim().ShouldBe(CoreHostContext.LocalAlias(seed.Key).ToString(CultureInfo.InvariantCulture));
            else f.Output.ShouldContain("LOCAL");
            f.Output.ShouldNotContain("ItemKey {");
            if (format.StartsWith("json", StringComparison.Ordinal))
            {
                using var result = JsonDocument.Parse(f.Output);
                var edges = result.RootElement.GetProperty("localRelationships");
                edges.GetArrayLength().ShouldBe(1);
                AssertLocalRelationship(edges[0], original);
            }
        }
        f.Http.Requests.ShouldBe(requests, "list must perform zero TOTAL HTTP, including viewer, schema and number lookup");
        f.CredentialCalls.ShouldBe(credentials);
        (await f.Snapshot()).Version.ShouldBe(added.Version);
        File.Exists(Path.Combine(f.Root, ".twig", "core-context.json")).ShouldBeFalse();
        f.Run("link", "local", "related", uuid, "--id", "42", "-o", "json").ShouldBe(1);
        (await f.Snapshot()).Proposals.Length.ShouldBe(1, "duplicate add is not a fake Verified operation");
        f.Run("link", "local", "unrelate", "42", "--id", uuid, "-o", "json").ShouldBe(0, f.Error);
        var removed = await f.Snapshot();
        removed.Relationships.Single().ShouldBe(original with { Deleted = true, LocalVersion = 2 });
        using (var result = JsonDocument.Parse(f.Output))
            AssertLocalRelationship(result.RootElement.GetProperty("relationship"), original with { Deleted = true, LocalVersion = 2 });
        removed.Proposals.Last().Operations.Single().ExpectedLocalVersion.ShouldBe(1);
        f.Run("link", "local", "unrelate", uuid, "--id", "42", "-o", "json").ShouldBe(1);
        f.Run("link", "local", "related", "42", "--id", uuid, "-o", "json").ShouldBe(0, f.Error);
        var resurrected = await f.Snapshot();
        resurrected.Relationships.Single().ShouldBe(original with { LocalVersion = 3 });
        using (var result = JsonDocument.Parse(f.Output))
            AssertLocalRelationship(result.RootElement.GetProperty("relationship"), original with { LocalVersion = 3 });
        resurrected.Proposals.Last().Operations.Single().ExpectedLocalVersion.ShouldBe(2);
        resurrected.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified);
        f.Http.Writes.ShouldBe(0, "metadata/read acquisition is allowed; native GraphQL mutations are not");
        telemetry.ShouldNotBeNull();
        telemetry["command"].ShouldBe("link local related");
        telemetry.Keys.Order().ShouldBe(new[] { "command", "exit_code", "os_platform", "output_format", "twig_version" });
        telemetry.Values.ShouldNotContain(uuid);
        await f.UseWorkspace(async workspace =>
        {
            var stale = await workspace.PrepareAsync([new("stale-aba", OpKind.Unrelate, original.From,
                other: original.To, relation: original.Kind, origin: RelationshipOrigin.Local, expectedLocalVersion: 1)], default);
            stale.IsSuccess.ShouldBeFalse("the edge is active again, but its original version must not authorize ABA removal");
            stale.Error.Code.ShouldBe(ErrorCode.Conflict);
            stale.Error.Message.ShouldContain("CAS version");
        });
    }

    [Fact]
    public async Task LocalRelatedKeepsOriginalEdgeKeysAcrossVerifiedSeedPublication()
    {
        using var f = new Fixture();
        await f.Initialize();
        var source = await LocalSeed(f, "published source");
        var target = await LocalSeed(f, "still local neighbor");
        var uuid = source.Key.LocalId.ToString("D");
        var other = target.Key.LocalId.ToString("D");
        f.Run("link", "local", "related", other, "--id", uuid, "-o", "json").ShouldBe(0, f.Error);
        var linked = await f.Snapshot();
        var original = linked.Relationships.Single();
        using (var output = JsonDocument.Parse(f.Output))
            AssertLocalRelationship(output.RootElement.GetProperty("relationship"), original);
        var bytes = Core.Core.ProposalBytes(linked.Proposals.Single());
        f.Run("seed", "publish", CoreHostContext.LocalAlias(source.Key).ToString(CultureInfo.InvariantCulture)).ShouldBe(0, f.Error);
        var published = await f.Snapshot();
        published.Mappings.Single().Local.ShouldBe(source.Key);
        published.Items.Single().Fields[GitHubProvider.Number].ShouldBe(FieldValue.Integer(42));
        published.Drafts.ShouldHaveSingleItem().Key.ShouldBe(target.Key);
        published.Relationships.Single().ShouldBe(original);
        foreach (var locator in new[] { uuid, "42" })
        {
            var requests = f.Http.Requests;
            f.Run("link", "local", "list", locator, "-o", "json").ShouldBe(0, f.Error);
            using var output = JsonDocument.Parse(f.Output);
            output.RootElement.GetProperty("localRelationships").GetArrayLength().ShouldBe(1);
            AssertLocalRelationship(output.RootElement.GetProperty("localRelationships")[0], original);
            f.Http.Requests.ShouldBe(requests);
        }
        f.Run("link", "local", "related", other, "--id", "42", "-o", "json").ShouldBe(1);
        f.Run("link", "local", "unrelate", other, "--id", "42", "-o", "json").ShouldBe(0, f.Error);
        using (var output = JsonDocument.Parse(f.Output))
            AssertLocalRelationship(output.RootElement.GetProperty("relationship"), original with { Deleted = true, LocalVersion = 2 });
        f.Run("link", "local", "related", other, "--id", uuid, "-o", "json").ShouldBe(0, f.Error);
        using (var output = JsonDocument.Parse(f.Output))
            AssertLocalRelationship(output.RootElement.GetProperty("relationship"), original with { LocalVersion = 3 });
        var final = await f.Snapshot();
        final.Relationships.Single().ShouldBe(original with { LocalVersion = 3 });
        final.Mappings.Single().ShouldBe(published.Mappings.Single());
        Core.Core.ProposalBytes(final.Proposals.First()).ShouldBe(bytes);
        final.Journal.ShouldAllBe(row => row.Status == OpStatus.Verified);
        f.Http.Writes.ShouldBe(1, "only the explicit seed publication may mutate the tracker");
        var parallel = original with
        {
            From = original.From == source.Key ? final.Mappings.Single().Remote : original.From,
            To = original.To == source.Key ? final.Mappings.Single().Remote : original.To,
        };
        await f.ReplaceItems(final.Items, final.Relationships.Add(parallel));
        f.Run("link", "local", "list", "42", "-o", "json").ShouldBe(1);
        f.Error.ShouldContain("Conflicting logical");
        f.Run("link", "local", "unrelate", other, "--id", uuid, "-o", "json").ShouldBe(1);
        f.Error.ShouldContain("Conflicting logical");
        (await f.Snapshot()).Proposals.Length.ShouldBe(final.Proposals.Length);
    }

    [Fact]
    public async Task LocalRelatedCannotBypassAnUnknownCreationWithANewDigest()
    {
        using var f = new Fixture();
        await f.Initialize();
        var source = await LocalSeed(f, "uncertain creation");
        var target = await LocalSeed(f, "known neighbor");
        f.Http.LoseAcknowledgement = true;
        f.Run("seed", "publish", CoreHostContext.LocalAlias(source.Key).ToString(CultureInfo.InvariantCulture)).ShouldBe(1);
        var before = await f.Snapshot();
        before.Journal.Single().Status.ShouldBe(OpStatus.Indeterminate);
        before.Drafts.ShouldContain(draft => draft.Key == source.Key);
        before.Mappings.ShouldBeEmpty();
        f.Run("link", "local", "related", target.Key.LocalId.ToString("D"),
            "--id", source.Key.LocalId.ToString("D"), "-o", "json").ShouldBe(1);
        f.Error.ShouldContain("unresolved");
        var after = await f.Snapshot();
        after.Relationships.ShouldBeEmpty();
        after.Proposals.Length.ShouldBe(before.Proposals.Length);
        after.Version.ShouldBe(before.Version);
        after.Journal.Single().Digest.ShouldBe(before.Journal.Single().Digest);
        after.Journal.Single().Attempt.ShouldBe(before.Journal.Single().Attempt);
        after.Journal.Single().Status.ShouldBe(OpStatus.Indeterminate);
        after.Journal.Single().Receipts.Single().Evidence.ShouldBe(before.Journal.Single().Receipts.Single().Evidence);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task LocalRelatedRefusesUnknownTargetsAndAmbiguousCliWithoutEffects()
    {
        using var f = new Fixture();
        await f.Initialize();
        var seed = await LocalSeed(f, "known live seed");
        var uuid = seed.Key.LocalId.ToString("D");
        var before = await f.Snapshot();
        var requests = f.Http.Requests;
        foreach (var args in new[] {
            new[] { "link", "local", "related", uuid, "-o", "json" },
            new[] { "link", "local", "related", uuid, "--id", Guid.CreateVersion7().ToString("D"), "-o", "json" },
            new[] { "link", "local", "related", uuid, "--id", uuid.ToUpperInvariant(), "-o", "json" },
            new[] { "link", "local", "related", uuid, "--id", uuid, "-o", "json" },
            new[] { "link", "local", "list", uuid, "--id", "42", "-o", "json" },
            new[] { "link", "local", "list", "-o", "json" },
            new[] { "link", "local", "list", "99", "-o", "json" },
            new[] { "link", "local", "related", uuid, uuid, "-o", "json" },
        })
        {
            f.Run(args).ShouldNotBe(0, string.Join(" ", args));
            f.Output.ShouldBeEmpty();
            f.Error.TrimStart().StartsWith('{').ShouldBeTrue(string.Join(" ", args) + "\n" + f.Error);
            using var error = JsonDocument.Parse(f.Error);
            error.RootElement.GetProperty("error").GetString().ShouldNotBeNullOrWhiteSpace();
        }
        f.Http.Requests.ShouldBe(requests);
        (await f.Snapshot()).Version.ShouldBe(before.Version);
        (await f.Snapshot()).Proposals.ShouldBeEmpty();
        f.Http.Writes.ShouldBe(0);
    }

    private static async Task<Draft> LocalSeed(Fixture f, string title)
    {
        var before = await f.Snapshot();
        f.Run("seed", "new", "--title", title, "--type", GitHubProvider.Untyped, "--no-parent", "-o", "json").ShouldBe(0, f.Error);
        return (await f.Snapshot()).Drafts.Single(draft => !before.Drafts.Any(old => old.Key == draft.Key));
    }

    private static void AssertLocalRelationship(JsonElement relationship, Relationship stored)
    {
        relationship.ValueKind.ShouldBe(JsonValueKind.Object);
        AssertEndpoint(relationship.GetProperty("source"), stored.From);
        AssertEndpoint(relationship.GetProperty("target"), stored.To);
        relationship.GetProperty("kind").GetString().ShouldBe(stored.Kind);
        relationship.GetProperty("origin").GetString().ShouldBe("LOCAL");
        relationship.GetProperty("storage").GetString().ShouldBe("local-only");
        relationship.GetProperty("publication").GetString().ShouldBe("non-native");
        relationship.GetProperty("localVersion").GetInt64().ShouldBe(stored.LocalVersion);
        relationship.GetProperty("deleted").GetBoolean().ShouldBe(stored.Deleted);
    }

    private static void AssertEndpoint(JsonElement endpoint, ItemKey stored)
    {
        endpoint.ValueKind.ShouldBe(JsonValueKind.Object);
        endpoint.EnumerateObject().Count().ShouldBe(stored.IsLocal ? 1 : 3);
        if (stored.IsLocal)
            endpoint.GetProperty("localId").GetString().ShouldBe(stored.LocalId.ToString("D"));
        else
        {
            endpoint.GetProperty("provider").GetString().ShouldBe(stored.Provider.ShouldNotBeNull().Value);
            endpoint.GetProperty("scope").GetString().ShouldBe(stored.ResourceScope);
            endpoint.GetProperty("nativeId").GetString().ShouldBe(stored.NativeId);
        }
    }
}
