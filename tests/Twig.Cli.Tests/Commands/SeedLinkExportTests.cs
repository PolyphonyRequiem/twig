using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Cli.Tests.Browser;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed class SeedLinkExportTests
{
    [Theory]
    [InlineData("export")]
    [InlineData("batch")]
    [InlineData("batch-projection")]
    [InlineData("show")]
    [InlineData("show-projection")]
    public async Task ProductionReads_IncludeBothDirectionsOfLocalSeedLinks(string mode)
    {
        await using var fixture = new NativeBrowserFixture();
        await fixture.InitializeNewStoreAsync();
        using var runtime = await fixture.CreateRuntimeAsync();
        var items = runtime.GetRequiredService<IWorkItemRepository>();
        var registry = runtime.GetRequiredService<IStagedIdentityRegistry>();
        var staged = await registry.MintAsync();
        var id = staged.Alias.Value;
        var seed = new WorkItem
        {
            Id = id, Title = "Export seed", Type = WorkItemType.Task, State = "New",
            IsSeed = true, StagedIdentity = staged.Identity, ParentId = 42,
        };
        await items.SaveAsync(seed);
        var seedLinks = runtime.GetRequiredService<ISeedLinkRepository>();
        SeedLink[] intended =
        [
            new(id, 42, SeedLinkTypes.ParentChild, DateTimeOffset.UnixEpoch),
            new(id, -5, SeedLinkTypes.BlockedBy, DateTimeOffset.UnixEpoch),
            new(-6, id, SeedLinkTypes.Blocks, DateTimeOffset.UnixEpoch),
        ];
        foreach (var link in intended)
            await seedLinks.AddLinkAsync(link);
        (await seedLinks.GetLinksForItemAsync(id)).Count.ShouldBe(3);
        (await items.GetByIdAsync(-5)).ShouldBeNull("export must not need endpoint bodies");
        (await items.GetByIdAsync(-6)).ShouldBeNull();
        var mutations = fixture.Transport.Mutations;
        var show = runtime.GetRequiredService<ShowCommand>();

        var (exit, output) = await StdoutCapture.RunAsync(() => mode switch
        {
            "export" => show.ExecuteBatchAsync(id.ToString(), "json", includeFields: true),
            "batch" => show.ExecuteBatchAsync(id.ToString(), "json"),
            "batch-projection" => show.ExecuteBatchAsync(id.ToString(), "json", sections: "links"),
            "show-projection" => show.ExecuteAsync(id, "json", sections: "links"),
            _ => show.ExecuteAsync(id, "json"),
        });

        exit.ShouldBe(0);
        using var document = JsonDocument.Parse(output);
        var root = document.RootElement;
        var item = mode switch
        {
            "export" or "batch-projection" => root.GetProperty("items")[0],
            "batch" => root[0],
            _ => root,
        };
        var links = mode.Contains("projection", StringComparison.Ordinal)
            ? item.GetProperty("requestedSections").GetProperty("links").GetProperty("items")
            : item.GetProperty("links");
        var actual = links.EnumerateArray().Select(link => (
            link.GetProperty("sourceId").GetInt32(),
            link.GetProperty("targetId").GetInt32(),
            link.GetProperty("linkType").GetString())).ToArray();
        actual.Order().ShouldBe(intended.Select(link =>
            (link.SourceId, link.TargetId, (string?)link.LinkType)).Order());
        var freshness = mode == "export" || mode.Contains("projection", StringComparison.Ordinal)
            ? item.GetProperty("freshness") : item;
        freshness.GetProperty("linksVerifiedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        if (mode is "show" or "batch")
        {
            item.GetProperty("isSeed").GetBoolean().ShouldBeTrue();
            item.GetProperty("relations").EnumerateArray().Select(relation => (
                relation.GetProperty("id").GetInt32(), relation.GetProperty("rel").GetString())).Order()
                .ShouldBe(new[]
                {
                    (-6, (string?)"System.LinkTypes.Dependency-Reverse"),
                    (-5, (string?)"System.LinkTypes.Dependency-Reverse"),
                    (42, (string?)"System.LinkTypes.Hierarchy-Reverse"),
                }.Order());
        }
        fixture.Transport.Mutations.ShouldBe(mutations);
        (await seedLinks.GetLinksForItemAsync(id)).ShouldBe(intended, ignoreOrder: true);
    }
}
