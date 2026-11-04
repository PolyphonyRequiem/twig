using System.Globalization;
using System.Text.Json;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Xunit;
using Model = Twig.Core.Core;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task CorePlanningCommandsPreserveAndOrPathTeamOwnershipAndWorkspaceExclusionSemantics()
    {
        var (paths, connection, native) = await RelationshipWorkspaceAsync();
        var config = await TwigConfiguration.LoadSplitAsync(paths);
        var store = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var key = ItemKey.Remote(connection.Provider, connection.ResourceScope, "42");
        var other = new Connection(new("github"), "api", "repo", "other-identity");
        var foreign = new Selector(SelectorKind.Pin, ItemKey.Remote(other.Provider, other.ResourceScope, "I_node"), connection: other);
        var bench = (await store.SaveAsync(new(Guid.NewGuid(), "Managed planning",
            [new(SelectorKind.Pin, key, connection: connection), foreign,
                new(SelectorKind.Query, query: new(QueryKind.Equal, "System.AssignedTo", FieldValue.Identity(Actor)), connection: connection)]), 0)).Value;
        var now = DateTimeOffset.UtcNow;
        var metadata = new PlanningMetadata(now, ["Project\\A", "Project\\B"],
            [new("Project\\Current", now.AddDays(-1), now.AddDays(1)), new("Project\\Next", now.AddDays(2), now.AddDays(3))], [], "canonical-team");
        (await store.SavePlanningAsync(connection, config.Team, metadata)).IsSuccess.ShouldBeTrue();
        var context = new CoreHostContext(paths.RepoRoot, connection, native.InstanceId);
        var beforeContext = (await context.ReadAsync(default)).Value;
        (await context.WriteAsync(beforeContext, beforeContext with { BenchId = bench.Id }, default)).IsSuccess.ShouldBeTrue();
        var contextBytes = await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"));
        var exclusions = (await store.SaveViewRulesAsync(paths.RepoRoot, connection, 0, [new(SelectorKind.Pin, key)])).Value;
        var requests = _transport.TotalRequests;
        var firstArea = RunPlanning(paths, ["workspace", "area", "add", "Project\\A", "--exact", "-o", "json"]);
        firstArea.Code.ShouldBe(0, firstArea.Error);
        RunPlanning(paths, ["workspace", "area", "add", "Project\\B", "-o", "minimal"]).Code.ShouldBe(0);
        RunPlanning(paths, ["workspace", "sprint", "add", "@current+1", "-o", "json"]).Code.ShouldBe(0);
        RunPlanning(paths, ["workspace", "area", "list", "-o", "json"]).Code.ShouldBe(0);
        RunPlanning(paths, ["workspace", "sprint", "list", "-o", "minimal"]).Code.ShouldBe(0);
        RunPlanning(paths, ["bench", "rules", bench.Name]).Code.ShouldBe(0);
        var current = (await store.ReadAsync()).Value.Benches.Single();
        current.Selectors.Length.ShouldBe(3);
        current.Selectors.ShouldContain(foreign);
        var query = current.Selectors.Single(selector => selector.Kind == SelectorKind.Query).Query!;
        query.Kind.ShouldBe(QueryKind.And);
        query.Children.Single(value => value.Kind == QueryKind.Iteration).PlanningTeam.ShouldBe("canonical-team");
        WorkItem Item(string area, string iteration, string owner)
            => new(key, "Fixture.Task", FieldValues.Empty.Set("System.AreaPath", FieldValue.String(area))
                .Set("System.IterationPath", FieldValue.String(iteration)).Set("System.AssignedTo", FieldValue.Identity(owner)), new(key, "1"), true);
        Model.Matches(query, Item("project\\a", "Project\\Next", Actor), metadata, now).Value.ShouldBeTrue();
        Model.Matches(query, Item("Project\\A\\Child", "Project\\Next", Actor), metadata, now).Value.ShouldBeFalse();
        Model.Matches(query, Item("Project\\B\\Child", "Project\\Next", Actor), metadata, now).Value.ShouldBeTrue();
        Model.Matches(query, Item("Project\\B", "Project\\Current", Actor), metadata, now).Value.ShouldBeFalse();
        Model.Matches(query, Item("Project\\B", "Project\\Next", Sibling), metadata, now).Value.ShouldBeFalse();
        RunPlanning(paths, ["workspace", "area", "remove", "Project\\A", "-o", "json"]).Code.ShouldBe(0);
        RunPlanning(paths, ["workspace", "sprint", "remove", "@CURRENT+1", "-o", "json"]).Code.ShouldBe(0);
        var inactive = (await store.ReadAsync()).Value.Benches.Single().Selectors.Single(selector => selector.Kind == SelectorKind.Query).Query!;
        inactive.Children.Single(value => value.Kind == QueryKind.Iteration).Value!.Values.ShouldBeEmpty();
        Model.Matches(inactive, Item("Project\\B", "Project\\Next", Actor), metadata, now).Value.ShouldBeFalse("last sprint removal must not broaden to an area/ownership-only query");
        var invalid = RunPlanning(paths, ["workspace", "sprint", "add", "@Current+0", "-o", "json"]);
        invalid.Code.ShouldBe(2);
        _transport.TotalRequests.ShouldBe(requests, "configuration and listing are native cached metadata operations, not provider acquisition");
        BenchCodec.SerializeSelectors((await store.ReadViewRulesAsync(paths.RepoRoot, connection)).Value.Exclusions)
            .ShouldBe(BenchCodec.SerializeSelectors(exclusions.Exclusions));
        (await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"))).ShouldBe(contextBytes);
        (await native.ReadAsync(connection, default)).Value.Version.ShouldBe(0);
        _transport.CommittedWrites.ShouldBe(0);
    }

    [Fact]
    public async Task CorePlanningCommandsPinNativeReferencesAndBrowseWithoutChangingSelectionOrPublishing()
    {
        var (paths, connection, native) = await RelationshipWorkspaceAsync();
        var store = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var bench = (await store.SaveAsync(new(Guid.NewGuid(), "Source planning", []), 0)).Value;
        var context = new CoreHostContext(paths.RepoRoot, connection, native.InstanceId);
        var before = (await context.ReadAsync(default)).Value;
        (await context.WriteAsync(before, before with { BenchId = bench.Id }, default)).IsSuccess.ShouldBeTrue();
        var contextBytes = await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"));
        var choices = RunPlanning(paths, ["bench", "sources", "-o", "json"]);
        choices.Code.ShouldBe(0, choices.Error);
        choices.Output.ShouldContain(PrincipalTransport.SavedQuery);
        _transport.ItemReadIds.ShouldBeEmpty();
        var added = RunPlanning(paths, ["bench", "rule", "add", bench.Name, "--query", PrincipalTransport.SavedQuery, "-o", "json"]);
        added.Code.ShouldBe(0, added.Error);
        var pinned = (await store.ReadAsync()).Value.Benches.Single();
        pinned.Selectors.Single().Query!.Kind.ShouldBe(QueryKind.SavedQuery);
        pinned.Selectors.Single().Query!.Value!.Text.ShouldBe(PrincipalTransport.SavedQuery);
        var definition = BenchCodec.Serialize(pinned);
        RunPlanning(paths, ["workspace", "-o", "json"]).Code.ShouldBe(1, "an unacquired native source is unknown, not an empty success");
        _transport.ItemReadIds.ShouldBeEmpty();
        var browsed = RunPlanning(paths, ["bench", "browse", "--query", PrincipalTransport.SavedQuery, "-o", "ids"]);
        browsed.Code.ShouldBe(0, browsed.Error);
        browsed.Output.Trim().ShouldBe("42");
        var cached = RunPlanning(paths, ["workspace", "-o", "json"]);
        cached.Code.ShouldBe(0, cached.Error);
        using (var json = JsonDocument.Parse(cached.Output))
            json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ShouldBe([42]);
        _transport.SavedQueryItems = [43];
        var refreshed = RunPlanning(paths, ["workspace", "--refresh", "-o", "json"]);
        refreshed.Code.ShouldBe(0, refreshed.Error);
        using (var json = JsonDocument.Parse(refreshed.Output))
            json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ShouldBe([43]);
        BenchCodec.Serialize((await store.ReadAsync()).Value.Benches.Single()).ShouldBe(definition);
        RunPlanning(paths, ["bench", "rules", bench.Name, "-o", "json"]).Code.ShouldBe(0);
        var rejected = RunPlanning(paths, ["bench", "rule", "remove", bench.Name, "--index", "1",
            "--expect-revision", (pinned.Revision - 1).ToString(CultureInfo.InvariantCulture), "-o", "json"]);
        rejected.Code.ShouldBe(1);
        var removed = RunPlanning(paths, ["bench", "rule", "remove", bench.Name, "--index", "1",
            "--expect-revision", pinned.Revision.ToString(CultureInfo.InvariantCulture), "-o", "json"]);
        removed.Code.ShouldBe(0, removed.Error);
        (await store.ReadAsync()).Value.Benches.Single().Selectors.ShouldBeEmpty();
        (await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"))).ShouldBe(contextBytes);
        var raw = (await native.ReadAsync(connection, default)).Value;
        raw.Items.Length.ShouldBe(2, "removing a shared rule never removes cached/native items");
        raw.Benches.ShouldBeEmpty();
        raw.Proposals.ShouldBeEmpty();
        raw.Journal.ShouldBeEmpty();
        _transport.CommittedWrites.ShouldBe(0);
        _transport.PatchAttempts.ShouldBe(0);
        _transport.PostCount.ShouldBe(0);
    }

    private (int Code, string Output, string Error) RunPlanning(TwigPaths paths, string[] args)
    {
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var code = RunCoreLink(paths, args);
            if (args.Contains("json"))
            {
                using var parsed = JsonDocument.Parse(code == 0 ? stdout.ToString() : stderr.ToString());
            }
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }
}
