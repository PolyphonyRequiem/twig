using Bench.Domain;
using Bench.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.DependencyInjection;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task CachedShowWithholdsAnItemAfterAnExactRefreshIsRefused()
    {
        var (paths, _, _, _, _) = await WorkspaceReadScopeAsync();
        HistoricalInvoke(paths, ["show", "41", "-o", "json"]).Output.ShouldContain("native 41");
        _transport.SetItem(41, "not readable", 2, Sibling);
        HistoricalInvoke(paths, ["show", "41", "--refresh", "-o", "json"]).Exit.ShouldBe(1);

        var refused = HistoricalInvoke(paths, ["show", "41", "-o", "json"]);
        refused.Exit.ShouldBe(1);
        refused.Output.ShouldBeEmpty();
        HistoricalInvoke(paths, ["show", "43", "-o", "json"]).Output.ShouldContain("query member");

        _transport.SetItem(41, "visible again", 3, Actor);
        HistoricalInvoke(paths, ["show", "41", "--refresh", "-o", "json"]).Output.ShouldContain("visible again");
        HistoricalInvoke(paths, ["show", "41", "-o", "json"]).Output.ShouldContain("visible again");
    }

    [Fact]
    public async Task CachedShowAndBenchRefuseAnExactSourceDenialWithoutProviderRequests()
    {
        var (paths, connection, store, _, _) = await WorkspaceReadScopeAsync();
        var state = await WorkspaceStoredDocumentAsync(paths);
        var evidence = await HistoricalEvidenceFileAsync(paths);
        await File.WriteAllBytesAsync(evidence + ".denied", [1]);
        var requests = _transport.TotalRequests;

        var shown = HistoricalInvoke(paths, ["show", "41", "-o", "json"]);
        shown.Exit.ShouldBe(1);
        shown.Output.ShouldBeEmpty();
        var bench = HistoricalInvoke(paths, ["workspace", "--bench", "Read scope", "-o", "json"]);
        bench.Exit.ShouldBe(1);
        bench.Output.ShouldBeEmpty();
        _transport.TotalRequests.ShouldBe(requests);
        (await WorkspaceStoredDocumentAsync(paths)).ShouldBe(state);
        (await store.ReadAsync(connection, default)).Value.RetainedItems.ShouldNotBeEmpty();

        _transport.SetItem(41, "fresh exact item", 2, Actor);
        var refreshed = HistoricalInvoke(paths, ["show", "41", "--refresh", "-o", "json"]);
        refreshed.Exit.ShouldBe(0, refreshed.Error);
        refreshed.Output.ShouldContain("fresh exact item");
        var stillWithheld = HistoricalInvoke(paths, ["show", "41"]);
        stillWithheld.Exit.ShouldBe(1);
        stillWithheld.Output.ShouldBeEmpty();
    }

    [Fact]
    public async Task CachedShowAndBenchSuppressWithdrawnItemsAndDraftsWithoutDeletingThem()
    {
        var (paths, _, _, _, _) = await WorkspaceReadScopeAsync();
        var state = await WorkspaceStoredDocumentAsync(paths);
        var evidence = await HistoricalEvidenceFileAsync(paths);
        await File.WriteAllTextAsync(evidence + ".withdrawn", """["41","44","45"]""");
        var requests = _transport.TotalRequests;

        var shown = HistoricalInvoke(paths, ["show", "41", "-o", "json"]);
        shown.Exit.ShouldBe(1);
        shown.Output.ShouldBeEmpty();
        HistoricalInvoke(paths, ["show", "43", "-o", "json"]).Output.ShouldContain("query member");
        var tree = HistoricalInvoke(paths, ["tree", "44", "-o", "json"]);
        tree.Exit.ShouldBe(1);
        tree.Output.ShouldBeEmpty();
        _transport.TotalRequests.ShouldBe(requests);

        var bench = HistoricalInvoke(paths, ["workspace", "--bench", "Read scope", "-o", "ids"]);
        bench.Exit.ShouldBe(1, "a withdrawn subtree member makes the hierarchy evidence incomplete");
        bench.Output.ShouldBeEmpty();
        await File.WriteAllTextAsync(evidence + ".withdrawn", """["41","45"]""");
        var remaining = HistoricalInvoke(paths, ["workspace", "--bench", "Read scope", "-o", "json"]);
        remaining.Exit.ShouldBe(0, remaining.Error);
        remaining.Output.ShouldContain("query member");
        remaining.Output.ShouldContain("Unpublished debt");
        remaining.Output.ShouldNotContain("native 41");
        remaining.Output.ShouldNotContain("Proposed debt");
        (await WorkspaceStoredDocumentAsync(paths)).ShouldBe(state);
    }

    [Fact]
    public async Task CachedShowBatchDoesNotExportWithdrawnFields()
    {
        var (paths, _, _, _, _) = await WorkspaceReadScopeAsync();
        var evidence = await HistoricalEvidenceFileAsync(paths);
        await File.WriteAllTextAsync(evidence + ".withdrawn", """["41"]""");
        var requests = _transport.TotalRequests;
        var batch = HistoricalInvoke(paths, ["show-batch", "41,43", "--include-fields", "-o", "json"]);
        batch.Exit.ShouldBe(1);
        batch.Output.ShouldNotContain("native 41");
        batch.Output.ShouldContain("query member");
        _transport.TotalRequests.ShouldBe(requests);
    }

    private string HistoricalEvidenceDirectory => Path.Combine(Home, "ServerData", "native-evidence");

    private (int Exit, string Output, string Error) HistoricalInvoke(Twig.Infrastructure.Config.TwigPaths paths, string[] args)
        => WorkspaceInvoke(paths, args, configure: services =>
            services.AddSingleton(new NativeHistoricalEvidence(HistoricalEvidenceDirectory)));

    private async Task<string> HistoricalEvidenceFileAsync(Twig.Infrastructure.Config.TwigPaths paths)
    {
        var services = new ServiceCollection();
        services.AddSingleton<Twig.Infrastructure.Auth.IConnectionBindingService>(_bindings);
        services.AddSingleton(new CoreTransportOverride(_transport.NewHandle));
        services.AddTwigWorkspaceServices(paths.StartDir, ["show"]);
        using var runtime = services.BuildServiceProvider();
        using var handle = (await runtime.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        Directory.CreateDirectory(HistoricalEvidenceDirectory);
        return Path.Combine(HistoricalEvidenceDirectory, CatalogFingerprint.Binding(NativeSourceAdapter.Binding(handle.Source)) + ".json");
    }
}
