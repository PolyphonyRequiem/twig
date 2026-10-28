using System.Text.Json;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Services;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Plan;

public sealed partial class PlanLifecycleServiceTests
{
    private const string LegacySeedEffect = """{"kind":"seed-publish","field":null,"to":null,"fieldLabel":null,"fieldType":null,"before":null,"textChange":null,"relation":null,"otherId":null}""";

    [Theory]
    [InlineData("blocks", "blocked-by")]
    [InlineData("blocked-by", "blocks")]
    [InlineData("depends-on", "depended-on-by")]
    [InlineData("depended-on-by", "depends-on")]
    [InlineData("predecessor", "successor")]
    [InlineData("successor", "predecessor")]
    [InlineData("related", "related")]
    public async Task SeedReview_PreviewReadsLocalLinks_ReversesAndDeduplicates(string relation, string reverse)
    {
        var seed = BuildSeed(-42, StagedIdentity.New(), "Linked draft", "Task");
        var items = new SqliteWorkItemRepository(_store, new WorkItemMapper());
        var links = new SqliteSeedLinkRepository(_store);
        await items.SaveAsync(seed);
        await links.AddLinkAsync(new SeedLink(seed.Id, 17, relation, DateTimeOffset.UnixEpoch));
        await links.AddLinkAsync(new SeedLink(18, seed.Id, relation, DateTimeOffset.UnixEpoch));
        await links.AddLinkAsync(new SeedLink(17, seed.Id, reverse, DateTimeOffset.UnixEpoch));
        await links.AddLinkAsync(new SeedLink(seed.Id, 19, SeedLinkTypes.ParentChild, DateTimeOffset.UnixEpoch));
        await links.AddLinkAsync(new SeedLink(20, seed.Id, SeedLinkTypes.ParentChild, DateTimeOffset.UnixEpoch));
        await links.AddLinkAsync(new SeedLink(-99, 22, SeedLinkTypes.Related, DateTimeOffset.UnixEpoch));
        (await links.GetLinksForItemAsync(seed.Id)).Count.ShouldBe(5);
        var fingerprint = await SeedFingerprintCalculator.ComputeAsync(seed,
            await links.GetLinksForItemAsync(seed.Id), _stagedRegistry, _publishIdMap);
        var source = PublishSeedPlan(seed.StagedIdentity!.Value, fingerprint);
        var service = BuildService(workItems: items, seedLinks: links);
        var preview = await service.PreviewAsync(WritePlan(source));
        var model = preview.ReviewModel.ShouldNotBeNull();
        model.ModelVersion.ShouldBe(1);
        model.Digest.ShouldBe(new PlanDocumentParser().Parse(source).Digest);
        var operation = model.Operations.Single();
        operation.Target.WorkItemId.ShouldBeNull();
        operation.Target.StagedIdentity.ShouldBe(seed.StagedIdentity.Value.ToString());
        operation.Target.Seed!.DisplayAlias.ShouldBe(seed.Id);
        operation.Preconditions.Single().Value.ShouldBe(fingerprint);
        var effects = Effects(model);
        effects.Length.ShouldBe(3);
        effects[0].GetRawText().ShouldBe(LegacySeedEffect);
        effects[1].GetRawText().ShouldBe(LinkEffect(relation, 17, false));
        effects[2].GetRawText().ShouldBe(LinkEffect(reverse, 18, false));

        var repeated = await service.PreviewAsync(WritePlan(source));
        ChangeProposalReviewModelJson.Serialize(repeated.ReviewModel!)
            .ShouldBe(ChangeProposalReviewModelJson.Serialize(model));
        await links.AddLinkAsync(new SeedLink(seed.Id, 21, SeedLinkTypes.Related, DateTimeOffset.UnixEpoch));
        var refreshed = await service.PreviewAsync(WritePlan(source));
        refreshed.Digest.ShouldBe(preview.Digest);
        Effects(refreshed.ReviewModel!).Length.ShouldBe(4);
        (await SeedFingerprintCalculator.ComputeAsync(seed, await links.GetLinksForItemAsync(seed.Id),
            _stagedRegistry, _publishIdMap)).ShouldNotBe(fingerprint);
    }

