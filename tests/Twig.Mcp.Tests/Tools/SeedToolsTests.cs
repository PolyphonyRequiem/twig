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
    //  twig_seed_validate — single seed passes
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedValidate_SingleSeed_Passes()
    {
        var seed = new WorkItemBuilder(-1, "Valid Seed").AsTask().AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        var sut = CreateSeedSut();
        var result = await sut.SeedValidate(id: -1);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("seedId").GetInt32().ShouldBe(-1);
        data.GetProperty("title").GetString().ShouldBe("Valid Seed");
        data.GetProperty("passed").GetBoolean().ShouldBeTrue();
        data.GetProperty("failures").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_validate — single seed fails validation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedValidate_SingleSeed_FailsValidation()
    {
        var seed = new WorkItemBuilder(-1, "").AsTask().AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new SeedPublishRules { RequiredFields = ["System.Title"], RequireParent = false });

        var sut = CreateSeedSut();
        var result = await sut.SeedValidate(id: -1);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("passed").GetBoolean().ShouldBeFalse();
        data.GetProperty("failures").GetArrayLength().ShouldBeGreaterThan(0);
        data.GetProperty("failures")[0].GetProperty("rule").GetString().ShouldBe("System.Title");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_validate — all seeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedValidate_AllSeeds_ReturnsBatchResults()
    {
        var seeds = new List<WorkItem>
        {
            new WorkItemBuilder(-1, "Good Seed").AsTask().AsSeed().Build(),
            new WorkItemBuilder(-2, "").AsTask().AsSeed().Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new SeedPublishRules { RequiredFields = ["System.Title"], RequireParent = false });

        var sut = CreateSeedSut();
        var result = await sut.SeedValidate(all: true);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("totalCount").GetInt32().ShouldBe(2);
        data.GetProperty("passedCount").GetInt32().ShouldBe(1);
        data.GetProperty("failedCount").GetInt32().ShouldBe(1);
        data.GetProperty("results").GetArrayLength().ShouldBe(2);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_validate — no id or all
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedValidate_NoIdOrAll_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedValidate();

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Specify a seed id or set all=true");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_validate — both id and all
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedValidate_BothIdAndAll_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedValidate(id: -1, all: true);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Specify either id or all=true");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_validate — positive id
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedValidate_PositiveId_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedValidate(id: 5);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("negative integer");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_validate — seed not found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedValidate_SeedNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(-99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        var sut = CreateSeedSut();
        var result = await sut.SeedValidate(id: -99);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_validate — non-seed item returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedValidate_NonSeedItem_ReturnsError()
    {
        var item = new WorkItemBuilder(-1, "Not A Seed").AsTask().Build(); // IsSeed = false
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(item);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(SeedPublishRules.Default);

        var sut = CreateSeedSut();
        var result = await sut.SeedValidate(id: -1);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_validate — require parent rule
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedValidate_RequireParent_FailsForOrphanSeed()
    {
        var seed = new WorkItemBuilder(-1, "Orphan Seed").AsTask().AsSeed().Build(); // no parent
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _seedPublishRulesProvider.GetRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new SeedPublishRules { RequiredFields = ["System.Title"], RequireParent = true });

        var sut = CreateSeedSut();
        var result = await sut.SeedValidate(id: -1);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("passed").GetBoolean().ShouldBeFalse();
        var failures = data.GetProperty("failures");
        failures.GetArrayLength().ShouldBeGreaterThan(0);
        // Should have a RequireParent failure
        var hasParentFailure = false;
        for (var i = 0; i < failures.GetArrayLength(); i++)
        {
            if (failures[i].GetProperty("rule").GetString() == "RequireParent")
            {
                hasParentFailure = true;
                break;
            }
        }
        hasParentFailure.ShouldBeTrue();
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_discard — happy path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedDiscard_ValidSeed_DiscardsAndReturnsConfirmation()
    {
        var seed = new WorkItemBuilder(-1, "To Discard").AsTask().AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem> { seed });

        var sut = CreateSeedSut();
        var result = await sut.SeedDiscard(id: -1);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("discardedId").GetInt32().ShouldBe(-1);
        data.GetProperty("discardedTitle").GetString().ShouldBe("To Discard");
        data.GetProperty("totalDiscarded").GetInt32().ShouldBe(1);
        data.GetProperty("descendantsDiscarded").GetInt32().ShouldBe(0);

        await _workItemRepo.Received(1).DeleteByIdAsync(-1, Arg.Any<CancellationToken>());
        await _seedLinkRepo.Received(1).DeleteLinksForItemAsync(-1, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_discard — cascade delete with children
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedDiscard_WithChildren_CascadeDeletes()
    {
        var parent = new WorkItemBuilder(-1, "Parent Seed").AsIssue().AsSeed().Build();
        var child = new WorkItemBuilder(-2, "Child Seed").AsTask().AsSeed().WithParent(-1).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem> { parent, child });

        var sut = CreateSeedSut();
        var result = await sut.SeedDiscard(id: -1);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("totalDiscarded").GetInt32().ShouldBe(2);
        data.GetProperty("descendantsDiscarded").GetInt32().ShouldBe(1);
        data.GetProperty("allDiscardedIds").GetArrayLength().ShouldBe(2);

        await _workItemRepo.Received(1).DeleteByIdAsync(-1, Arg.Any<CancellationToken>());
        await _workItemRepo.Received(1).DeleteByIdAsync(-2, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_discard — positive id
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedDiscard_PositiveId_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedDiscard(id: 5);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("negative integer");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_discard — seed not found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedDiscard_SeedNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(-99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var sut = CreateSeedSut();
        var result = await sut.SeedDiscard(id: -99);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_discard — clears active context when discarding active seed
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedDiscard_ActiveSeed_ClearsContext()
    {
        var seed = new WorkItemBuilder(-1, "Active Seed").AsTask().AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem> { seed });
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(-1);

        var sut = CreateSeedSut();
        await sut.SeedDiscard(id: -1);

        await _contextStore.Received(1).ClearActiveWorkItemIdAsync(Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_chain — happy path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedChain_ValidTitles_CreatesChain()
    {
        var parent = new WorkItemBuilder(100, "Parent Epic").AsEpic().Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var config = BuildProcessConfigWithChildren(WorkItemType.Epic, WorkItemType.Issue);
        _processConfigProvider.GetConfiguration().Returns(config);

        var sut = CreateSeedSut();
        var result = await sut.SeedChain(parentId: 100, titles: ["First", "Second", "Third"]);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("createdCount").GetInt32().ShouldBe(3);
        data.GetProperty("parentId").GetInt32().ShouldBe(100);
        data.GetProperty("seeds").GetArrayLength().ShouldBe(3);

        var seeds = data.GetProperty("seeds");
        seeds[0].GetProperty("title").GetString().ShouldBe("First");
        seeds[1].GetProperty("title").GetString().ShouldBe("Second");
        seeds[2].GetProperty("title").GetString().ShouldBe("Third");

        // Each seed should have a unique negative ID
        var ids = new HashSet<int>();
        for (var i = 0; i < 3; i++)
        {
            var id = seeds[i].GetProperty("id").GetInt32();
            id.ShouldBeLessThan(0);
            ids.Add(id).ShouldBeTrue();
        }

        // All should be inferred as Issue (child of Epic)
        for (var i = 0; i < 3; i++)
            seeds[i].GetProperty("type").GetString().ShouldBe("Issue");

        await _workItemRepo.Received(3).SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_chain — with explicit type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedChain_WithExplicitType_UsesType()
    {
        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var config = BuildProcessConfigWithChildren(WorkItemType.Issue, WorkItemType.Task, WorkItemType.Bug);
        _processConfigProvider.GetConfiguration().Returns(config);

        var sut = CreateSeedSut();
        var result = await sut.SeedChain(parentId: 100, titles: ["Task A", "Task B"], type: "Bug");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("createdCount").GetInt32().ShouldBe(2);
        var seeds = data.GetProperty("seeds");
        seeds[0].GetProperty("type").GetString().ShouldBe("Bug");
        seeds[1].GetProperty("type").GetString().ShouldBe("Bug");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_chain — with assignee
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedChain_WithAssignee_SetsAssigneeOnAllSeeds()
    {
        var parent = new WorkItemBuilder(100, "Parent Epic").AsEpic().Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var config = BuildProcessConfigWithChildren(WorkItemType.Epic, WorkItemType.Issue);
        _processConfigProvider.GetConfiguration().Returns(config);

        var sut = CreateSeedSut();
        var result = await sut.SeedChain(parentId: 100, titles: ["S1", "S2"], assignedTo: "Dan Green");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        var seeds = data.GetProperty("seeds");
        seeds[0].GetProperty("assignedTo").GetString().ShouldBe("Dan Green");
        seeds[1].GetProperty("assignedTo").GetString().ShouldBe("Dan Green");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_chain — empty titles
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedChain_EmptyTitles_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedChain(parentId: 100, titles: []);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("At least one title");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_chain — blank title in array
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedChain_BlankTitleInArray_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedChain(parentId: 100, titles: ["Good", "", "Also Good"]);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("non-empty");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_chain — parent not found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedChain_ParentNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Not found"));

        var sut = CreateSeedSut();
        var result = await sut.SeedChain(parentId: 999, titles: ["Seed"]);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_chain — disallowed type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedChain_DisallowedType_ReturnsError()
    {
        var parent = new WorkItemBuilder(100, "Parent Epic").AsEpic().Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var config = BuildProcessConfigWithChildren(WorkItemType.Epic, WorkItemType.Issue);
        _processConfigProvider.GetConfiguration().Returns(config);

        var sut = CreateSeedSut();
        var result = await sut.SeedChain(parentId: 100, titles: ["Seed"], type: "Task");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("not an allowed child");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_chain — invalid type string
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedChain_InvalidType_ReturnsError()
    {
        var parent = new WorkItemBuilder(100, "Parent Epic").AsEpic().Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        // Only allow Issue children — "NotAType123" won't match
        var config = BuildProcessConfigWithChildren(WorkItemType.Epic, WorkItemType.Issue);
        _processConfigProvider.GetConfiguration().Returns(config);

        var sut = CreateSeedSut();
        var result = await sut.SeedChain(parentId: 100, titles: ["Seed"], type: "NotAType123");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("not an allowed child");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_reconcile — nothing to do
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedReconcile_NothingToDo_ReturnsCleanResult()
    {
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new List<SeedLink>());
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>()).Returns(new List<(int, int)>());
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem>());

        var sut = CreateSeedSut();
        var result = await sut.SeedReconcile();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("linksRepaired").GetInt32().ShouldBe(0);
        data.GetProperty("linksRemoved").GetInt32().ShouldBe(0);
        data.GetProperty("parentIdsFixed").GetInt32().ShouldBe(0);
        data.GetProperty("nothingToDo").GetBoolean().ShouldBeTrue();
        data.GetProperty("warnings").GetArrayLength().ShouldBe(0);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_reconcile — repairs stale link via publish map
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedReconcile_StaleLink_RemapsViaPublishMap()
    {
        var staleLink = new SeedLink(-1, -2, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new List<SeedLink> { staleLink });
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-1, 500), (-2, 600) });

        // Both old IDs are gone (published)
        _workItemRepo.ExistsByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem>());

        var sut = CreateSeedSut();
        var result = await sut.SeedReconcile();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("linksRepaired").GetInt32().ShouldBeGreaterThan(0);
        data.GetProperty("nothingToDo").GetBoolean().ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_reconcile — removes orphaned link
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedReconcile_OrphanedLink_RemovesIt()
    {
        var orphanLink = new SeedLink(-1, -2, SeedLinkTypes.Related, DateTimeOffset.UtcNow);
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new List<SeedLink> { orphanLink });
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>()).Returns(new List<(int, int)>());

        // Neither seed exists and no publish mapping
        _workItemRepo.ExistsByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.ExistsByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(false);
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem>());

        var sut = CreateSeedSut();
        var result = await sut.SeedReconcile();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("linksRemoved").GetInt32().ShouldBe(1);
        data.GetProperty("nothingToDo").GetBoolean().ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_reconcile — fixes stale parent ID
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedReconcile_StaleParentId_FixesIt()
    {
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new List<SeedLink>());
        _publishIdMapRepo.GetAllMappingsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<(int, int)> { (-10, 1000) });

        // Seed with stale parent reference
        var seed = new WorkItemBuilder(-5, "Child Seed").AsTask().AsSeed().WithParent(-10).Build();
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem> { seed });
        _workItemRepo.ExistsByIdAsync(-10, Arg.Any<CancellationToken>()).Returns(false);

        var sut = CreateSeedSut();
        var result = await sut.SeedReconcile();

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("parentIdsFixed").GetInt32().ShouldBe(1);
        data.GetProperty("nothingToDo").GetBoolean().ShouldBeFalse();
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — happy path: update title
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_UpdateTitle_SavesChangedSeed()
    {
        var seed = new WorkItemBuilder(-1, "Old Title").AsTask().AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1, title: "New Title");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("id").GetInt32().ShouldBe(-1);
        data.GetProperty("title").GetString().ShouldBe("New Title");
        data.GetProperty("changedCount").GetInt32().ShouldBe(1);
        data.GetProperty("changedFields").GetArrayLength().ShouldBe(1);

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.Title == "New Title" && w.IsSeed),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — update description (converts markdown to HTML)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_UpdateDescription_ConvertsMarkdownToHtml()
    {
        var seed = new WorkItemBuilder(-1, "My Seed").AsTask().AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1, description: "**bold text**");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("changedCount").GetInt32().ShouldBe(1);

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w =>
                w.Fields.ContainsKey("System.Description") &&
                w.Fields["System.Description"]!.Contains("<strong>")),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — update type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_UpdateType_ChangesWorkItemType()
    {
        var seed = new WorkItemBuilder(-1, "My Seed").AsTask().AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1, type: "Bug");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("type").GetString().ShouldBe("Bug");
        data.GetProperty("changedCount").GetInt32().ShouldBe(1);

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.Type == WorkItemType.Bug),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — update parent
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_UpdateParent_ChangesParentId()
    {
        var seed = new WorkItemBuilder(-1, "My Seed").AsTask().AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1, parentId: 200);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("parentId").GetInt32().ShouldBe(200);
        data.GetProperty("changedCount").GetInt32().ShouldBe(1);

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.ParentId == 200),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — clear parent with 0
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_ClearParent_SetsParentToNull()
    {
        var seed = new WorkItemBuilder(-1, "My Seed").AsTask().AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1, parentId: 0);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("parentId").ValueKind.ShouldBe(JsonValueKind.Null);
        data.GetProperty("changedCount").GetInt32().ShouldBe(1);

        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.ParentId == null),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — multiple fields at once
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_MultipleFields_ReportsAllChanges()
    {
        var seed = new WorkItemBuilder(-1, "Old Title").AsTask().AsSeed().WithParent(100).Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1, title: "New Title", description: "desc", type: "Bug", parentId: 200);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("changedCount").GetInt32().ShouldBe(4);
        data.GetProperty("changedFields").GetArrayLength().ShouldBe(4);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — no changes detected
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_SameTitle_ReportsNoChanges()
    {
        var seed = new WorkItemBuilder(-1, "Same Title").AsTask().AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1, title: "Same Title");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("changedCount").GetInt32().ShouldBe(0);

        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<WorkItem>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — error: positive id
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_PositiveId_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: 5, title: "New Title");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("negative integer");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — error: no fields provided
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_NoFields_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("At least one field");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — error: empty title
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_EmptyTitle_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1, title: "");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("empty or whitespace");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — error: seed not found
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_SeedNotFound_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(-99, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -99, title: "New Title");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — error: non-seed item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_NonSeedItem_ReturnsError()
    {
        var item = new WorkItemBuilder(-1, "Not A Seed").AsTask().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(item);

        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1, title: "New Title");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_edit — error: invalid type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedEdit_InvalidType_ReturnsError()
    {
        var seed = new WorkItemBuilder(-1, "My Seed").AsTask().AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedEdit(id: -1, type: "");

        result.IsError.ShouldBe(true);
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_link — happy path: related link
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedLink_RelatedLink_CreatesSuccessfully()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedLink(sourceId: -1, targetId: -2);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("sourceId").GetInt32().ShouldBe(-1);
        data.GetProperty("targetId").GetInt32().ShouldBe(-2);
        data.GetProperty("linkType").GetString().ShouldBe("related");
        data.GetProperty("created").GetBoolean().ShouldBeTrue();

        await _seedLinkRepo.Received(1).AddLinkAsync(
            Arg.Is<SeedLink>(l => l.SourceId == -1 && l.TargetId == -2 && l.LinkType == "related"),
            Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_link — explicit link type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedLink_ExplicitType_UsesSpecifiedType()
    {
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkItem>
        {
            new WorkItemBuilder(-1, "Source").AsTask().AsSeed().Build(),
            new WorkItemBuilder(-2, "Target").AsTask().AsSeed().Build(),
        });
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(new List<SeedLink>());

        var sut = CreateSeedSut();
        var result = await sut.SeedLink(sourceId: -1, targetId: -2, type: "blocks");

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("linkType").GetString().ShouldBe("blocks");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_link — error: both positive IDs
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedLink_BothPositiveIds_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedLink(sourceId: 1, targetId: 2);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("At least one ID must be a seed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_link — error: invalid link type
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedLink_InvalidLinkType_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedLink(sourceId: -1, targetId: -2, type: "invalid-type");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Invalid link type");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_link — error: self-referencing link
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedLink_SelfReference_ReturnsError()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedLink(sourceId: -1, targetId: -1, type: "blocks");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("Self-referencing");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_link — error: cycle detection
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedLink_WouldCreateCycle_ReturnsError()
    {
        var seeds = new List<WorkItem>
        {
            new WorkItemBuilder(-1, "A").AsTask().AsSeed().Build(),
            new WorkItemBuilder(-2, "B").AsTask().AsSeed().Build(),
        };
        _workItemRepo.GetSeedsAsync(Arg.Any<CancellationToken>()).Returns(seeds);

        // Existing link: -1 blocks -2
        var existingLinks = new List<SeedLink>
        {
            new SeedLink(-1, -2, SeedLinkTypes.Blocks, DateTimeOffset.UtcNow)
        };
        _seedLinkRepo.GetAllSeedLinksAsync(Arg.Any<CancellationToken>()).Returns(existingLinks);

        var sut = CreateSeedSut();
        // Try to add -2 blocks -1 → cycle
        var result = await sut.SeedLink(sourceId: -2, targetId: -1, type: "blocks");

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("cycle");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_link — duplicate link
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedLink_DuplicateLink_ReturnsError()
    {
        _seedLinkRepo.AddLinkAsync(Arg.Any<SeedLink>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Microsoft.Data.Sqlite.SqliteException("UNIQUE constraint failed", 19));

        var sut = CreateSeedSut();
        var result = await sut.SeedLink(sourceId: -1, targetId: -2);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("already exists");
    }

    // ═══════════════════════════════════════════════════════════════
    //  twig_seed_link — mixed seed and ADO item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task SeedLink_SeedAndAdoItem_CreatesSuccessfully()
    {
        var sut = CreateSeedSut();
        var result = await sut.SeedLink(sourceId: -1, targetId: 500);

        result.IsError.ShouldBeNull();
        var data = ParseResult(result);
        data.GetProperty("sourceId").GetInt32().ShouldBe(-1);
        data.GetProperty("targetId").GetInt32().ShouldBe(500);
        data.GetProperty("created").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task SeedLink_ParentChildFromSeed_SetsAuthoritativeParentId()
    {
        var seed = new WorkItemBuilder(-1, "Child").AsTask().AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedLink(
            sourceId: -1,
            targetId: 100,
            type: SeedLinkTypes.ParentChild);

        result.IsError.ShouldBeNull();
        await _workItemRepo.Received(1).SaveAsync(
            Arg.Is<WorkItem>(w => w.Id == -1 && w.ParentId == 100),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedLink_ParentChildFromSeed_WithDifferentParent_ReturnsError()
    {
        var seed = new WorkItemBuilder(-1, "Child")
            .AsTask()
            .AsSeed()
            .WithParent(50)
            .Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var sut = CreateSeedSut();
        var result = await sut.SeedLink(
            sourceId: -1,
            targetId: 100,
            type: SeedLinkTypes.ParentChild);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("already has parent #50");
        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SeedLink_ParentChildFromSeed_WithDifferentExistingLink_ReturnsError()
    {
        var seed = new WorkItemBuilder(-1, "Child").AsTask().AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);
        _seedLinkRepo.GetLinksForItemAsync(-1, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new SeedLink(-1, 50, SeedLinkTypes.ParentChild, DateTimeOffset.UtcNow),
            });

        var sut = CreateSeedSut();
        var result = await sut.SeedLink(
            sourceId: -1,
            targetId: 100,
            type: SeedLinkTypes.ParentChild);

        result.IsError.ShouldBe(true);
        GetErrorText(result).ShouldContain("already has parent #50");
        await _seedLinkRepo.DidNotReceive().AddLinkAsync(
            Arg.Any<SeedLink>(),
            Arg.Any<CancellationToken>());
    }
}
