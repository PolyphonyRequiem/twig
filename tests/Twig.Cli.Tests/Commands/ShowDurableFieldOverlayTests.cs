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
/// Epic #1168: with the neutral store opted in, <c>twig show</c> resolved through the real command
/// registration displays durable local values over the disposable mirror, sharing the mirror
/// projection's single scope resolution. Opted out it shows the mirror and never resolves the provider.
/// </summary>
public sealed class ShowDurableFieldOverlayTests : IDisposable
{
    private const string Scope = "11111111-2222-3333-4444-555555555555";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "twig-show-overlay-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteCacheStore _store = new("Data Source=:memory:");
    private readonly CountingProvider _provider = new();

    public ShowDurableFieldOverlayTests() => Directory.CreateDirectory(_root);

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

    private ServiceProvider Compose(bool optedIn)
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
        services.AddSingleton(Substitute.For<IAdoWorkItemService>());
        services.AddSingleton(Substitute.For<IIterationService>());
        services.AddSingleton<IWorkItemRepository>(sp => TwigServiceRegistration.CreateMirrorRepository(sp, optedIn));
        services.AddTwigRenderingServices();
        services.AddTwigCommandServices();
        services.AddTwigCommands();
        return services.BuildServiceProvider();
    }

    private async Task<string> ShowJsonAsync(ServiceProvider services)
    {
        var command = services.GetRequiredService<ShowCommand>();
        var (exit, output) = await StdoutCapture.RunAsync(() => command.ExecuteAsync(42, "json"));
        exit.ShouldBe(0);
        return output;
    }

    private static string Title(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("title").GetString()!;
    }

    private Task SeedAsync(ServiceProvider services, bool editLocally) =>
        SeedAsync(services, 42, "Remote title", editLocally ? "Local title" : null);

    private async Task SeedAsync(ServiceProvider services, int itemId, string remoteTitle, string? localTitle)
    {
        var item = new WorkItemBuilder(itemId, remoteTitle).InState("Active").Build();
        item.MarkSynced(3);
        await services.GetRequiredService<IWorkItemRepository>().SaveAsync(item);
        if (localTitle is null) return;

        var local = new SqliteLocalWorkItemRepository(_store);
        var id = (await local.FindAsync(new ExternalWorkItemIdentity("ado", Scope, itemId.ToString(System.Globalization.CultureInfo.InvariantCulture))))!.Value;
        var current = (await local.GetAsync(id))!;
        (await local.UpdateAsync(id, current.Revision, FieldValues.Empty.Set("System.Title", new TextValue(localTitle))))
            .Value.ShouldBeOfType<LocalWriteApplied>();
    }

    private async Task<string> ShowBatchJsonAsync(ServiceProvider services, string batch = "42,43")
    {
        var command = services.GetRequiredService<ShowCommand>();
        var (exit, output) = await StdoutCapture.RunAsync(() => command.ExecuteBatchAsync(batch, "json"));
        exit.ShouldBe(0);
        return output;
    }

    private static Dictionary<long, string> BatchTitles(string json)
    {
        var titles = new Dictionary<long, string>();
        using var doc = JsonDocument.Parse(json);
        Collect(doc.RootElement);
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

    private List<(long Revision, string UpdatedAt, string FieldsJson)> DurableRows()
    {
        using var cmd = _store.GetConnection().CreateCommand();
        cmd.CommandText = "SELECT revision, updated_at, fields_json FROM pending.local_work_items ORDER BY local_id";
        using var reader = cmd.ExecuteReader();
        var rows = new List<(long, string, string)>();
        while (reader.Read())
            rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    [Fact]
    public async Task OptedIn_ShowBatchDisplaysDurableValuesPerItem_WithoutWritingBack()
    {
        using var services = Compose(optedIn: true);
        await SeedAsync(services, 42, "Remote title", "Local title");
        await SeedAsync(services, 43, "Untouched remote", localTitle: null);
        var before = DurableRows();

        var titles = BatchTitles(await ShowBatchJsonAsync(services));
        BatchTitles(await ShowBatchJsonAsync(services)).ShouldBe(titles);

        titles[42].ShouldBe("Local title");
        titles[43].ShouldBe("Untouched remote");
        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBe(before);
        var mirror = services.GetRequiredService<IWorkItemRepository>();
        (await mirror.GetByIdAsync(42))!.Title.ShouldBe("Remote title");
        (await mirror.GetByIdAsync(42))!.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task OptedOut_ShowBatchIsByteIdenticalToTheUneditedMirror_AndNeverResolvesTheProvider()
    {
        using var optedOut = Compose(optedIn: false);
        await SeedAsync(optedOut, 42, "Remote title", localTitle: null);
        await SeedAsync(optedOut, 43, "Untouched remote", localTitle: null);
        var optedOutOutput = await ShowBatchJsonAsync(optedOut);

        BatchTitles(optedOutOutput)[42].ShouldBe("Remote title");
        _provider.Calls.ShouldBe(0);
        DurableRows().ShouldBeEmpty();

        using var optedInUnedited = Compose(optedIn: true);
        await SeedAsync(optedInUnedited, 42, "Remote title", localTitle: null);
        await SeedAsync(optedInUnedited, 43, "Untouched remote", localTitle: null);
        (await ShowBatchJsonAsync(optedInUnedited)).ShouldBe(optedOutOutput);
    }

    [Fact]
    public async Task OptedIn_NullScope_ShowBatchFallsBackToTheMirror_WithoutProjecting()
    {
        _provider.ResolvedScope = null;
        using var services = Compose(optedIn: true);
        await SeedAsync(services, 42, "Remote title", localTitle: null);
        await SeedAsync(services, 43, "Untouched remote", localTitle: null);

        var titles = BatchTitles(await ShowBatchJsonAsync(services));

        titles[42].ShouldBe("Remote title");
        titles[43].ShouldBe("Untouched remote");
        _provider.Calls.ShouldBe(1);
        DurableRows().ShouldBeEmpty();
    }

    [Fact]
    public async Task OptedIn_ShowDisplaysTheDurableValue_WithOneScopeResolution()
    {
        using var services = Compose(optedIn: true);
        await SeedAsync(services, editLocally: true);

        Title(await ShowJsonAsync(services)).ShouldBe("Local title");
        Title(await ShowJsonAsync(services)).ShouldBe("Local title");

        _provider.Calls.ShouldBe(1);
        (await services.GetRequiredService<IWorkItemRepository>().GetByIdAsync(42))!.Title.ShouldBe("Remote title");
    }

    [Fact]
    public async Task OptedOut_ShowDisplaysTheMirror_AndNeverResolvesTheProvider()
    {
        using var services = Compose(optedIn: false);
        await SeedAsync(services, editLocally: false);

        Title(await ShowJsonAsync(services)).ShouldBe("Remote title");

        services.GetRequiredService<DurableFieldOverlay>().IsEnabled.ShouldBeFalse();
        _provider.Calls.ShouldBe(0);
    }
}
