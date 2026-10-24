using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Hints;
using Twig.Infrastructure.Config;
using Twig.Rendering;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// AB#1170: every link <c>twig show</c> / <c>show-batch</c> emits carries an <c>origin</c> derived
/// from the active provider's capability descriptor — <c>provider-native</c> only when the provider
/// declares that kind native, and <c>null</c> (unknown) otherwise. There is no constant fallback:
/// without a provider seam the origin is unknown, never assumed.
/// </summary>
public sealed class ShowLinkOriginTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly IWorkItemLinkRepository _linkRepo = Substitute.For<IWorkItemLinkRepository>();

    public ShowLinkOriginTests()
    {
        foreach (var id in new[] { 10, 20 })
            _workItemRepo.GetByIdAsync(id, Arg.Any<CancellationToken>())
                .Returns(new WorkItemBuilder(id, $"Item {id}").Build());

        var links = new[]
        {
            new WorkItemLink(10, 200, LinkTypes.Predecessor),
            new WorkItemLink(10, 201, LinkTypes.Related),
        };
        _linkRepo.GetLinksForSetAsync(Arg.Any<IReadOnlyList<int>>(), Arg.Any<CancellationToken>()).Returns(links);
        _linkRepo.GetLinksAsync(10, Arg.Any<CancellationToken>()).Returns(links);
    }

    private ShowCommand CreateCommand(ProviderCapabilityDescriptor? descriptor)
    {
        var adoService = Substitute.For<IAdoWorkItemService>();
        var pendingChangeStore = Substitute.For<IPendingChangeStore>();
        var protectedCacheWriter = new ProtectedCacheWriter(_workItemRepo, pendingChangeStore);
        var syncCoordinatorFactory = new SyncCoordinatorFactory(
            _workItemRepo, adoService, protectedCacheWriter, pendingChangeStore, _linkRepo, 30, 30);

        var formatterFactory = new OutputFormatterFactory(new HumanOutputFormatter());
        var hintEngine = new HintEngine(new DisplayConfig { Hints = false });
        var pipelineFactory = new RenderingPipelineFactory(formatterFactory, null!, isOutputRedirected: () => true);
        var ctx = new CommandContext(pipelineFactory, formatterFactory, hintEngine, new TwigConfiguration(),
            TelemetryClient: Substitute.For<ITelemetryClient>());

        var tempDir = Path.Combine(Path.GetTempPath(), "twig-showlinkorigin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var statusFieldReader = new StatusFieldConfigReader(
            new TwigPaths(tempDir, Path.Combine(tempDir, "config"), Path.Combine(tempDir, "twig.db")));

        IWorkItemProvider? provider = null;
        if (descriptor is not null)
        {
            provider = Substitute.For<IWorkItemProvider>();
            provider.Capabilities.Returns(descriptor);
        }

        return new ShowCommand(ctx, _workItemRepo, _linkRepo, syncCoordinatorFactory, statusFieldReader)
        {
            WorkItemProvider = provider,
        };
    }

    private static Dictionary<int, string?> OriginsByTarget(JsonElement item)
        => item.GetProperty("links").EnumerateArray().ToDictionary(
            l => l.GetProperty("targetId").GetInt32(),
            l => l.GetProperty("origin").ValueKind == JsonValueKind.Null ? null : l.GetProperty("origin").GetString());

    private static JsonElement BatchItem(string json, int id)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Single(e => e.GetProperty("id").GetInt32() == id).Clone();
    }

    [Fact]
    public async Task ShowBatch_Json_OriginFollowsDescriptor()
    {
        var (_, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(ProviderCapabilityCatalog.GitHub).ExecuteBatchAsync("10,20", "json"));

        var origins = OriginsByTarget(BatchItem(output, 10));
        origins[200].ShouldBe("provider-native");
        origins[201].ShouldBeNull();
    }

    [Fact]
    public async Task ShowBatch_Json_NoProvider_OriginIsUnknownNotAssumed()
    {
        var (_, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(null).ExecuteBatchAsync("10,20", "json"));

        var origins = OriginsByTarget(BatchItem(output, 10));
        origins[200].ShouldBeNull();
        origins[201].ShouldBeNull();
    }

    [Fact]
    public async Task ShowBatch_LinksSection_OriginFollowsDescriptor()
    {
        var (_, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(ProviderCapabilityCatalog.Ado).ExecuteBatchAsync("10,20", "json", sections: "links"));

        output.ShouldContain("origin");
        output.ShouldContain("provider-native");
    }

    [Fact]
    public async Task Show_Json_OriginFollowsDescriptor()
    {
        var (_, output) = await StdoutCapture.RunAsync(
            () => CreateCommand(ProviderCapabilityCatalog.Ado).ExecuteAsync(10, "json"));

        using var doc = JsonDocument.Parse(output);
        var origins = OriginsByTarget(doc.RootElement);
        origins[200].ShouldBe("provider-native");
        origins[201].ShouldBe("provider-native");
    }
}
