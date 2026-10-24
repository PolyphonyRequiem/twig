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
        public ProviderCapabilityDescriptor Capabilities => ProviderCapabilityCatalog.Ado;

        public Task<string?> ResolveResourceScopeAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<string?>(Scope);
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

    private async Task SeedAsync(ServiceProvider services, bool editLocally)
    {
        var item = new WorkItemBuilder(42, "Remote title").InState("Active").Build();
        item.MarkSynced(3);
        await services.GetRequiredService<IWorkItemRepository>().SaveAsync(item);
        if (!editLocally) return;

        var local = new SqliteLocalWorkItemRepository(_store);
        var id = (await local.FindAsync(new ExternalWorkItemIdentity("ado", Scope, "42")))!.Value;
        var current = (await local.GetAsync(id))!;
        (await local.UpdateAsync(id, current.Revision, FieldValues.Empty.Set("System.Title", new TextValue("Local title"))))
            .Value.ShouldBeOfType<LocalWriteApplied>();
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
