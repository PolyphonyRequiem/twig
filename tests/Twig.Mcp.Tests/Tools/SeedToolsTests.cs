using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="SeedTools"/> (twig_seed_new, twig_seed_view, twig_seed_publish).
/// </summary>
public sealed class SeedToolsTests : CreationToolsTestBase
{
    private SeedTools CreateSeedSut()
    {
        return new SeedTools(BuildResolver(DefaultConfig), new SeedFactory(new SeedIdCounter()));
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_new — happy path with parentId
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedNew_WithParent_CreatesLocalSeed()
    {
        var parent = new WorkItemBuilder(100, "Parent Epic").AsEpic().Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var config = BuildProcessConfigWithChildren(WorkItemType.Epic, WorkItemType.Issue);
        _processConfigProvider.GetConfiguration().Returns(config);

        var sut = CreateSeedSut();
        var result = await sut.SeedNew("My Seed", parentId: 100);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("id").GetInt32().ShouldBeLessThan(0);
        data.GetProperty("title").GetString().ShouldBe("My Seed");
        data.GetProperty("type").GetString().ShouldBe("Issue");
        data.GetProperty("isSeed").GetBoolean().ShouldBeTrue();
        data.GetProperty("parentId").GetInt32().ShouldBe(100);

        await _workItemRepo.Received(1).SaveAsync(Arg.Is<WorkItem>(w => w.IsSeed && w.Title == "My Seed"), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_new — happy path without parentId, with explicit type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedNew_WithoutParent_RequiresType()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedNew("My Seed", type: "Task");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("id").GetInt32().ShouldBeLessThan(0);
        data.GetProperty("title").GetString().ShouldBe("My Seed");
        data.GetProperty("type").GetString().ShouldBe("Task");
        data.GetProperty("isSeed").GetBoolean().ShouldBeTrue();
        data.GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_new — error: no title
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedNew_EmptyTitle_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedNew("", type: "Task");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Title is required");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_new — error: no type or parent
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedNew_NoTypeOrParent_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedNew("My Seed");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("parentId or type");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_new — error: invalid type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedNew_InvalidType_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedNew("My Seed", type: "");

        result.IsError.ShouldBe(true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_new — parent not found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedNew_ParentNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Not found"));

        var sut = CreateSeedSut();
        var result = await sut.SeedNew("My Seed", parentId: 999);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("999");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_new — description is converted to HTML
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedNew_WithDescription_SetsHtmlField()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedNew("My Seed", type: "Task", description: "**bold text**");

        result.IsError.ShouldBeNull();

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w =>
                w.Fields.ContainsKey("System.Description") &&
                w.Fields["System.Description"]!.Contains("<strong>")),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_new — with type override on parented seed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedNew_ParentedWithTypeOverride_CreatesCorrectType()
    {
        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var config = BuildProcessConfigWithChildren(WorkItemType.Issue, WorkItemType.Task, WorkItemType.Bug);
        _processConfigProvider.GetConfiguration().Returns(config);

        var sut = CreateSeedSut();
        var result = await sut.SeedNew("My Bug Seed", type: "Bug", parentId: 100);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("type").GetString().ShouldBe("Bug");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_new — disallowed child type returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedNew_DisallowedChildType_ReturnsError()
    {
        var parent = new WorkItemBuilder(100, "Parent Epic").AsEpic().Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var config = BuildProcessConfigWithChildren(WorkItemType.Epic, WorkItemType.Issue);
        _processConfigProvider.GetConfiguration().Returns(config);

        var sut = CreateSeedSut();
        var result = await sut.SeedNew("My Bug Seed", type: "Bug", parentId: 100);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Allowed child types");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_new — initializes seed counter from DB
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedNew_InitializesSeedCounter_FromMinSeedId()
    {
        _workItemRepo.GetMinSeedIdAsync(Arg.Any<CancellationToken>()).Returns(-5);

        var sut = CreateSeedSut();
        var result = await sut.SeedNew("Seed After Init", type: "Task");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        // ID should be below -5 since counter was initialized at -5
        data.GetProperty("id").GetInt32().ShouldBeLessThan(-5);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_view — no seeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedView_NoSeeds_ReturnsEmptyList()
    {
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem>());

        var sut = CreateSeedSut();
        var result = await sut.SeedView();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("seedCount").GetInt32().ShouldBe(0);
        data.GetProperty("groups").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_view — with seeds grouped by parent
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedView_WithSeeds_GroupsByParent()
    {
        var seeds = new List<WorkItem>
        {
            new WorkItemBuilder(-1, "Seed A").AsTask().AsSeed().WithParent(100).Build(),
            new WorkItemBuilder(-2, "Seed B").AsTask().AsSeed().WithParent(100).Build(),
            new WorkItemBuilder(-3, "Seed C").AsIssue().AsSeed().WithParent(200).Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        var sut = CreateSeedSut();
        var result = await sut.SeedView();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("seedCount").GetInt32().ShouldBe(3);

        var groups = data.GetProperty("groups");
        groups.GetArrayLength().ShouldBe(2);

        // First group (parentId 100) should have 2 seeds
        var group100 = groups[0];
        group100.GetProperty("parentId").GetInt32().ShouldBe(100);
        group100.GetProperty("seeds").GetArrayLength().ShouldBe(2);

        // Second group (parentId 200) should have 1 seed
        var group200 = groups[1];
        group200.GetProperty("parentId").GetInt32().ShouldBe(200);
        group200.GetProperty("seeds").GetArrayLength().ShouldBe(1);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_view — orphan seeds (no parent)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedView_OrphanSeeds_GroupedUnderNullParent()
    {
        var seeds = new List<WorkItem>
        {
            new WorkItemBuilder(-1, "Orphan Seed").AsTask().AsSeed().Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        var sut = CreateSeedSut();
        var result = await sut.SeedView();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("seedCount").GetInt32().ShouldBe(1);

        var groups = data.GetProperty("groups");
        groups.GetArrayLength().ShouldBe(1);
        groups[0].GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — error: no id or all
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_NoIdOrAll_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedPublish();

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("id or set all=true");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — error: both id and all
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_BothIdAndAll_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedPublish(id: -1, all: true);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("not both");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — error: positive id
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_PositiveId_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedPublish(id: 42);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("negative integer");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — single: seed not found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_SeedNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var sut = CreateSeedSut();
        var result = await sut.SeedPublish(id: -1);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("-1");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — single: happy path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_Single_PublishesAndReturnsRemappedId()
    {
        var seed = new WorkItemBuilder(-1, "My Seed").AsTask().AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        var createdItem = new WorkItemBuilder(500, "My Seed").AsTask().WithParent(100).Build();
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(createdItem);

        var mockTx = Substitute.For<ITransaction>();
        _unitOfWork.BeginAsync(Arg.Any<CancellationToken>()).Returns(mockTx);

        var sut = CreateSeedSut();
        var result = await sut.SeedPublish(id: -1);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("oldId").GetInt32().ShouldBe(-1);
        data.GetProperty("newId").GetInt32().ShouldBe(500);
        data.GetProperty("title").GetString().ShouldBe("My Seed");
        data.GetProperty("status").GetString().ShouldBe("created");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — single: unpublished parent guard
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_UnpublishedParent_ReturnsError()
    {
        var seed = new WorkItemBuilder(-1, "Child Seed").AsTask().AsSeed().WithParent(-2).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedPublish(id: -1);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("must be published first");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — single: updates active context after publish
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_ActiveSeed_UpdatesContextToNewId()
    {
        var seed = new WorkItemBuilder(-1, "Active Seed").AsTask().AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(-1);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        var createdItem = new WorkItemBuilder(500, "Active Seed").AsTask().WithParent(100).Build();
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>()).Returns(500);
        _adoService.FetchAsync(500, Arg.Any<CancellationToken>()).Returns(createdItem);

        var mockTx = Substitute.For<ITransaction>();
        _unitOfWork.BeginAsync(Arg.Any<CancellationToken>()).Returns(mockTx);

        var sut = CreateSeedSut();
        var result = await sut.SeedPublish(id: -1);

        result.IsError.ShouldBeNull();
        await _contextStore.Received(1).SetActiveWorkItemIdAsync(500, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — single: dry run
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_DryRun_ReturnsWithoutAdoCalls()
    {
        var seed = new WorkItemBuilder(-1, "Dry Run Seed").AsTask().AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        var sut = CreateSeedSut();
        var result = await sut.SeedPublish(id: -1, dryRun: true);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("status").GetString().ShouldBe("dry_run");

        await _adoService.DidNotReceive().CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — batch: no seeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_BatchNoSeeds_ReturnsEmpty()
    {
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem>());

        var sut = CreateSeedSut();
        var result = await sut.SeedPublish(all: true);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("publishedCount").GetInt32().ShouldBe(0);
        data.GetProperty("results").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — single: ADO failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_AdoFailure_ReturnsAdoError()
    {
        var seed = new WorkItemBuilder(-1, "Fail Seed").AsTask().AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);
        _adoService.CreateAsync(Arg.Any<CreateWorkItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network timeout"));

        var sut = CreateSeedSut();
        var result = await sut.SeedPublish(id: -1);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Publish failed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_publish — single: validation failure
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedPublish_ValidationFails_ReturnsValidationError()
    {
        // Create a seed missing required fields
        var seed = new WorkItemBuilder(-1, "").AsTask().AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new SeedPublishRules { RequiredFields = ["System.Title"], RequireParent = false });

        var sut = CreateSeedSut();
        var result = await sut.SeedPublish(id: -1);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("validation");
    }
}
