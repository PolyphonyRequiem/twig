using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects.Fields;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// #1186 display-only A semantics for <c>twig query</c> and <c>twig nav</c>/history: opted in, the
/// cells and labels show durable local values, while the WIQL selection, order, count, truncation,
/// ids, pattern matching, history entries and navigation targets stay on the mirror.
/// </summary>
public sealed partial class ShowDurableFieldOverlayTests
{
    private static WorkItem Remote(int id, string title, int? parentId = null)
    {
        var builder = new WorkItemBuilder(id, title).InState("Active").WithIterationPath(@"Project\Sprint 1");
        if (parentId is not null)
            builder = builder.WithParent(parentId.Value);
        var item = builder.Build();
        item.MarkSynced(3);
        return item;
    }

    /// <summary>The ADO query returns 43 before 42, so order is evidence rather than an accident of ids.</summary>
    private static void ReturnFromWiql(ServiceProvider services)
    {
        var ado = services.GetRequiredService<IAdoWorkItemService>();
        ado.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<int> { 43, 42 });
        ado.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<WorkItem> { Remote(43, "Untouched remote"), Remote(42, "Remote title") });
    }

    private static async Task<string> QueryAsync(ServiceProvider services, string format)
    {
        var command = services.GetRequiredService<QueryCommand>();
        var (exit, output) = await StdoutCapture.RunAsync(() => command.ExecuteAsync(title: "title", top: 25, outputFormat: format));
        exit.ShouldBe(0);
        return output;
    }

    private static List<long> IdsInOrder(string json)
    {
        var ids = new List<long>();
        using var doc = JsonDocument.Parse(json);
        Collect(doc.RootElement);
        return ids;

        void Collect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number)
                    ids.Add(id.GetInt64());
                foreach (var property in element.EnumerateObject())
                    Collect(property.Value);
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in element.EnumerateArray())
                    Collect(child);
            }
        }
    }

    [Fact]
    public async Task OptedIn_QueryJsonShowsDurableCells_WhileSelectionOrderCountAndIdsStayOnTheFetch()
    {
        using var services = Compose(optedIn: true);
        await SeedAsync(services, 42, "Remote title", "Local title");
        await SeedAsync(services, 43, "Untouched remote", localTitle: null);
        ReturnFromWiql(services);
        var before = DurableRows();

        var json = await QueryAsync(services, "json");

        var titles = BatchTitles(json);
        titles[42].ShouldBe("Local title");
        titles[43].ShouldBe("Untouched remote");
        IdsInOrder(json).ShouldBe(new long[] { 43, 42 });
        using var document = JsonDocument.Parse(json);
        FindProperty(document.RootElement, "count")!.Value.GetInt32().ShouldBe(2);
        FindProperty(document.RootElement, "truncated")!.Value.GetBoolean().ShouldBeFalse();
        (await QueryAsync(services, "ids")).ShouldBe("43\n42" + Environment.NewLine);
        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBe(before);
        var mirror = services.GetRequiredService<IWorkItemRepository>();
        (await mirror.GetByIdAsync(42))!.Title.ShouldBe("Remote title");
        (await mirror.GetByIdAsync(42))!.IsDirty.ShouldBeFalse();
    }

    private void CorruptLocalFields()
    {
        using var command = _store.GetConnection().CreateCommand();
        command.CommandText = "UPDATE pending.local_work_items SET fields_json = 'null'";
        command.ExecuteNonQuery().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task OptedIn_QueryIdsDoNotReadLocalFields_WhileCellOutputSurfacesCorruption()
    {
        using var services = Compose(optedIn: true);
        await SeedAsync(services, 42, "Remote title", "Local title");
        await SeedAsync(services, 43, "Untouched remote", localTitle: null);
        ReturnFromWiql(services);
        CorruptLocalFields();
        var before = DurableRows();

        (await QueryAsync(services, "ids")).ShouldBe("43\n42" + Environment.NewLine);
        await Should.ThrowAsync<InvalidDataException>(() => QueryAsync(services, "json"));
        DurableRows().ShouldBe(before);
    }

    [Fact]
    public async Task OptedIn_HumanQueryShowsTheDurableCell()
    {
        using var services = Compose(optedIn: true);
        await SeedAsync(services, 42, "Remote title", "Local title");
        await SeedAsync(services, 43, "Untouched remote", localTitle: null);
        ReturnFromWiql(services);

        var human = await QueryAsync(services, "human");

        human.ShouldContain("Local title");
        human.ShouldNotContain("Remote title");
        human.ShouldContain("Untouched remote");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("human")]
    [InlineData("minimal")]
    [InlineData("ids")]
    public async Task OptedOut_QueryIsByteIdenticalToTheUneditedMirror_AndNeverResolvesTheProvider(string format)
    {
        using var optedOut = Compose(optedIn: false);
        ReturnFromWiql(optedOut);
        var optedOutOutput = await QueryAsync(optedOut, format);

        optedOutOutput.ShouldNotContain("Local title");
        _provider.Calls.ShouldBe(0);
        DurableRows().ShouldBeEmpty();

        using var optedInUnedited = Compose(optedIn: true);
        ReturnFromWiql(optedInUnedited);
        (await QueryAsync(optedInUnedited, format)).ShouldBe(optedOutOutput);
    }

    [Fact]
    public async Task OptedIn_NullScope_QueryFallsBackToTheFetchedItems_WithoutProjecting()
    {
        _provider.ResolvedScope = null;
        using var services = Compose(optedIn: true);
        ReturnFromWiql(services);

        var titles = BatchTitles(await QueryAsync(services, "json"));

        titles[42].ShouldBe("Remote title");
        titles[43].ShouldBe("Untouched remote");
        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBeEmpty();
    }

    // ── nav back / fore / history ────────────────────────────────────

    private async Task VisitAsync(ServiceProvider services, params int[] ids)
    {
        var history = services.GetRequiredService<INavigationHistoryStore>();
        foreach (var id in ids)
            await history.RecordVisitAsync(id);
    }

    private static async Task<string> HistoryAsync(ServiceProvider services) =>
        StripVisitTimes(await RunNavAsync(services, c => c.HistoryAsync(nonInteractive: true, "human")));

    private static async Task<string> RunNavAsync(ServiceProvider services, Func<NavigationHistoryCommands, Task<int>> run)
    {
        var command = services.GetRequiredService<NavigationHistoryCommands>();
        var (exit, output) = await StdoutCapture.RunAsync(() => run(command));
        exit.ShouldBe(0);
        return output;
    }

    private static string StripVisitTimes(string output) =>
        Regex.Replace(output, @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}", "<visited>");

    [Fact]
    public async Task OptedIn_NavBackForeAndHistoryShowDurableLabels_WhileTargetsAndEntriesStayOnHistory()
    {
        using var services = Compose(optedIn: true);
        await SeedAsync(services, 42, "Remote title", "Local title");
        await SeedAsync(services, 43, "Untouched remote", "Local forward");
        await EditLocallyAsync(42, FieldValues.Empty
            .Set("System.Title", new TextValue("Local title"))
            .Set("System.State", new TextValue("Closed")));
        await EditLocallyAsync(43, FieldValues.Empty
            .Set("System.Title", new TextValue("Local forward"))
            .Set("System.State", new TextValue("New")));
        await VisitAsync(services, 42, 43);
        var before = DurableRows();

        var history = await HistoryAsync(services);
        history.ShouldContain("Local title");
        history.ShouldNotContain("Remote title");
        history.ShouldContain("Local forward");
        history.ShouldContain("[Closed]");

        var back = await RunNavAsync(services, c => c.BackAsync());
        back.ShouldStartWith("#42 ");
        back.ShouldContain("Local title");
        back.ShouldContain("[Closed]");
        (await services.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(42);

        var fore = await RunNavAsync(services, c => c.ForeAsync());
        fore.ShouldStartWith("#43 ");
        fore.ShouldContain("Local forward");
        fore.ShouldContain("[New]");
        (await services.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(43);

        var json = await RunNavAsync(services, c => c.HistoryAsync(nonInteractive: true, "json"));
        json.ShouldNotContain("title");

        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBe(before);
        var mirror = services.GetRequiredService<IWorkItemRepository>();
        (await mirror.GetByIdAsync(42))!.Title.ShouldBe("Remote title");
        (await mirror.GetByIdAsync(42))!.IsDirty.ShouldBeFalse();
    }

    [Theory]
    [InlineData("json")]
    [InlineData("minimal")]
    public async Task OptedIn_IdOnlyHistoryDoesNotReadLocalFields(string format)
    {
        using var services = Compose(optedIn: true);
        await SeedAsync(services, 42, "Remote title", "Local title");
        await VisitAsync(services, 42);
        using var optedOut = Compose(optedIn: false);
        var legacy = await RunNavAsync(optedOut, c => c.HistoryAsync(nonInteractive: true, format));
        CorruptLocalFields();
        var before = DurableRows();

        var output = await RunNavAsync(services, c => c.HistoryAsync(nonInteractive: true, format));

        output.ShouldBe(legacy, "id-only output stays byte-identical to the opted-out composition");
        output.ShouldContain("42");
        output.ShouldNotContain("Local title");
        await Should.ThrowAsync<InvalidDataException>(() => HistoryAsync(services));
        DurableRows().ShouldBe(before);
    }

    [Fact]
    public async Task OptedOut_NavHistoryIsByteIdenticalToTheUneditedMirror_AndNeverResolvesTheProvider()
    {
        using var optedOut = Compose(optedIn: false);
        await SeedAsync(optedOut, 42, "Remote title", localTitle: null);
        await SeedAsync(optedOut, 43, "Untouched remote", localTitle: null);
        await VisitAsync(optedOut, 42, 43);
        var optedOutHistory = await HistoryAsync(optedOut);
        var optedOutBack = await RunNavAsync(optedOut, c => c.BackAsync());

        optedOutHistory.ShouldContain("Remote title");
        optedOutBack.ShouldContain("Remote title");
        _provider.Calls.ShouldBe(0);
        DurableRows().ShouldBeEmpty();

        // The store is shared per test: restart history so the second composition replays the same visits.
        await optedOut.GetRequiredService<INavigationHistoryStore>().ClearAsync();
        using var optedInUnedited = Compose(optedIn: true);
        await SeedAsync(optedInUnedited, 42, "Remote title", localTitle: null);
        await SeedAsync(optedInUnedited, 43, "Untouched remote", localTitle: null);
        await VisitAsync(optedInUnedited, 42, 43);
        (await HistoryAsync(optedInUnedited)).ShouldBe(optedOutHistory);
        (await RunNavAsync(optedInUnedited, c => c.BackAsync())).ShouldBe(optedOutBack);
    }

    [Fact]
    public async Task OptedIn_NullScope_NavHistoryFallsBackToTheMirror_WithoutProjecting()
    {
        _provider.ResolvedScope = null;
        using var services = Compose(optedIn: true);
        await SeedAsync(services, 42, "Remote title", localTitle: null);
        await VisitAsync(services, 42);

        (await HistoryAsync(services)).ShouldContain("Remote title");
        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBeEmpty();
    }

    // ── nav down disambiguation ──────────────────────────────────────

    private static async Task<(int Exit, string Stderr)> DownAsync(ServiceProvider services, string? pattern)
    {
        var command = services.GetRequiredService<NavigationCommands>();
        return await StderrCapture.RunAsync(() => command.DownAsync(pattern, "human"));
    }

    private async Task<ServiceProvider> ComposeFamilyAsync(bool optedIn, bool editLocally)
    {
        var services = Compose(optedIn);
        await SeedAsync(services, 42, "Parent", localTitle: null);
        await SeedAsync(services, 43, "Remote child one", editLocally ? "Local child one" : null, parentId: 42);
        await SeedAsync(services, 44, "Remote child two", localTitle: null, parentId: 42);
        await services.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(42);
        return services;
    }

    [Fact]
    public async Task OptedIn_NavDownCandidatesShowDurableTitles_WhilePatternMatchingStaysOnTheMirror()
    {
        using var services = await ComposeFamilyAsync(optedIn: true, editLocally: true);
        var before = DurableRows();

        var (bareExit, bare) = await DownAsync(services, null);
        bareExit.ShouldBe(1);
        bare.ShouldContain("Local child one");
        bare.ShouldNotContain("Remote child one");
        bare.ShouldContain("Remote child two");

        var (patternExit, pattern) = await DownAsync(services, "Remote child");
        patternExit.ShouldBe(1);
        pattern.ShouldContain("Local child one");
        pattern.ShouldNotContain("Remote child one");

        var (localExit, local) = await DownAsync(services, "Local child");
        localExit.ShouldBe(1);
        local.ShouldContain("No child matches 'Local child'.");

        (await services.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(42);
        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBe(before);
    }

    [Fact]
    public async Task OptedOut_NavDownIsByteIdenticalToTheUneditedMirror_AndNeverResolvesTheProvider()
    {
        string bare, pattern;
        using (var optedOut = await ComposeFamilyAsync(optedIn: false, editLocally: false))
        {
            bare = (await DownAsync(optedOut, null)).Stderr;
            pattern = (await DownAsync(optedOut, "Remote child")).Stderr;
        }

        bare.ShouldContain("Remote child one");
        _provider.Calls.ShouldBe(0);
        DurableRows().ShouldBeEmpty();

        using var optedInUnedited = await ComposeFamilyAsync(optedIn: true, editLocally: false);
        (await DownAsync(optedInUnedited, null)).Stderr.ShouldBe(bare);
        (await DownAsync(optedInUnedited, "Remote child")).Stderr.ShouldBe(pattern);
    }

    [Fact]
    public async Task OptedIn_NullScope_NavDownFallsBackToTheMirror_WithoutProjecting()
    {
        _provider.ResolvedScope = null;
        using var services = await ComposeFamilyAsync(optedIn: true, editLocally: false);

        var (exit, output) = await DownAsync(services, null);

        exit.ShouldBe(1);
        output.ShouldContain("Remote child one");
        output.ShouldContain("Remote child two");
        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBeEmpty();
    }
}
