using Twig.Infrastructure.Telemetry;
using System.Globalization;
using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Infrastructure.Services;
using Twig.Infrastructure.Config;
using Xunit;
using CoreModel = Twig.Core;
using CleanStore = Twig.Store.Sqlite.SqliteStore;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task CoreWorkspaceReadResolvesSharedRelativeRulesFromCachedTeamCalendar()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        var configuration = await TwigConfiguration.LoadSplitAsync(paths);
        var shared = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var now = DateTimeOffset.UtcNow;
        var bench = new CoreModel.Bench(Guid.NewGuid(), "Relative cached scope",
            [new(CoreModel.SelectorKind.Query, query: new(CoreModel.QueryKind.Iteration, "System.IterationPath",
                CoreModel.FieldValue.Strings(["@Current+1"]), planningTeam: "canonical-team"), connection: connection)]);
        (await shared.SaveAsync(bench, 0)).IsSuccess.ShouldBeTrue();
        (await shared.SavePlanningAsync(connection, configuration.Team, new(now, [],
            [new("Project\\Current", now.AddDays(-1), now.AddDays(1)), new("Project\\Next", now.AddDays(2), now.AddDays(3))],
            [], "canonical-team"))).IsSuccess.ShouldBeTrue();
        CoreModel.WorkItem Item(string id, string iteration)
        {
            var key = CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, id);
            return new(key, "Fixture.Task", CoreModel.FieldValues.Empty
                .Set("System.Title", CoreModel.FieldValue.String("Cached " + id))
                .Set("System.IterationPath", CoreModel.FieldValue.String(iteration)), new(key, "1"), true);
        }
        (await store.CommitAsync(connection, 0, new(1, [Item("42", "Project\\Current"), Item("43", "Project\\Next")]), default))
            .IsSuccess.ShouldBeTrue();
        var context = new CoreHostContext(paths.RepoRoot, connection, store.InstanceId);
        var before = (await context.ReadAsync(default)).Value;
        (await context.WriteAsync(before, before with { BenchId = bench.Id }, default)).IsSuccess.ShouldBeTrue();
        _transport.ItemReadIds.Clear();
        _transport.QueryAfterIds.Clear();
        var oldOut = Console.Out;
        var oldError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            RunCoreLink(paths, ["workspace", "-o", "json"]).ShouldBe(0, stderr.ToString());
            using var json = JsonDocument.Parse(stdout.ToString());
            json.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetInt32()).ShouldBe([43]);
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
        _transport.ItemReadIds.ShouldBeEmpty();
        _transport.QueryAfterIds.ShouldBeEmpty();
        _transport.CommittedWrites.ShouldBe(0);
        (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(1);
        (await shared.ReadAsync()).Value.Benches.Single().Selectors.Single().Query!.Value!.Values.ShouldContain("@Current+1");
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("ws")]
    public async Task CoreWorkspaceReadRoutesThroughProductionComposition(string route)
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        File.Exists(Path.Combine(paths.TwigDir, CoreWorkspaceFactory.FileName)).ShouldBeTrue();
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, [route, "-o", "json"]);
        using var root = services.BuildServiceProvider();
        root.GetService<CoreCommandAdapter>().ShouldNotBeNull();
        AssertNativeRegistrations(services);
        using var admitted = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        var item = (await admitted.Workspace.ReadAsync(CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "42"), default)).Value;
        var current = (await store.ReadAsync(connection, default)).Value;
        var bench = new CoreModel.Bench(Guid.NewGuid(), "Read scope", [new(CoreModel.SelectorKind.Pin, item.Key)]);
        (await store.CommitAsync(connection, current.Version, new(current.Version + 1, current.Items, current.Relationships,
            current.Drafts, current.Proposals, current.Journal, current.Mappings, [bench]), default)).IsSuccess.ShouldBeTrue();
        var context = (await admitted.Workspace.Context.ReadAsync(default)).Value;
        (await admitted.Workspace.Context.WriteAsync(context, context with { BenchId = bench.Id }, default)).IsSuccess.ShouldBeTrue();
        var capture = (await store.ReadAsync(connection, default)).Value;
        _transport.ItemReadIds.Clear();
        var beforeOut = Console.Out;
        var beforeError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            RunCoreLink(paths, [route, "-o", "json"]).ShouldBe(0, stderr.ToString());
            using var json = JsonDocument.Parse(stdout.ToString());
            json.RootElement.GetProperty("projection").GetString().ShouldBe("core-bench-workspace-v1");
            json.RootElement.GetProperty("items").EnumerateArray().Select(value => value.GetProperty("id").GetInt32())
                .ShouldBe([42]);
            _transport.ItemReadIds.ShouldBeEmpty();
            _transport.CommittedWrites.ShouldBe(0);
            (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(capture.Version);
        }
        finally
        {
            Console.SetOut(beforeOut);
            Console.SetError(beforeError);
        }
    }

    [Fact]
    public async Task CoreWorkspaceReadKeepsExactMembershipTypedDebtAndOneOutputAcrossFormats()
    {
        var (paths, connection, store, bench, seed) = await WorkspaceReadScopeAsync();
        var initial = (await store.ReadAsync(connection, default)).Value;
        var planned = initial.Journal.Single();
        var attempt = Guid.NewGuid();
        var applying = planned.Begin(attempt).Value;
        (await store.CommitAsync(connection, initial.Version, new(initial.Version + 1, initial.Items,
            initial.Relationships, initial.Drafts, initial.Proposals, [applying], initial.Mappings, initial.Benches),
            default)).IsSuccess.ShouldBeTrue();
        initial = (await store.ReadAsync(connection, default)).Value;
        var unknown = applying.Record(new(planned.Digest, planned.Operation, attempt,
            CoreModel.OpStatus.Indeterminate, ["Fixture retained uncertain acknowledgement"],
            error: new(CoreModel.ErrorCode.Unavailable, "Uncertain fixture write; must not replay"))).Value;
        (await store.CommitAsync(connection, initial.Version, new(initial.Version + 1, initial.Items,
            initial.Relationships, initial.Drafts, initial.Proposals, [unknown], initial.Mappings, initial.Benches),
            default)).IsSuccess.ShouldBeTrue();
        var before = (await store.ReadAsync(connection, default)).Value;
        before.Journal.Single().Status.ShouldBe(CoreModel.OpStatus.Indeterminate);
        var document = await WorkspaceStoredDocumentAsync(paths);
        var context = await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"));
        var telemetry = Substitute.For<ITelemetryClient>();
        telemetry.WhenForAnyArgs(client => client.TrackEvent(default!, default, default))
            .Do(_ => throw new InvalidOperationException("Optional telemetry failed"));
        _transport.ItemReadIds.Clear();
        _transport.QueryAfterIds.Clear();
        var requests = _transport.TotalRequests;
        var expectedIds = new[] { 41, 42, 43, 44, 45, CoreHostContext.LocalAlias(seed) }.Order().ToArray();
        before.Benches.Single(value => value.Id == bench).Selectors.Select(value => value.Kind)
            .ShouldBe([CoreModel.SelectorKind.Pin, CoreModel.SelectorKind.Subtree, CoreModel.SelectorKind.Query]);
        before.Drafts.ShouldContain(value => value.Key == seed && value.Baseline == null);
        foreach (var format in new[] { "human", "json", "json-full", "json-compact", "minimal", "ids" })
        {
            var result = WorkspaceInvoke(paths,
                ["ws", "--bench", "read SCOPE", "--expect-bench", bench.ToString("D"), "--no-live", "--flat", "--view", "table", "-o", format], telemetry);
            result.Exit.ShouldBe(0, result.Error);
            result.Error.ShouldBeEmpty();
            result.Output.ShouldNotContain("native 90");
            result.Output.ShouldNotContain("native 91");
            result.Output.ShouldNotContain("native 99");
            if (format.StartsWith("json", StringComparison.Ordinal))
            {
                using var json = JsonDocument.Parse(result.Output);
                var rows = json.RootElement.GetProperty("items").EnumerateArray().ToArray();
                rows.Select(row => row.GetProperty("id").GetInt32()).Order().ShouldBe(expectedIds);
                var debt = rows.Single(row => row.GetProperty("id").GetInt32() == 45);
                debt.GetProperty("benchMember").GetBoolean().ShouldBeFalse();
                debt.GetProperty("hasDraft").GetBoolean().ShouldBeTrue();
                debt.GetProperty("title").GetString().ShouldBe("Proposed debt");
                debt.GetProperty("source").GetString().ShouldBe("cached+proposed");
                var fields = debt.GetProperty("proposedFields").EnumerateArray().ToArray();
                fields.Single(value => value.GetProperty("reference").GetString() == "Custom.Amount")
                    .GetProperty("value").GetDecimal().ShouldBe(1.5m);
                fields.Single(value => value.GetProperty("reference").GetString() == "Custom.Clear")
                    .GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
                var local = rows.Single(row => row.GetProperty("id").GetInt32() < 0);
                local.GetProperty("localId").GetGuid().ShouldBe(seed.LocalId);
                local.GetProperty("source").GetString().ShouldBe("local-draft");
                json.RootElement.GetProperty("scope").GetString().ShouldNotBeNull().ShouldContain("not an exhaustive provider query");
            }
            else if (format == "ids")
                result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).Order().ShouldBe(expectedIds);
            else
            {
                result.Output.ShouldContain("Proposed debt");
                result.Output.ShouldContain("Unpublished debt");
            }
        }
        _transport.TotalRequests.ShouldBeGreaterThan(requests, "schema is acquired from the actual provider, not fabricated offline");
        _transport.ItemReadIds.ShouldBeEmpty();
        _transport.QueryAfterIds.ShouldBeEmpty();
        _transport.CommittedWrites.ShouldBe(0);
        _transport.PostCount.ShouldBe(0);
        var after = (await store.ReadAsync(connection, default)).Value;
        after.Version.ShouldBe(before.Version);
        after.Drafts.Select(value => value.Key).ShouldBe(before.Drafts.Select(value => value.Key));
        after.Drafts.Single(value => !value.Key.IsLocal).Baseline!.Fields.ShouldBe(before.Drafts.Single(value => !value.Key.IsLocal).Baseline!.Fields);
        CoreModel.Core.ProposalBytes(after.Proposals.Single()).ShouldBe(CoreModel.Core.ProposalBytes(before.Proposals.Single()));
        after.Journal.Single().Status.ShouldBe(CoreModel.OpStatus.Indeterminate);
        after.Journal.Single().Attempt.ShouldBe(attempt);
        after.Benches.Single(value => value.Id == bench).Selectors.ShouldBe(before.Benches.Single(value => value.Id == bench).Selectors);
        (await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"))).ShouldBe(context);
        (await WorkspaceStoredDocumentAsync(paths)).ShouldBe(document);
        var properties = (Dictionary<string, string>)telemetry.ReceivedCalls().Last().GetArguments()[1]!;
        properties["command"].ShouldBe("workspace");
        properties["exit_code"].ShouldBe("0");
        properties.Keys.Order().ShouldBe(new[] { "command", "exit_code", "os_platform", "output_format", "twig_version" });
        properties.Values.ShouldNotContain("Read scope");
    }

    [Fact]
    public async Task CoreWorkspaceReadRefusesUnsupportedShapesAndUntrustworthyScopeWithoutBroadening()
    {
        var (paths, connection, store, bench, _) = await WorkspaceReadScopeAsync();
        var initial = (await store.ReadAsync(connection, default)).Value;
        var requests = _transport.TotalRequests;
        foreach (var (args, expectedExit, retired) in new[]
        {
                (new[] { "workspace", "--all", "--refresh" }, 1, true),
                (new[] { "sprint", "--refresh" }, 1, true),
                (new[] { "tree", "--all", "--refresh" }, 1, true),
                (new[] { "workspace", "--tree", "--refresh" }, 1, true),
                (new[] { "workspace", "--include-browser" }, 1, true),
                (new[] { "workspace", "--browser-version", "2" }, 1, true),
                (new[] { "workspace", "--expect-binding", "captured" }, 1, true),
                (new[] { "workspace", "--expect-identity", "captured" }, 1, true),
                (new[] { "workspace", "--expect-settings", "captured" }, 1, true),
                (new[] { "workspace", "--view", "tree" }, 2, false),
                (new[] { "workspace", "--view", "bogus" }, 2, false),
                (new[] { "workspace", "--expect-bench", "7" }, 2, false),
            })
        {
            var result = WorkspaceInvoke(paths, [.. args, "-o", "json"]);
            result.Exit.ShouldBe(expectedExit, string.Join(" ", args) + ": " + result.Error);
            result.Output.ShouldBeEmpty();
            using var error = JsonDocument.Parse(result.Error);
            var message = error.RootElement.GetProperty("error").GetString();
            message.ShouldNotBeNullOrWhiteSpace();
            if (retired) message.ShouldNotBeNull().ShouldContain("retired from current delivery");
            _transport.TotalRequests.ShouldBe(requests);
            (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(initial.Version);
        }
        foreach (var args in new[]
        {
                new[] { "--bench", "missing" }, new[] { "--expect-bench", Guid.NewGuid().ToString("D") },
                new[] { "--bench", "Other", "--refresh" },
            })
        {
            var result = WorkspaceInvoke(paths, ["workspace", .. args, "-o", "json"]);
            result.Exit.ShouldBe(1, result.Error);
            result.Output.ShouldBeEmpty();
            _transport.TotalRequests.ShouldBe(requests);
        }
        var library = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var duplicate = await library.SaveAsync(new(Guid.NewGuid(), "READ SCOPE", []), 0);
        duplicate.IsSuccess.ShouldBeFalse("the shared owner must reject ambiguous names before storing them");
        duplicate.Error.Code.ShouldBe(CoreModel.ErrorCode.Conflict);
        // Every variant has unrelated cached items; none may turn a failed selector into all-cache/empty success.
        foreach (var failure in new[] { "missing", "incomplete", "cycle", "unknown-relation", "query-evidence", "no-selection", "foreign" })
        {
            var current = (await store.ReadAsync(connection, default)).Value;
            var selected = initial.Benches.Single(value => value.Id == bench);
            var items = initial.Items;
            var edges = initial.Relationships;
            if (failure == "missing")
                selected = new(bench, selected.Name, [new(CoreModel.SelectorKind.Pin,
                        CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, "404"))]);
            if (failure == "incomplete")
                items = items.Select(item => item.Key.NativeId == "42"
                    ? new CoreModel.WorkItem(item.Key, item.Type, item.Fields, item.Revision, false, item.Resources, item.ResourcesComplete)
                    : item).ToImmutableArray();
            if (failure == "cycle")
                edges = edges.Add(new(items.Single(item => item.Key.NativeId == "44").Key,
                    items.Single(item => item.Key.NativeId == "42").Key, "Native.ParentForward", CoreModel.RelationshipOrigin.Provider));
            if (failure == "unknown-relation")
                selected = new(bench, selected.Name, [new(CoreModel.SelectorKind.Subtree,
                        items.Single(item => item.Key.NativeId == "42").Key, hierarchyRelation: "Unknown.Hierarchy")]);
            if (failure == "query-evidence")
                selected = new(bench, selected.Name, [new(CoreModel.SelectorKind.Query,
                        query: new(CoreModel.QueryKind.Equal, "Unknown.Field", CoreModel.FieldValue.Integer(3)))]);
            if (failure == "foreign")
            {
                var foreign = CoreModel.ItemKey.Remote(new("github"), "foreign-scope", "opaque-node");
                items = items.Add(new(foreign, items[0].Type, items[0].Fields, new(foreign, "foreign"), true));
            }
            var retained = (await library.ReadAsync()).Value.Benches.Single(value => value.Id == bench);
            var scoped = selected.Selectors.Select(selector => new CoreModel.Selector(selector.Kind, selector.Item,
                selector.Query, selector.HierarchyRelation, connection));
            (await library.SaveAsync(new(selected.Id, selected.Name, scoped), retained.Revision)).IsSuccess.ShouldBeTrue();
            (await store.CommitAsync(connection, current.Version, new(current.Version + 1, items, edges, initial.Drafts,
                initial.Proposals, initial.Journal, initial.Mappings, initial.Benches), default)).IsSuccess.ShouldBeTrue();
            if (failure == "no-selection")
            {
                var file = new CoreHostContext(paths.RepoRoot, connection, store.InstanceId);
                var context = (await file.ReadAsync(default)).Value;
                (await file.WriteAsync(context, context with { BenchId = null }, default)).IsSuccess.ShouldBeTrue();
            }
            var result = WorkspaceInvoke(paths, failure == "no-selection"
                ? ["workspace", "-o", "json"] : ["workspace", "--bench", "Read scope", "-o", "json"]);
            result.Exit.ShouldBe(1, failure + ": " + result.Error);
            result.Output.ShouldBeEmpty();
            JsonDocument.Parse(result.Error).Dispose();
            (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(current.Version + 1);
        }
        _transport.CommittedWrites.ShouldBe(0);
        _transport.ItemReadIds.Clear();
        var numeric = initial.Benches.Single(value => value.Id == bench);
        var state = (await store.ReadAsync(connection, default)).Value;
        var numericItems = initial.Items.Select(item => new CoreModel.WorkItem(item.Key, item.Type,
            item.Fields.Set("Custom.Score", CoreModel.FieldValue.Integer(item.Key.NativeId == "43" ? 5 : 1)),
            item.Revision, item.RelationshipsComplete, item.Resources, item.ResourcesComplete)).ToArray();
        var numericBefore = (await library.ReadAsync()).Value.Benches.Single(value => value.Id == bench);
        (await library.SaveAsync(new(numeric.Id, numeric.Name, [new(CoreModel.SelectorKind.Query,
            query: new(CoreModel.QueryKind.GreaterOrEqual, "Custom.Score", CoreModel.FieldValue.Integer(5)), connection: connection)]),
            numericBefore.Revision)).IsSuccess.ShouldBeTrue();
        (await store.CommitAsync(connection, state.Version, new(state.Version + 1, numericItems, initial.Relationships,
            initial.Drafts, initial.Proposals, initial.Journal, initial.Mappings, initial.Benches), default)).IsSuccess.ShouldBeTrue();
        var typed = WorkspaceInvoke(paths, ["workspace", "--bench", bench.ToString("D"), "-o", "ids"]);
        typed.Exit.ShouldBe(0, typed.Error);
        typed.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(int.Parse).Order().ShouldBe(new[] { 43, 45, CoreHostContext.LocalAlias(initial.Drafts.Single(value => value.Key.IsLocal).Key) }.Order());
        _transport.ItemReadIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task CoreWorkspaceReadRefreshUsesOnlySharedPullAndPreservesDebtAndMappedSelectors()
    {
        var (paths, connection, store, bench, seed) = await WorkspaceReadScopeAsync();
        var initial = (await store.ReadAsync(connection, default)).Value;
        var scope = initial.Benches.Single(value => value.Id == bench);
        (await store.CommitAsync(connection, initial.Version, new(initial.Version + 1, initial.Items, initial.Relationships,
            initial.Drafts, initial.Proposals, initial.Journal, initial.Mappings,
            initial.Benches.Select(value => value.Id == bench
                ? new CoreModel.Bench(value.Id, value.Name, scope.Selectors.Where(selector => selector.Kind != CoreModel.SelectorKind.Query))
                : value)), default)).IsSuccess.ShouldBeTrue();
        var before = (await store.ReadAsync(connection, default)).Value;
        var contextBytes = await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"));
        _transport.SetItem(45, "Fresh remote debt", 2, Actor);
        _transport.ItemReadIds.Clear();
        var refreshed = WorkspaceInvoke(paths, ["workspace", "--refresh", "--expect-bench", bench.ToString("D"), "-o", "json"]);
        refreshed.Exit.ShouldBe(0, refreshed.Error);
        using (var json = JsonDocument.Parse(refreshed.Output))
            json.RootElement.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("id").GetInt32() == 45)
                .GetProperty("title").GetString().ShouldBe("Proposed debt");
        _transport.ItemReadIds.ShouldContain(45);
        _transport.ItemReadIds.ShouldContain(44);
        _transport.ItemReadIds.ShouldNotContain(90);
        _transport.ItemReadIds.ShouldNotContain(91);
        _transport.CommittedWrites.ShouldBe(0);
        var after = (await store.ReadAsync(connection, default)).Value;
        after.Items.Single(item => item.Key.NativeId == "45").Fields["System.Title"].Text.ShouldBe("Fresh remote debt");
        after.Drafts.Single(value => !value.Key.IsLocal).Baseline!.Fields.ShouldBe(before.Drafts.Single(value => !value.Key.IsLocal).Baseline!.Fields);
        after.Drafts.Single(value => !value.Key.IsLocal).Changes.ShouldBe(before.Drafts.Single(value => !value.Key.IsLocal).Changes);
        after.Drafts.Single(value => value.Key == seed).ShouldBe(before.Drafts.Single(value => value.Key == seed));
        after.Journal.ShouldBe(before.Journal);
        CoreModel.Core.ProposalBytes(after.Proposals.Single()).ShouldBe(CoreModel.Core.ProposalBytes(before.Proposals.Single()));
        after.Benches.Single(value => value.Id == bench).Selectors.ShouldBe(before.Benches.Single(value => value.Id == bench).Selectors);
        (await File.ReadAllBytesAsync(Path.Combine(paths.TwigDir, "core-context.json"))).ShouldBe(contextBytes);

        var local = CoreHostContext.LocalAlias(seed).ToString(CultureInfo.InvariantCulture);
        var publication = WorkspaceInvoke(paths, ["seed", "publish", local]);
        publication.Exit.ShouldBe(0, publication.Error);
        var published = (await store.ReadAsync(connection, default)).Value;
        var mapping = published.Mappings.Single(value => value.Local == seed);
        var library = UserBenchStore.ForRegistry(_bindings.RegistryPath);
        var selected = (await library.ReadAsync()).Value.Benches.Single(value => value.Id == bench);
        (await library.SaveAsync(new(selected.Id, selected.Name,
            selected.Selectors.Add(new(CoreModel.SelectorKind.Pin, seed, connection: connection))),
            selected.Revision)).IsSuccess.ShouldBeTrue();
        var captured = (await store.ReadAsync(connection, default)).Value;
        _transport.ItemReadIds.Clear();
        var read = WorkspaceInvoke(paths, ["workspace", "-o", "ids"]);
        read.Exit.ShouldBe(0, read.Error);
        read.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ShouldContain(mapping.Remote.NativeId!);
        read.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ShouldNotContain(local);
        _transport.ItemReadIds.ShouldBeEmpty();
        var retained = (await store.ReadAsync(connection, default)).Value;
        retained.Version.ShouldBe(captured.Version);
        retained.Mappings.ShouldBe(captured.Mappings);
        (await library.ReadAsync()).Value.Benches.Single(value => value.Id == bench).Selectors.Last().Item.ShouldBe(seed);
        retained.Benches.Single(value => value.Id == bench).Selectors.ShouldBe(published.Benches.Single(value => value.Id == bench).Selectors);
        retained.Journal.Select(value => (value.Digest, value.Operation, value.Attempt, value.Status, value.Receipts.Length))
            .ShouldBe(captured.Journal.Select(value => (value.Digest, value.Operation, value.Attempt, value.Status, value.Receipts.Length)));
        foreach (var row in captured.Journal)
        {
            var actual = retained.Journal.Single(value => value.Digest == row.Digest && value.Operation == row.Operation);
            foreach (var (expectedReceipt, actualReceipt) in row.Receipts.Zip(actual.Receipts))
            {
                actualReceipt.Evidence.ShouldBe(expectedReceipt.Evidence);
                actualReceipt.Revision.ShouldBe(expectedReceipt.Revision);
                actualReceipt.PublishedKey.ShouldBe(expectedReceipt.PublishedKey);
                actualReceipt.Status.ShouldBe(expectedReceipt.Status);
                actualReceipt.Attempt.ShouldBe(expectedReceipt.Attempt);
            }
        }
        (await store.CommitAsync(connection, retained.Version, new(retained.Version + 1, retained.Items,
            retained.Relationships, retained.Drafts.Add(before.Drafts.Single(value => value.Key == seed)),
            retained.Proposals, retained.Journal, retained.Mappings, retained.Benches), default)).IsSuccess.ShouldBeTrue();
        var debtCapture = (await store.ReadAsync(connection, default)).Value;
        var mappedDebt = WorkspaceInvoke(paths, ["workspace", "-o", "json"]);
        mappedDebt.Exit.ShouldBe(0, mappedDebt.Error);
        using var mappedJson = JsonDocument.Parse(mappedDebt.Output);
        var mappedRow = mappedJson.RootElement.GetProperty("items").EnumerateArray()
            .Single(value => value.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture) == mapping.Remote.NativeId);
        mappedRow.GetProperty("localId").GetGuid().ShouldBe(seed.LocalId);
        mappedRow.GetProperty("hasDraft").GetBoolean().ShouldBeTrue();
        mappedRow.GetProperty("source").GetString().ShouldBe("cached+proposed");
        (await store.ReadAsync(connection, default)).Value.Version.ShouldBe(debtCapture.Version);
    }

    private async Task<(TwigPaths Paths, CoreModel.Connection Connection, CleanStore Store, Guid Bench, CoreModel.ItemKey Seed)> WorkspaceReadScopeAsync()
    {
        var (paths, connection, store) = await RelationshipWorkspaceAsync();
        foreach (var id in new[] { 41, 42, 43, 44, 45, 90, 91, 99 })
            _transport.SetItem(id, id == 43 ? "query member" : "native " + id, 1, Actor);
        _transport.SetHierarchy(42, 44);
        _transport.SetHierarchy(90, 91);
        var services = new ServiceCollection();
        services.AddSingleton<IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["workspace"]);
        using var root = services.BuildServiceProvider();
        AssertNativeRegistrations(services);
        using var handle = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        foreach (var id in new[] { 41, 42, 43, 44, 45, 90, 91, 99 })
            (await handle.Workspace.ReadAsync(CoreModel.ItemKey.Remote(connection.Provider, connection.ResourceScope, id.ToString(CultureInfo.InvariantCulture)), default)).IsSuccess.ShouldBeTrue();
        var schema = (await handle.Workspace.SchemaAsync(default)).Value;
        var snapshot = (await store.ReadAsync(connection, default)).Value;
        CoreModel.ItemKey Key(string id) => snapshot.Items.Single(item => item.Key.NativeId == id).Key;
        var bench = new CoreModel.Bench(Guid.NewGuid(), "Read scope",
            [new(CoreModel.SelectorKind.Pin, Key("41")),
                    new(CoreModel.SelectorKind.Subtree, Key("42"), hierarchyRelation: "Native.ParentForward"),
                    new(CoreModel.SelectorKind.Query, query: new(CoreModel.QueryKind.Equal, "System.Title", CoreModel.FieldValue.String("query member")))]);
        var debt = snapshot.Items.Single(item => item.Key.NativeId == "45");
        var seed = CoreModel.ItemKey.Local(Guid.CreateVersion7());
        var proposal = new CoreModel.Proposal(connection, schema.Fingerprint, snapshot.Version,
            [new("retained", CoreModel.OpKind.Batch, debt.Key, debt.Revision,
                    new([KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Retained proposal"))]))]);
        (await store.CommitAsync(connection, snapshot.Version, new(snapshot.Version + 1, snapshot.Items, snapshot.Relationships,
            [new(debt.Key, debt.Type, debt, new([
                    KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Proposed debt")),
                    KeyValuePair.Create("Custom.Amount", CoreModel.FieldValue.Decimal(1.5m)),
                    KeyValuePair.Create("Custom.Clear", CoreModel.FieldValue.Null),
                    KeyValuePair.Create("id", CoreModel.FieldValue.Integer(123456)),
                ])),
                new(seed, debt.Type, null, new([
                    KeyValuePair.Create("System.Title", CoreModel.FieldValue.String("Unpublished debt")),
                    KeyValuePair.Create("System.State", CoreModel.FieldValue.String("To Do")),
                ]))], [proposal], [CoreModel.JournalEntry.Planned(proposal.Digest, 0)], snapshot.Mappings,
            [bench, new(Guid.NewGuid(), "Other", [new(CoreModel.SelectorKind.Pin, Key("99"))])]), default)).IsSuccess.ShouldBeTrue();
        var context = (await handle.Workspace.Context.ReadAsync(default)).Value;
        (await handle.Workspace.Context.WriteAsync(context, context with
        {
            BenchId = bench.Id,
            History = [CoreContextKey.Capture(Key("90"))],
            Position = 0,
        }, default)).IsSuccess.ShouldBeTrue();
        return (paths, connection, store, bench.Id, seed);
    }

    private (int Exit, string Output, string Error) WorkspaceInvoke(TwigPaths paths, string[] args, ITelemetryClient? telemetry = null)
    {
        var beforeOut = Console.Out;
        var beforeError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var exit = Twig.TwigConsoleApp.Run(args, registration =>
            {
                registration.AddSingleton<IConnectionBindingService>(_bindings);
                registration.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
                if (telemetry is not null) registration.AddSingleton(telemetry);
                registration.AddTwigWorkspaceServices(paths.StartDir, args);
            });
            return (exit, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(beforeOut);
            Console.SetError(beforeError);
        }
    }

    private static async Task<string> WorkspaceStoredDocumentAsync(TwigPaths paths)
    {
        await using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(paths.TwigDir, "relationships-core.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await database.OpenAsync();
        await using var query = database.CreateCommand();
        query.CommandText = "SELECT document FROM core_snapshots";
        return (string)(await query.ExecuteScalarAsync()).ShouldNotBeNull();
    }
}
