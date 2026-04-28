using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services;

public sealed class SprintIterationResolverTests
{
    private readonly IIterationService _iterationService = Substitute.For<IIterationService>();
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();

    private static readonly IterationPath Sprint1 = IterationPath.Parse(@"Project\Sprint 1").Value;
    private static readonly IterationPath Sprint2 = IterationPath.Parse(@"Project\Sprint 2").Value;
    private static readonly IterationPath Sprint3 = IterationPath.Parse(@"Project\Sprint 3").Value;
    private static readonly IterationPath Sprint4 = IterationPath.Parse(@"Project\Sprint 4").Value;
    private static readonly IterationPath Sprint5 = IterationPath.Parse(@"Project\Sprint 5").Value;

    private static readonly IReadOnlyList<TeamIteration> StandardIterations =
    [
        new TeamIteration(@"Project\Sprint 1", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2025, 1, 14, 0, 0, 0, TimeSpan.Zero)),
        new TeamIteration(@"Project\Sprint 2", new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2025, 1, 28, 0, 0, 0, TimeSpan.Zero)),
        new TeamIteration(@"Project\Sprint 3", new DateTimeOffset(2025, 1, 29, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2025, 2, 11, 0, 0, 0, TimeSpan.Zero)),
        new TeamIteration(@"Project\Sprint 4", new DateTimeOffset(2025, 2, 12, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2025, 2, 25, 0, 0, 0, TimeSpan.Zero)),
        new TeamIteration(@"Project\Sprint 5", new DateTimeOffset(2025, 2, 26, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2025, 3, 11, 0, 0, 0, TimeSpan.Zero)),
    ];

    private SprintIterationResolver CreateSut() => new(_iterationService, _workItemRepo);

    private void SetupStandardIterations(IterationPath currentIteration)
    {
        _iterationService.GetTeamIterationsAsync(Arg.Any<CancellationToken>()).Returns(StandardIterations);
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>()).Returns(currentIteration);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveExpressionAsync — Absolute expressions
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveExpressionAsync_AbsolutePath_ReturnsIterationPath()
    {
        var sut = CreateSut();
        var expr = IterationExpression.Parse(@"Project\Sprint 3").Value;

        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldNotBeNull();
        result.Value.Value.ShouldBe(@"Project\Sprint 3");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveExpressionAsync — Relative expressions
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveExpressionAsync_CurrentOffset0_ReturnsCurrentIteration()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();
        var expr = IterationExpression.Parse("@current").Value;

        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldNotBeNull();
        result.Value.ShouldBe(Sprint3);
    }

    [Fact]
    public async Task ResolveExpressionAsync_CurrentMinus1_ReturnsPreviousIteration()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();
        var expr = IterationExpression.Parse("@current-1").Value;

        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldNotBeNull();
        result.Value.ShouldBe(Sprint2);
    }

    [Fact]
    public async Task ResolveExpressionAsync_CurrentPlus1_ReturnsNextIteration()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();
        var expr = IterationExpression.Parse("@current+1").Value;

        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldNotBeNull();
        result.Value.ShouldBe(Sprint4);
    }

    [Fact]
    public async Task ResolveExpressionAsync_CurrentMinus2_ReturnsTwoBack()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();
        var expr = IterationExpression.Parse("@current-2").Value;

        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldNotBeNull();
        result.Value.ShouldBe(Sprint1);
    }

    [Fact]
    public async Task ResolveExpressionAsync_CurrentPlus2_ReturnsTwoForward()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();
        var expr = IterationExpression.Parse("@current+2").Value;

        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldNotBeNull();
        result.Value.ShouldBe(Sprint5);
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveExpressionAsync — Edge cases
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveExpressionAsync_OffsetBeyondEnd_ReturnsNull()
    {
        SetupStandardIterations(Sprint5);
        var sut = CreateSut();
        var expr = IterationExpression.Parse("@current+1").Value;

        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveExpressionAsync_OffsetBeyondStart_ReturnsNull()
    {
        SetupStandardIterations(Sprint1);
        var sut = CreateSut();
        var expr = IterationExpression.Parse("@current-1").Value;

        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveExpressionAsync_NoTeamIterations_ReturnsNull()
    {
        _iterationService.GetTeamIterationsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<TeamIteration>());
        var sut = CreateSut();
        var expr = IterationExpression.Parse("@current").Value;

        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveExpressionAsync_CurrentNotInList_ReturnsNull()
    {
        _iterationService.GetTeamIterationsAsync(Arg.Any<CancellationToken>()).Returns(StandardIterations);
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse(@"Project\Sprint 99").Value);
        var sut = CreateSut();
        var expr = IterationExpression.Parse("@current").Value;

        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveExpressionAsync_IterationsWithNullDates_SortedToEnd()
    {
        var iterations = new List<TeamIteration>
        {
            new(@"Project\NoDate", null, null),
            new(@"Project\Sprint 1", new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), null),
            new(@"Project\Sprint 2", new DateTimeOffset(2025, 1, 15, 0, 0, 0, TimeSpan.Zero), null),
        };
        _iterationService.GetTeamIterationsAsync(Arg.Any<CancellationToken>()).Returns(iterations);
        _iterationService.GetCurrentIterationAsync(Arg.Any<CancellationToken>())
            .Returns(IterationPath.Parse(@"Project\Sprint 2").Value);
        var sut = CreateSut();

        // @current+1 should resolve to the NoDate iteration (sorted to end)
        var expr = IterationExpression.Parse("@current+1").Value;
        var result = await sut.ResolveExpressionAsync(expr);

        result.ShouldNotBeNull();
        result.Value.Value.ShouldBe(@"Project\NoDate");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveAllAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveAllAsync_EmptyList_ReturnsEmptyList()
    {
        var sut = CreateSut();

        var result = await sut.ResolveAllAsync([]);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveAllAsync_MixedExpressions_ResolvesAll()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();
        var expressions = new[]
        {
            IterationExpression.Parse("@current").Value,
            IterationExpression.Parse("@current-1").Value,
            IterationExpression.Parse(@"Project\Sprint 5").Value,
        };

        var result = await sut.ResolveAllAsync(expressions);

        result.Count.ShouldBe(3);
        result.ShouldContain(p => p.Value == @"Project\Sprint 3");
        result.ShouldContain(p => p.Value == @"Project\Sprint 2");
        result.ShouldContain(p => p.Value == @"Project\Sprint 5");
    }

    [Fact]
    public async Task ResolveAllAsync_SkipsUnresolvableExpressions()
    {
        SetupStandardIterations(Sprint1);
        var sut = CreateSut();
        var expressions = new[]
        {
            IterationExpression.Parse("@current").Value,
            IterationExpression.Parse("@current-1").Value, // out of bounds — should be skipped
        };

        var result = await sut.ResolveAllAsync(expressions);

        result.Count.ShouldBe(1);
        result[0].ShouldBe(Sprint1);
    }

    [Fact]
    public async Task ResolveAllAsync_DeduplicatesSamePath()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();
        var expressions = new[]
        {
            IterationExpression.Parse("@current").Value,
            IterationExpression.Parse(@"Project\Sprint 3").Value, // same as @current
        };

        var result = await sut.ResolveAllAsync(expressions);

        result.Count.ShouldBe(1);
        result[0].ShouldBe(Sprint3);
    }

    // ═══════════════════════════════════════════════════════════════
    //  GetSprintItemsAsync — Multi-iteration aggregation
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSprintItemsAsync_EmptyExpressions_ReturnsEmpty()
    {
        var sut = CreateSut();

        var result = await sut.GetSprintItemsAsync([], "Dan", allUsers: false);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetSprintItemsAsync_AllUsers_QueriesWithoutAssignee()
    {
        SetupStandardIterations(Sprint3);
        var item1 = new WorkItemBuilder(1, "Task 1").WithIterationPath(@"Project\Sprint 3").Build();
        _workItemRepo.GetByIterationAsync(Sprint3, Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });
        var sut = CreateSut();
        var expressions = new[] { IterationExpression.Parse("@current").Value };

        var result = await sut.GetSprintItemsAsync(expressions, "Dan", allUsers: true);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(1);
        await _workItemRepo.Received(1).GetByIterationAsync(Sprint3, Arg.Any<CancellationToken>());
        await _workItemRepo.DidNotReceive().GetByIterationAndAssigneeAsync(Arg.Any<IterationPath>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSprintItemsAsync_SingleUser_QueriesWithAssignee()
    {
        SetupStandardIterations(Sprint3);
        var item1 = new WorkItemBuilder(1, "Task 1").WithIterationPath(@"Project\Sprint 3").AssignedTo("Dan").Build();
        _workItemRepo.GetByIterationAndAssigneeAsync(Sprint3, "Dan", Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });
        var sut = CreateSut();
        var expressions = new[] { IterationExpression.Parse("@current").Value };

        var result = await sut.GetSprintItemsAsync(expressions, "Dan", allUsers: false);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(1);
        await _workItemRepo.Received(1).GetByIterationAndAssigneeAsync(Sprint3, "Dan", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSprintItemsAsync_MultipleIterations_UnionResults()
    {
        SetupStandardIterations(Sprint3);
        var item1 = new WorkItemBuilder(1, "Task 1").WithIterationPath(@"Project\Sprint 2").Build();
        var item2 = new WorkItemBuilder(2, "Task 2").WithIterationPath(@"Project\Sprint 3").Build();
        _workItemRepo.GetByIterationAsync(Sprint2, Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });
        _workItemRepo.GetByIterationAsync(Sprint3, Arg.Any<CancellationToken>())
            .Returns(new[] { item2 });
        var sut = CreateSut();
        var expressions = new[]
        {
            IterationExpression.Parse("@current-1").Value,
            IterationExpression.Parse("@current").Value,
        };

        var result = await sut.GetSprintItemsAsync(expressions, null, allUsers: true);

        result.Count.ShouldBe(2);
        result.ShouldContain(w => w.Id == 1);
        result.ShouldContain(w => w.Id == 2);
    }

    [Fact]
    public async Task GetSprintItemsAsync_DeduplicatesById()
    {
        SetupStandardIterations(Sprint3);
        // Same item appears in both iterations (e.g., moved between sprints)
        var item1 = new WorkItemBuilder(1, "Task 1").WithIterationPath(@"Project\Sprint 3").Build();
        _workItemRepo.GetByIterationAsync(Sprint2, Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });
        _workItemRepo.GetByIterationAsync(Sprint3, Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });
        var sut = CreateSut();
        var expressions = new[]
        {
            IterationExpression.Parse("@current-1").Value,
            IterationExpression.Parse("@current").Value,
        };

        var result = await sut.GetSprintItemsAsync(expressions, null, allUsers: true);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task GetSprintItemsAsync_UnresolvableExpressions_ReturnsItemsFromResolvable()
    {
        SetupStandardIterations(Sprint1);
        var item1 = new WorkItemBuilder(1, "Task 1").WithIterationPath(@"Project\Sprint 1").Build();
        _workItemRepo.GetByIterationAsync(Sprint1, Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });
        var sut = CreateSut();
        var expressions = new[]
        {
            IterationExpression.Parse("@current").Value,
            IterationExpression.Parse("@current-1").Value, // out of bounds
        };

        var result = await sut.GetSprintItemsAsync(expressions, null, allUsers: true);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(1);
    }

    [Fact]
    public async Task GetSprintItemsAsync_NullUserDisplayName_QueriesAllUsers()
    {
        SetupStandardIterations(Sprint3);
        var item1 = new WorkItemBuilder(1, "Task 1").WithIterationPath(@"Project\Sprint 3").Build();
        _workItemRepo.GetByIterationAsync(Sprint3, Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });
        var sut = CreateSut();
        var expressions = new[] { IterationExpression.Parse("@current").Value };

        var result = await sut.GetSprintItemsAsync(expressions, null, allUsers: false);

        result.Count.ShouldBe(1);
        await _workItemRepo.Received(1).GetByIterationAsync(Sprint3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetSprintItemsAsync_EmptyUserDisplayName_QueriesAllUsers()
    {
        SetupStandardIterations(Sprint3);
        var item1 = new WorkItemBuilder(1, "Task 1").WithIterationPath(@"Project\Sprint 3").Build();
        _workItemRepo.GetByIterationAsync(Sprint3, Arg.Any<CancellationToken>())
            .Returns(new[] { item1 });
        var sut = CreateSut();
        var expressions = new[] { IterationExpression.Parse("@current").Value };

        var result = await sut.GetSprintItemsAsync(expressions, "", allUsers: false);

        result.Count.ShouldBe(1);
        await _workItemRepo.Received(1).GetByIterationAsync(Sprint3, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  ResolveRelativeAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveRelativeAsync_OffsetZero_ReturnsCurrent()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();

        var result = await sut.ResolveRelativeAsync(0);

        result.ShouldNotBeNull();
        result.Value.ShouldBe(Sprint3);
    }

    [Fact]
    public async Task ResolveRelativeAsync_NegativeOffset_ReturnsPrevious()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();

        var result = await sut.ResolveRelativeAsync(-1);

        result.ShouldNotBeNull();
        result.Value.ShouldBe(Sprint2);
    }

    [Fact]
    public async Task ResolveRelativeAsync_PositiveOffset_ReturnsNext()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();

        var result = await sut.ResolveRelativeAsync(1);

        result.ShouldNotBeNull();
        result.Value.ShouldBe(Sprint4);
    }

    [Fact]
    public async Task ResolveRelativeAsync_LargeNegativeOffset_ReturnsNull()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();

        var result = await sut.ResolveRelativeAsync(-10);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveRelativeAsync_LargePositiveOffset_ReturnsNull()
    {
        SetupStandardIterations(Sprint3);
        var sut = CreateSut();

        var result = await sut.ResolveRelativeAsync(10);

        result.ShouldBeNull();
    }
}
