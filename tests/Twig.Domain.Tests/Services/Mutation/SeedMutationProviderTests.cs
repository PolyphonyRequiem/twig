using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Mutation;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Domain.Tests.Services.Mutation;

public sealed class SeedMutationProviderTests
{
    private readonly IWorkItemRepository _workItemRepo = Substitute.For<IWorkItemRepository>();
    private readonly SeedMutationProvider _sut;

    public SeedMutationProviderTests()
    {
        _sut = new SeedMutationProvider(_workItemRepo);
    }

    // ═══════════════════════════════════════════════════════════════
    //  UpdateFieldAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task UpdateField_OnSeed_UpdatesAndSaves()
    {
        var seed = new WorkItemBuilder(-1, "Seed task").AsSeed().Build();
        _workItemRepo.GetByIdAsync(-1, Arg.Any<CancellationToken>()).Returns(seed);

        var change = new FieldChange("System.Title", "Seed task", "Updated title");
        var result = await _sut.UpdateFieldAsync(-1, change, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.ErrorMessage.ShouldBeNull();
        result.NewRevision.ShouldNotBeNull();
        seed.Fields["System.Title"].ShouldBe("Updated title");
        seed.IsDirty.ShouldBeTrue();
        await _workItemRepo.Received(1).SaveAsync(seed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateField_OnNonSeed_ReturnsError()
    {
        var published = new WorkItemBuilder(42, "Published task").Build();
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(published);

        var change = new FieldChange("System.Title", "Published task", "New title");
        var result = await _sut.UpdateFieldAsync(42, change, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("not a seed");
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<Domain.Aggregates.WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateField_OnMissingItem_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((Domain.Aggregates.WorkItem?)null);

        var change = new FieldChange("System.Title", null, "New title");
        var result = await _sut.UpdateFieldAsync(999, change, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("not found");
    }

    // ═══════════════════════════════════════════════════════════════
    //  ChangeStateAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task ChangeState_OnSeed_UpdatesAndSaves()
    {
        var seed = new WorkItemBuilder(-2, "Seed bug").AsSeed().InState("New").Build();
        _workItemRepo.GetByIdAsync(-2, Arg.Any<CancellationToken>()).Returns(seed);

        var stateChange = new FieldChange("System.State", "New", "Active");
        var result = await _sut.ChangeStateAsync(-2, stateChange, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.NewRevision.ShouldNotBeNull();
        seed.State.ShouldBe("Active");
        seed.IsDirty.ShouldBeTrue();
        await _workItemRepo.Received(1).SaveAsync(seed, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeState_OnNonSeed_ReturnsError()
    {
        var published = new WorkItemBuilder(50, "Published item").InState("New").Build();
        _workItemRepo.GetByIdAsync(50, Arg.Any<CancellationToken>()).Returns(published);

        var stateChange = new FieldChange("System.State", "New", "Active");
        var result = await _sut.ChangeStateAsync(50, stateChange, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("not a seed");
        await _workItemRepo.DidNotReceive().SaveAsync(Arg.Any<Domain.Aggregates.WorkItem>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeState_OnMissingItem_ReturnsError()
    {
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>()).Returns((Domain.Aggregates.WorkItem?)null);

        var stateChange = new FieldChange("System.State", "New", "Active");
        var result = await _sut.ChangeStateAsync(999, stateChange, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("not found");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ChangeState_OnNullOrEmptyState_ReturnsError(string? newState)
    {
        var stateChange = new FieldChange("System.State", "New", newState);
        var result = await _sut.ChangeStateAsync(-1, stateChange, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("null or empty");
        await _workItemRepo.DidNotReceive().GetByIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  MutationResult value object
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public void MutationResult_Success_SetsFields()
    {
        var result = MutationResult.Success(5);
        result.IsSuccess.ShouldBeTrue();
        result.NewRevision.ShouldBe(5);
        result.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public void MutationResult_Error_SetsFields()
    {
        var result = MutationResult.Error("Something went wrong");
        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldBe("Something went wrong");
        result.NewRevision.ShouldBeNull();
    }
}
