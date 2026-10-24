using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.DependencyInjection;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Domain.ValueObjects.Fields;
using Twig.Infrastructure;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// Durable field overlays affect rendered query/navigation labels only; selection, identifiers,
/// history, and navigation targets remain owned by the mirror and history store.
/// </summary>
public sealed class DurableFieldOverlayQueryNavTests : IDisposable
{
    private const string Scope = "11111111-2222-3333-4444-555555555555";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "twig-query-nav-overlay-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly CountingProvider _provider = new();
    private readonly IProviderWorkItemReader _reader = Substitute.For<IProviderWorkItemReader>();

    public DurableFieldOverlayQueryNavTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private sealed class CountingProvider : IWorkItemProvider
    {
        public int Calls { get; private set; }
        public string? ResolvedScope { get; set; } = Scope;
        public ProviderCapabilityDescriptor Capabilities => ProviderCapabilityCatalog.Ado;

        public Task<string?> ResolveResourceScopeAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(ResolvedScope);
        }
    }

    private ServiceProvider Compose()
    {
        var twigDir = Path.Combine(_root, ".twig");
        Directory.CreateDirectory(twigDir);
        var config = new TwigConfiguration();
        var paths = new TwigPaths(twigDir, Path.Combine(twigDir, "config"), Path.Combine(twigDir, "twig.db"));

        var services = new ServiceCollection();
        services.AddConnectionServices(config, twigDir, _root);
        services.AddSingleton(paths);
        services.AddSingleton(_store);
        services.AddSingleton<IWorkItemProvider>(_provider);
        services.AddSingleton<IProviderWorkItemReader>(_reader);
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return services.BuildServiceProvider();
    }

    private static Twig.Domain.Aggregates.WorkItem Remote(int id, string title, int? parentId = null)
    {
        var builder = new WorkItemBuilder(id, title).InState("Active").WithIterationPath(@"Project\Sprint 1");
        if (parentId is not null)
            builder = builder.WithParent(parentId.Value);
        var item = builder.Build();
        item.MarkSynced(3);
        return item;
    }

    private async Task SeedAsync(
        ServiceProvider services, int itemId, string remoteTitle, string? localTitle = null, string? localState = null,
        int? parentId = null)
    {
        var item = Remote(itemId, remoteTitle, parentId);
        await services.GetRequiredService<IWorkItemRepository>().SaveAsync(item);
        if (localTitle is null && localState is null)
            return;

        var identity = new ExternalWorkItemIdentity("ado", Scope, itemId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var localId = (await services.GetRequiredService<IExternalIdentityMap>().FindAsync(identity))!.Value;
        var localStore = services.GetRequiredService<ILocalWorkItemStore>();
        var current = (await localStore.GetAsync(localId))!;
        var fields = FieldValues.Empty;
        if (localTitle is not null)
            fields = fields.Set("System.Title", new TextValue(localTitle));
        if (localState is not null)
            fields = fields.Set("System.State", new TextValue(localState));
        (await localStore.UpdateAsync(localId, current.Revision, fields)).Value.ShouldBeOfType<LocalWriteApplied>();
    }

    private void ReturnFromWiql()
    {
        _reader.QueryByWiqlAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<int> { 43, 42 });
        _reader.FetchBatchAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<Twig.Domain.Aggregates.WorkItem>
            {
                Remote(43, "Untouched remote"),
                Remote(42, "Remote title"),
            });
    }

    private static async Task<string> QueryAsync(ServiceProvider services, string format)
    {
        var (exit, output) = await StdoutCapture.RunAsync(() =>
            services.GetRequiredService<QueryCommand>().ExecuteAsync(title: "title", top: 25, outputFormat: format));
        exit.ShouldBe(0);
        return output;
    }

    private async Task<string> QueryWithoutOverlayAsync(ServiceProvider services, string format)
    {
        var command = new QueryCommand(
            _reader,
            services.GetRequiredService<IWorkItemRepository>(),
            services.GetRequiredService<TwigConfiguration>(),
            services.GetRequiredService<Twig.Formatters.OutputFormatterFactory>(),
            services.GetRequiredService<Twig.Hints.HintEngine>(),
            services.GetService<Twig.Rendering.RendererFactory>(),
            services.GetService<ITelemetryClient>());
        var (exit, output) = await StdoutCapture.RunAsync(() =>
            command.ExecuteAsync(title: "title", top: 25, outputFormat: format));
        exit.ShouldBe(0);
        return output;
    }

    private static Dictionary<long, string> QueryTitles(string json)
    {
        var titles = new Dictionary<long, string>();
        using var document = JsonDocument.Parse(json);
        Collect(document.RootElement);
        return titles;

        void Collect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                if (element.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number
                    && element.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                    titles[id.GetInt64()] = title.GetString()!;
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

    private static List<long> QueryIdsInOrder(string json)
    {
        var ids = new List<long>();
        using var document = JsonDocument.Parse(json);
        Collect(document.RootElement);
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

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out var property))
                return property;
            foreach (var child in element.EnumerateObject())
            {
                var result = FindProperty(child.Value, name);
                if (result.HasValue)
                    return result;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var result = FindProperty(child, name);
                if (result.HasValue)
                    return result;
            }
        }

        return null;
    }

    private List<(long Revision, string UpdatedAt, string FieldsJson)> DurableRows()
    {
        using var command = _store.GetConnection().CreateCommand();
        command.CommandText = "SELECT revision, updated_at, fields_json FROM pending.local_work_items ORDER BY local_id";
        using var reader = command.ExecuteReader();
        var rows = new List<(long, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    [Fact]
    public async Task QueryJsonOverlaysFieldsWithoutChangingSelectionOrderCountOrIds()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        await SeedAsync(services, 43, "Untouched remote");
        ReturnFromWiql();
        var before = DurableRows();

        var json = await QueryAsync(services, "json");

        var titles = QueryTitles(json);
        titles[42].ShouldBe("Local title");
        titles[43].ShouldBe("Untouched remote");
        QueryIdsInOrder(json).ShouldBe(new long[] { 43, 42 });
        using var document = JsonDocument.Parse(json);
        FindProperty(document.RootElement, "count")!.Value.GetInt32().ShouldBe(2);
        FindProperty(document.RootElement, "truncated")!.Value.GetBoolean().ShouldBeFalse();
        (await QueryAsync(services, "ids")).ShouldBe("43\n42" + Environment.NewLine);
        DurableRows().ShouldBe(before);
        (await services.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Remote title");
    }

    [Theory]
    [InlineData("json")]
    [InlineData("human")]
    [InlineData("minimal")]
    [InlineData("ids")]
    public async Task QueryWithoutLocalOverridesIsByteIdenticalWithoutOverlay(string format)
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title");
        await SeedAsync(services, 43, "Untouched remote");
        ReturnFromWiql();

        var composed = await QueryAsync(services, format);
        var optedOut = await QueryWithoutOverlayAsync(services, format);

        optedOut.ShouldBe(composed);
    }

    [Fact]
    public async Task QueryIdsDoNotReadCorruptLocalFieldsButRenderedCellsDo()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        await SeedAsync(services, 43, "Untouched remote");
        ReturnFromWiql();
        using (var command = _store.GetConnection().CreateCommand())
        {
            command.CommandText = "UPDATE pending.local_work_items SET fields_json = 'null'";
            command.ExecuteNonQuery().ShouldBeGreaterThan(0);
        }

        (await QueryAsync(services, "ids")).ShouldBe("43\n42" + Environment.NewLine);
        await Should.ThrowAsync<InvalidDataException>(() => QueryAsync(services, "json"));
    }

    [Fact]
    public async Task OverlayFallsBackToTheMirrorWhenScopeResolutionReturnsNull()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        _provider.ResolvedScope = null;

        var remote = (await services.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!;
        var shown = await services.GetRequiredService<DurableFieldOverlay>().ApplyAsync(remote);

        shown.Title.ShouldBe("Remote title");
    }

    [Fact]
    public async Task NavigationDisplaysDurableLabelsWithoutChangingPatternMatchingOrTarget()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Parent");
        await SeedAsync(services, 43, "Remote child one", "Local child one", parentId: 42);
        await SeedAsync(services, 44, "Remote child two", parentId: 42);
        await services.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(42);
        var command = services.GetRequiredService<NavigationCommands>();

        var (bareExit, bare) = await StderrCapture.RunAsync(() => command.DownAsync(outputFormat: "human"));
        bareExit.ShouldBe(1);
        bare.ShouldContain("Local child one");
        bare.ShouldNotContain("Remote child one");

        var (patternExit, pattern) = await StderrCapture.RunAsync(() =>
            command.DownAsync("Remote child", "human"));
        patternExit.ShouldBe(1);
        pattern.ShouldContain("Local child one");
        pattern.ShouldContain("Remote child two");

        var (localExit, local) = await StderrCapture.RunAsync(() =>
            command.DownAsync("Local child", "human"));
        localExit.ShouldBe(1);
        local.ShouldContain("No child matches 'Local child'.");
        (await services.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(42);
    }

    [Fact]
    public async Task NavigationHistoryDisplaysDurableLabelsButPreservesTargetsAndHistoryIds()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title", localState: "Closed");
        await SeedAsync(services, 43, "Remote forward", "Local forward", localState: "New");
        var history = services.GetRequiredService<INavigationHistoryStore>();
        await history.RecordVisitAsync(42);
        await history.RecordVisitAsync(43);
        var command = services.GetRequiredService<NavigationHistoryCommands>();

        var (historyExit, renderedHistory) = await StdoutCapture.RunAsync(() =>
            command.HistoryAsync(nonInteractive: true, outputFormat: "human"));
        historyExit.ShouldBe(0);
        renderedHistory.ShouldContain("Local title");
        renderedHistory.ShouldContain("Local forward");
        renderedHistory.ShouldContain("[Closed]");

        var (backExit, back) = await StdoutCapture.RunAsync(() => command.BackAsync());
        backExit.ShouldBe(0);
        back.ShouldStartWith("#42 ");
        back.ShouldContain("Local title");
        (await services.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(42);

        var (foreExit, fore) = await StdoutCapture.RunAsync(() => command.ForeAsync());
        foreExit.ShouldBe(0);
        fore.ShouldStartWith("#43 ");
        fore.ShouldContain("Local forward");
        (await services.GetRequiredService<IContextStore>().GetActiveWorkItemIdAsync()).ShouldBe(43);

        var (jsonExit, json) = await StdoutCapture.RunAsync(() =>
            command.HistoryAsync(nonInteractive: true, outputFormat: "json"));
        jsonExit.ShouldBe(0);
        json.ShouldNotContain("title");
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("entries").EnumerateArray()
            .Select(entry => entry.GetProperty("workItemId").GetInt32())
            .ShouldBe(new[] { 42, 43 });
    }

    [Fact]
    public async Task NavigationHistoryIdOnlyOutputDoesNotReadCorruptLocalFields()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Remote title", "Local title");
        var history = services.GetRequiredService<INavigationHistoryStore>();
        await history.RecordVisitAsync(42);
        using (var command = _store.GetConnection().CreateCommand())
        {
            command.CommandText = "UPDATE pending.local_work_items SET fields_json = 'null'";
            command.ExecuteNonQuery().ShouldBeGreaterThan(0);
        }

        var (minimalExit, minimal) = await StdoutCapture.RunAsync(() =>
            services.GetRequiredService<NavigationHistoryCommands>()
                .HistoryAsync(nonInteractive: true, outputFormat: "minimal"));

        minimalExit.ShouldBe(0);
        minimal.Trim().ShouldBe("42");
    }

    [Fact]
    public async Task NavigationFallsBackToMirrorLabelsWhenScopeResolutionReturnsNull()
    {
        using var services = Compose();
        await SeedAsync(services, 42, "Parent");
        await SeedAsync(services, 43, "Remote child one", "Local child one", parentId: 42);
        await SeedAsync(services, 44, "Remote child two", parentId: 42);
        await services.GetRequiredService<IContextStore>().SetActiveWorkItemIdAsync(42);
        _provider.ResolvedScope = null;

        var (exit, output) = await StderrCapture.RunAsync(() =>
            services.GetRequiredService<NavigationCommands>().DownAsync(outputFormat: "human"));

        exit.ShouldBe(1);
        output.ShouldContain("Remote child one");
        output.ShouldNotContain("Local child one");
        output.ShouldContain("Remote child two");
    }
}
