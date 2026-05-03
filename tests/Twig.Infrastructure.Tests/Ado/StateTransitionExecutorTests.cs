using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.TestKit;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class StateTransitionExecutorTests
{
    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();

    private static TypeConfig AgileUserStoryConfig() =>
        ProcessConfigBuilder.Agile().TypeConfigs[WorkItemType.UserStory];

    private static TypeConfig BasicTaskConfig() =>
        ProcessConfigBuilder.Basic().TypeConfigs[WorkItemType.Task];

    private static AdoBadRequestException TransitionError(string from, string to)
        => new($"TF401320: The state transition from '{from}' to '{to}' is not allowed for this work item.");

    [Fact]
    public async Task DirectTransition_Succeeds_NoChaining()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("New").Build();
        var typeConfig = AgileUserStoryConfig();

        _ado.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Active"),
                10, Arg.Any<CancellationToken>())
            .Returns(11);

        var result = await StateTransitionExecutor.ExecuteAsync(_ado, item, "Active", typeConfig, 10);

        result.IsSuccess.ShouldBeTrue();
        result.Path.ShouldBe(["New", "Active"]);
        result.FinalState.ShouldBe("Active");
        result.FinalRevision.ShouldBe(11);
        result.TransitionCount.ShouldBe(1);
        await _ado.Received(1).PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MultiHop_AgileNewToClosed_ChainsThroughActiveAndResolved()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("New").Build();
        var typeConfig = AgileUserStoryConfig();

        // Direct New → Closed: rejected
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "New" && c.Single().NewValue == "Closed"),
                10, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("New", "Closed"));

        // Intermediate New → Active: succeeds
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "New" && c.Single().NewValue == "Active"),
                10, Arg.Any<CancellationToken>())
            .Returns(11);

        // Intermediate Active → Resolved: succeeds
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "Active" && c.Single().NewValue == "Resolved"),
                11, Arg.Any<CancellationToken>())
            .Returns(12);

        // Final Resolved → Closed: succeeds
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "Resolved" && c.Single().NewValue == "Closed"),
                12, Arg.Any<CancellationToken>())
            .Returns(13);

        var result = await StateTransitionExecutor.ExecuteAsync(_ado, item, "Closed", typeConfig, 10);

        result.IsSuccess.ShouldBeTrue();
        result.Path.ShouldBe(["New", "Active", "Resolved", "Closed"]);
        result.FinalState.ShouldBe("Closed");
        result.FinalRevision.ShouldBe(13);
        result.TransitionCount.ShouldBe(3);
    }

    [Fact]
    public async Task BackwardChain_ClosedToNew_WalksReversedOrder()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("Closed").Build();
        var typeConfig = AgileUserStoryConfig();

        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "Closed" && c.Single().NewValue == "New"),
                10, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("Closed", "New"));

        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "Closed" && c.Single().NewValue == "Resolved"),
                10, Arg.Any<CancellationToken>())
            .Returns(11);
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "Resolved" && c.Single().NewValue == "Active"),
                11, Arg.Any<CancellationToken>())
            .Returns(12);
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "Active" && c.Single().NewValue == "New"),
                12, Arg.Any<CancellationToken>())
            .Returns(13);

        var result = await StateTransitionExecutor.ExecuteAsync(_ado, item, "New", typeConfig, 10);

        result.IsSuccess.ShouldBeTrue();
        result.Path.ShouldBe(["Closed", "Resolved", "Active", "New"]);
        result.TransitionCount.ShouldBe(3);
    }

    [Fact]
    public async Task MidChainFailure_StopsAtLastSuccessfulIntermediate()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("New").Build();
        var typeConfig = AgileUserStoryConfig();

        // Direct New → Closed: rejected
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "New" && c.Single().NewValue == "Closed"),
                10, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("New", "Closed"));
        // New → Active: succeeds
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "New" && c.Single().NewValue == "Active"),
                10, Arg.Any<CancellationToken>())
            .Returns(11);
        // Active → Resolved: rejected (simulate broken workflow edge)
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "Active" && c.Single().NewValue == "Resolved"),
                11, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("Active", "Resolved"));
        // Active → Closed: also rejected (final retry)
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "Active" && c.Single().NewValue == "Closed"),
                11, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("Active", "Closed"));

        var result = await StateTransitionExecutor.ExecuteAsync(_ado, item, "Closed", typeConfig, 10);

        result.IsSuccess.ShouldBeFalse();
        result.Path.ShouldBe(["New", "Active"]);
        result.FinalState.ShouldBe("Active");
        result.FinalRevision.ShouldBe(11);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("Active");
    }

    [Fact]
    public async Task NonTransitionError_OnDirectAttempt_RethrowsImmediately()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("New").Build();
        var typeConfig = AgileUserStoryConfig();

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 10, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoAuthenticationException());

        await Should.ThrowAsync<AdoAuthenticationException>(
            () => StateTransitionExecutor.ExecuteAsync(_ado, item, "Active", typeConfig, 10));

        await _ado.Received(1).PatchAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NonTransitionError_OnIntermediate_RethrowsAndAbortsChain()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("New").Build();
        var typeConfig = AgileUserStoryConfig();

        // Direct rejected
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Closed"),
                10, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("New", "Closed"));
        // First intermediate hits a 500
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Active"),
                10, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoServerException(503));

        await Should.ThrowAsync<AdoServerException>(
            () => StateTransitionExecutor.ExecuteAsync(_ado, item, "Closed", typeConfig, 10));
    }

    [Fact]
    public async Task EndpointMissingFromTypeStates_FailsWithClearMessage()
    {
        // Item is in a state that's not part of the type's States list (custom workflow edge)
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("UnlistedState").Build();
        var typeConfig = AgileUserStoryConfig();

        _ado.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 10, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("UnlistedState", "Closed"));

        var result = await StateTransitionExecutor.ExecuteAsync(_ado, item, "Closed", typeConfig, 10);

        result.IsSuccess.ShouldBeFalse();
        result.Path.ShouldBe(["UnlistedState"]);
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("Cannot chain transition");
        result.ErrorMessage.ShouldContain("UnlistedState");
    }

    [Fact]
    public async Task AdjacentStates_NoIntermediates_FailsImmediatelyOnRejection()
    {
        // Basic: To Do → Doing are adjacent. If direct fails, there are no intermediates
        // so we should fail immediately without extra round trips.
        var item = new WorkItemBuilder(42, "task").AsTask().InState("To Do").Build();
        var typeConfig = BasicTaskConfig();

        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Doing"),
                10, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("To Do", "Doing"));

        var result = await StateTransitionExecutor.ExecuteAsync(_ado, item, "Doing", typeConfig, 10);

        result.IsSuccess.ShouldBeFalse();
        result.Path.ShouldBe(["To Do"]);
        // The "final retry" PATCH (To Do → Doing) is what produces the surface error.
        // Only the direct attempt and the final retry should fire — no intermediates.
        await _ado.Received(2).PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), 10, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IntermediateRejected_SkipsToNextCandidate()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("New").Build();
        var typeConfig = AgileUserStoryConfig();

        // Direct New → Closed: rejected
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Closed" && c.Single().OldValue == "New"),
                10, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("New", "Closed"));
        // First intermediate Active: rejected (simulate odd workflow)
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Active" && c.Single().OldValue == "New"),
                10, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("New", "Active"));
        // Second intermediate Resolved: succeeds (skip-ahead)
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Resolved" && c.Single().OldValue == "New"),
                10, Arg.Any<CancellationToken>())
            .Returns(11);
        // Final Resolved → Closed: succeeds
        _ado.PatchAsync(42, Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Closed" && c.Single().OldValue == "Resolved"),
                11, Arg.Any<CancellationToken>())
            .Returns(12);

        var result = await StateTransitionExecutor.ExecuteAsync(_ado, item, "Closed", typeConfig, 10);

        result.IsSuccess.ShouldBeTrue();
        result.Path.ShouldBe(["New", "Resolved", "Closed"]);
        result.TransitionCount.ShouldBe(2);
    }

    [Fact]
    public void ComputeIntermediatePath_ForwardSlice_ExclusiveOfEndpoints()
    {
        var states = new[] { "New", "Active", "Resolved", "Closed", "Removed" };

        var path = StateTransitionExecutor.ComputeIntermediatePath(states, "New", "Closed");

        path.ShouldNotBeNull();
        path.ShouldBe(["Active", "Resolved"]);
    }

    [Fact]
    public void ComputeIntermediatePath_BackwardSlice_ReversedOrder()
    {
        var states = new[] { "New", "Active", "Resolved", "Closed" };

        var path = StateTransitionExecutor.ComputeIntermediatePath(states, "Closed", "New");

        path.ShouldNotBeNull();
        path.ShouldBe(["Resolved", "Active"]);
    }

    [Fact]
    public void ComputeIntermediatePath_AdjacentStates_EmptyList()
    {
        var states = new[] { "New", "Active" };

        var path = StateTransitionExecutor.ComputeIntermediatePath(states, "New", "Active");

        path.ShouldNotBeNull();
        path.ShouldBeEmpty();
    }

    [Fact]
    public void ComputeIntermediatePath_FromMissing_ReturnsNull()
    {
        var states = new[] { "New", "Active" };

        StateTransitionExecutor.ComputeIntermediatePath(states, "Phantom", "Active").ShouldBeNull();
    }

    [Fact]
    public void ComputeIntermediatePath_ToMissing_ReturnsNull()
    {
        var states = new[] { "New", "Active" };

        StateTransitionExecutor.ComputeIntermediatePath(states, "New", "Phantom").ShouldBeNull();
    }
}
