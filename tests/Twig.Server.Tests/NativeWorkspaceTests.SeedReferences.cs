using System.Diagnostics;
using System.Text.Json;
using Shouldly;
using Twig.Server;
using Twig.Core;
using Xunit;

namespace Twig.Server.Tests;

public sealed partial class NativeWorkspaceTests
{
    [Fact]
    public async Task OnlyExplicitlyAdmittedMirrorCanContributeSeedOnlyMembers()
    {
        await Start();
        var viewing = await Source("viewing");
        var origin = await Source("origin", mirror: viewing.Stamp.Connection);
        var bench = await Bench("References only");
        var seed = new Seed(ItemKey.Local(Guid.CreateVersion7()), "issue", FieldValues.Empty.Set("System.Title", FieldValue.String("Origin member")));
        gateway.Snapshots[origin.Root] = new(3, retainedItems: [seed]);
        (await definitions.SetSeedReferenceAsync(bench.Id, new(origin.Stamp.Connection, origin.Stamp.StoreInstance, seed.Key), true)).Value.ShouldBeTrue();
        SaveSelection(bench.Id);
        var before = (await Workspace()).GetProperty("view");
        before.GetRawText().ShouldNotContain("Origin member");
        using (var opened = await client.PutAsJsonAsync(View + "context", new ContextBody(viewing.Root), TransportJson.Default.ContextBody))
            opened.EnsureSuccessStatusCode();
        using (var opened = await client.PutAsJsonAsync(View + "population-context", new PopulationContextBody(origin.Root), TransportJson.Default.PopulationContextBody))
            opened.EnsureSuccessStatusCode();
        using var response = JsonDocument.Parse(await client.GetStringAsync(View + $"benches/{bench.Id:D}"));
        var members = response.RootElement.GetProperty("sources").EnumerateArray()
            .SelectMany(source => source.GetProperty("items").EnumerateArray()).ToArray();
        members.ShouldHaveSingleItem().GetProperty("title").GetString().ShouldBe("Origin member");
        gateway.Denied.Add(origin.Root);
        using var check = await client.GetAsync(View + $"benches/{bench.Id:D}/access");
        check.StatusCode.ShouldBe(System.Net.HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AgentCannotReportCompleteMembershipWhenASeedIsWithheld()
    {
        await Start();
        var store = (await definitions.ReadAsync()).Value.InstanceId;
        var canvas = sessions.RegisterNativeCanvas(store);
        owner = sessions.ConnectOwner(store, canvas.ViewId, profile);
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.ContentCapability);
        var source = await Source("agent");
        var bench = await Bench("Shared", source.Stamp.Connection);
        var seed = new Seed(ItemKey.Local(Guid.CreateVersion7()), "issue",
            FieldValues.Empty.Set("System.Title", FieldValue.String("Private seed")));
        gateway.Snapshots[source.Root] = new(2, retainedItems: [seed]);
        (await definitions.SetSeedReferenceAsync(bench.Id, new(source.Stamp.Connection, source.Stamp.StoreInstance, seed.Key), true)).Value.ShouldBeTrue();
        SaveSelection(bench.Id);
        await Workspace();
        using var permission = await client.PutAsJsonAsync(View + "agent/permissions", new AgentPermissionsBody(bench.Id, true, false, false),
            TransportJson.Default.AgentPermissionsBody);
        permission.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new("Bearer", canvas.PresentationCapability);
        using var context = JsonDocument.Parse(await client.GetStringAsync(View + "agent/context"));
        var snapshot = context.RootElement.GetProperty("snapshotId").GetGuid();
        using var page = await client.PostAsJsonAsync(View + "agent/bench", new AgentPageBody(snapshot), TransportJson.Default.AgentPageBody);
        page.StatusCode.ShouldBe(System.Net.HttpStatusCode.ServiceUnavailable);
        (await page.Content.ReadAsStringAsync()).ShouldNotContain("Private seed");
    }

    [Fact]
    public async Task SeedOnAnotherSourceDoesNotHideProtectedProviderEditsOnMembershipFailure()
    {
        await Start();
        var origin = await Source("seed");
        var other = await Source("other");
        var connection = other.Stamp.Connection;
        ItemKey Remote(string id) => ItemKey.Remote(connection.Provider, connection.ResourceScope, id);
        var key = Remote("42");
        var baseline = new WorkItem(key, "issue", FieldValues.Empty, new(key, "1"));
        var changes = FieldValues.Empty.Set("System.Title", FieldValue.String("Protected proposal"));
        gateway.Snapshots[other.Root] = new(2, [baseline], retainedItems: [new LocalEdits(key, baseline.Type, baseline, changes)]);
        var bench = (await definitions.SaveAsync(new(Guid.NewGuid(), "Partial", [new(SelectorKind.Pin, Remote("99"),
            connection: connection)], connections: [origin.Stamp.Connection, connection]), 0)).Value;
        var seed = new Seed(ItemKey.Local(Guid.CreateVersion7()), "issue", FieldValues.Empty);
        gateway.Snapshots[origin.Root] = new(2, retainedItems: [seed]);
        (await definitions.SetSeedReferenceAsync(bench.Id, new(origin.Stamp.Connection, origin.Stamp.StoreInstance, seed.Key), true)).Value.ShouldBeTrue();
        SaveSelection(bench.Id);
        var sources = (await Workspace()).GetProperty("view").GetProperty("sources").EnumerateArray().ToArray();
        var degraded = sources.Single(s => s.GetProperty("sourceId").GetGuid() == other.Source.Id);
        degraded.GetProperty("errorCode").GetString().ShouldNotBeNull();
        var fallback = degraded.GetProperty("items").EnumerateArray().ShouldHaveSingleItem();
        fallback.GetProperty("benchMember").GetBoolean().ShouldBeFalse();
        fallback.GetProperty("title").GetString().ShouldBe("Protected proposal");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PendingDeliveryUsesOnlyVerifiedRemoteIdentity(bool alreadyPinned)
    {
        await Start();
        var source = await Source("origin");
        var connection = source.Stamp.Connection;
        var local = ItemKey.Local(Guid.CreateVersion7());
        var remote = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var bench = (await definitions.SaveAsync(new(Guid.NewGuid(), "Publication",
            alreadyPinned ? [new(SelectorKind.Pin, remote, connection: connection)] : [], connections: [connection]), 0)).Value;
        (await definitions.SetSeedReferenceAsync(bench.Id, new(connection, source.Stamp.StoreInstance, local), true)).Value.ShouldBeTrue();
        SaveSelection(bench.Id);
        var draft = new Seed(local, "fixture.issue", FieldValues.Empty.Set("System.Title", FieldValue.String("Local draft")));
        var item = new WorkItem(remote, draft.Type, FieldValues.Empty.Set("System.Title", FieldValue.String("Verified provider item")), new(remote, "1"));
        var plan = new Plan(connection, "schema", 1, [new("create", OpKind.Create, local, fields: draft.Fields,
            type: draft.Type, concurrency: ConcurrencyMode.ObservedNonAtomic)]);
        var applying = JournalEntry.Planned(plan.Digest, 0).Begin(Guid.NewGuid()).Value;
        var applied = applying.Record(new(plan.Digest, 0, applying.Attempt, OpStatus.Applied, ["accepted"], item.Revision, remote)).Value;
        var verified = applied.Record(new(plan.Digest, 0, applying.Attempt, OpStatus.Verified, ["read back"], item.Revision, remote)).Value;
        foreach (var (entry, drafts) in new[] { (applied, new[] { draft }), (verified, new[] { draft }), (verified, Array.Empty<Seed>()) })
        {
            gateway.Snapshots[source.Root] = new(2, [item], retainedItems: drafts, plans: [plan], journal: [entry],
                mappings: [new(local, remote, plan.Digest, 0)]);
            var view = (await Workspace()).GetProperty("view");
            var rows = view.GetProperty("sources").EnumerateArray().Single().GetProperty("items").EnumerateArray().ToArray();
            if (entry == applied)
                rows.Single(row => row.GetProperty("isLocal").GetBoolean()).GetProperty("benchMember").GetBoolean().ShouldBeTrue();
            else
            {
                var member = rows.ShouldHaveSingleItem();
                member.GetProperty("isLocal").GetBoolean().ShouldBeFalse();
                member.GetProperty("benchMember").GetBoolean().ShouldBeTrue();
                member.GetProperty("title").GetString().ShouldBe("Verified provider item");
            }
        }
        (await definitions.ReadSeedReferencesAsync(bench.Id)).Value.Length.ShouldBe(1);
    }

    [Fact]
    public async Task OrdinaryBenchShowsReferencedSeedOnlyFromItsAdmittedWorktree()
    {
        await Start();
        async Task Git(params string[] arguments)
        {
            var command = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false };
            foreach (var argument in arguments) command.ArgumentList.Add(argument);
            using var git = Process.Start(command)!;
            await git.WaitForExitAsync();
            git.ExitCode.ShouldBe(0);
        }
        await Git("init", "--quiet", "worktree-a");
        await Git("-C", "worktree-a", "worktree", "add", "--quiet", "--orphan", "-b", "other", "../worktree-b");
        var origin = await Source("worktree-a");
        var other = await Source("worktree-b", mirror: origin.Stamp.Connection);
        var bench = await Bench("Shared Bench");
        var local = ItemKey.Local(Guid.CreateVersion7());
        Seed Draft(ItemKey id, string title) => new(id, "fixture.issue",
            FieldValues.Empty.Set("System.Title", FieldValue.String(title)));
        gateway.Snapshots[origin.Root] = new(2, retainedItems:
            [Draft(local, "Origin seed"), Draft(ItemKey.Local(Guid.NewGuid()), "Unrelated draft")]);
        gateway.Snapshots[other.Root] = new(2, retainedItems: [Draft(local, "Wrong store")]);
        (await definitions.SetSeedReferenceAsync(bench.Id, new(origin.Stamp.Connection,
            origin.Stamp.StoreInstance, local), true, default)).Value.ShouldBeTrue();

        foreach (var (source, assigned) in new[] { (origin, true), (other, true), (other, false) })
        {
            var bindings = (await definitions.ReadRepositoryAsync(source.Root)).Value;
            (await definitions.SaveRepositoryAsync(source.Root, bindings.Revision,
                assigned ? [source.Stamp.Connection] : [], bench.Id, attachedBenches: [bench.Id])).IsSuccess.ShouldBeTrue();
            var session = sessions.Content(owner.ViewId, owner.ContentCapability, true);
            var state = sessions.ReadState(session);
            sessions.SaveState(session, new(state.Revision, state.Choices with
            {
                RepositoryRoot = source.Root, BenchId = bench.Id, ViewingRoot = null, DisplayRoots = [],
            }));
            if (assigned)
            {
                await Workspace();
                using var selected = await client.PutAsJsonAsync(View + "context", new ContextBody(source.Root),
                    TransportJson.Default.ContextBody);
                selected.EnsureSuccessStatusCode();
                state = sessions.ReadState(session);
                sessions.SaveState(session, new(state.Revision, state.Choices with
                    { ViewingRoot = source.Root, DisplayRoots = [source.Root] }));
            }
            var workspace = await Workspace();
            AssertMembers(workspace.GetProperty("view"), source == origin);
            using var direct = JsonDocument.Parse(await client.GetStringAsync(View + $"benches/{bench.Id:D}"));
            AssertMembers(direct.RootElement, source == origin);
        }

        static void AssertMembers(JsonElement view, bool atOrigin)
        {
            var sources = view.GetProperty("sources").EnumerateArray().ToArray();
            var items = sources.SelectMany(s => s.GetProperty("items").EnumerateArray()).ToArray();
            view.GetRawText().ShouldNotContain("Unrelated draft");
            view.GetRawText().ShouldNotContain("Wrong store");
            if (atOrigin)
            {
                var member = items.Single();
                member.GetProperty("title").GetString().ShouldBe("Origin seed");
                member.GetProperty("benchMember").GetBoolean().ShouldBeTrue();
                member.GetProperty("normalSeed").GetBoolean().ShouldBeFalse();
                member.GetProperty("pin").ValueKind.ShouldBe(JsonValueKind.Null);
            }
            else
            {
                items.ShouldBeEmpty();
                view.GetRawText().ShouldNotContain("Origin seed");
                var unavailable = sources.Single(s => s.GetProperty("label").GetString() == "Unavailable seed membership");
                unavailable.GetProperty("errorCode").GetString().ShouldBe("Unavailable");
                unavailable.GetProperty("items").GetArrayLength().ShouldBe(0);
                unavailable.GetProperty("connection").ValueKind.ShouldBe(JsonValueKind.Null);
            }
        }
    }
}
