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