    [Theory]
    [InlineData("absent", true)]
    [InlineData("earlier", false)]
    [InlineData("later", true)]
    public async Task SeedReview_DeferralUsesOnlyEarlierPeerPublications_AndOrdersEffects(string peerPosition, bool deferred)
    {
        var seed = BuildSeed(-42, StagedIdentity.New(), "Source", "Task");
        var peer = BuildSeed(-50, StagedIdentity.New(), "Peer", "Task");
        _workItems.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed, peer });
        SeedLink[] links =
        [
            new(seed.Id, 7, "related", DateTimeOffset.UnixEpoch),
            new(seed.Id, peer.Id, "related", DateTimeOffset.UnixEpoch),
            new(seed.Id, peer.Id, "blocks", DateTimeOffset.UnixEpoch),
            new(seed.Id, peer.Id, "blocks", DateTimeOffset.UnixEpoch.AddDays(1)),
            new(peer.Id, seed.Id, "blocked-by", DateTimeOffset.UnixEpoch),
        ];
        _seedLinks.GetLinksForItemAsync(seed.Id, Arg.Any<CancellationToken>()).Returns(links);
        _seedLinks.GetLinksForItemAsync(peer.Id, Arg.Any<CancellationToken>()).Returns(links.Where(l => l.TargetId == peer.Id || l.SourceId == peer.Id).ToArray());
        (await _workItems.GetSeedsAsync()).ShouldContain(seed);
        (await _seedLinks.GetLinksForItemAsync(seed.Id)).Count.ShouldBe(5);
        using var source = JsonDocument.Parse(PublishSeedPlan(seed.StagedIdentity!.Value, "fingerprint"));
        using var peerSource = JsonDocument.Parse(PublishSeedPlan(peer.StagedIdentity!.Value, "peer-fingerprint").Replace("\"S\"", "\"P\""));
        var operation = source.RootElement.GetProperty("operations")[0].GetRawText();
        var peerOperation = peerSource.RootElement.GetProperty("operations")[0].GetRawText();
        var operations = peerPosition switch
        {
            "earlier" => $"{peerOperation},{operation}",
            "later" => $"{operation},{peerOperation}",
            _ => operation,
        };
        var plan = $$"""{"version":1,"workspace":{"organization":"acme","project":"cache"},"operations":[{{operations}}]}""";
        var model = (await BuildService().PreviewAsync(WritePlan(plan))).ReviewModel.ShouldNotBeNull();
        var ordinal = peerPosition == "earlier" ? 1 : 0;
        var effects = Effects(model, ordinal);
        effects.Select(c => c.GetRawText()).ShouldBe(new[]
        {
            LegacySeedEffect, LinkEffect("blocks", peer.Id, deferred),
            LinkEffect("related", peer.Id, deferred), LinkEffect("related", 7, false),
        });
        model.ModelVersion.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SeedReview_NoDerivedLinks_PreservesLegacyBytes(bool hierarchyOnly)
    {
        var seed = BuildSeed(-42, StagedIdentity.New(), "Unlinked", "Task");
        _workItems.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new[] { seed });
        _seedLinks.GetLinksForItemAsync(seed.Id, Arg.Any<CancellationToken>()).Returns(hierarchyOnly
            ? new[] { new SeedLink(seed.Id, 1, SeedLinkTypes.ParentChild, DateTimeOffset.UnixEpoch) } : []);
        var source = PublishSeedPlan(seed.StagedIdentity!.Value, "fp");
        var model = (await BuildService().PreviewAsync(WritePlan(source))).ReviewModel.ShouldNotBeNull();
        using var json = JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
        json.RootElement.GetProperty("operations")[0].GetProperty("consequences").GetRawText()
            .ShouldBe($"[{LegacySeedEffect}]");
        model.ModelVersion.ShouldBe(1);
    }

    [Fact]
    public async Task SeedReview_StandaloneAddAndRemoveLinks_PreserveLegacyBytes()
    {
        var source = """
            {"version":1,"workspace":{"organization":"acme","project":"cache"},"operations":[
            {"id":"add","kind":"add-link","workItemId":1,"expectedRevision":1,"relation":"related","otherId":2},
            {"id":"remove","kind":"remove-link","workItemId":1,"expectedRevision":1,"relation":"related","otherId":3}]}
            """;
        var model = (await BuildService().PreviewAsync(WritePlan(source))).ReviewModel.ShouldNotBeNull();
        Effects(model, 0).Single().GetRawText().ShouldBe(LinkEffect("related", 2, null));
        Effects(model, 1).Single().GetRawText().ShouldBe(LinkEffect("related", 3, null).Replace("link-add", "link-remove"));
        ChangeProposalReviewModelJson.Serialize(model).ShouldNotContain("deferred");
    }

    private static JsonElement[] Effects(ChangeProposalReviewModel model, int ordinal = 0)
    {
        using var json = JsonDocument.Parse(ChangeProposalReviewModelJson.Serialize(model));
        return json.RootElement.GetProperty("operations")[ordinal].GetProperty("consequences")
            .EnumerateArray().Select(c => c.Clone()).ToArray();
    }

    private static string LinkEffect(string relation, int otherId, bool? deferred)
    {
        var suffix = deferred is null ? "" : ",\"deferred\":" + (deferred.Value ? "true" : "false");
        return $$"""{"kind":"link-add","field":null,"to":null,"fieldLabel":null,"fieldType":null,"before":null,"textChange":null,"relation":"{{relation}}","otherId":{{otherId}}{{suffix}}}""";
    }
}
