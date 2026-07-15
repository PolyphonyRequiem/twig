using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Tests for the auto-chaining behavior of <c>twig_state</c> when ADO rejects a
/// direct transition (multi-hop walks through intermediate states).
/// </summary>
public sealed class MutationToolsStateChainTests : MutationToolsTestBase
{
    /// <summary>4-state config so a multi-hop chain has room to be exercised.</summary>
    private static ProcessConfiguration BuildChainConfig() =>
        BuildProcessConfig(WorkItemType.UserStory,
            ("New", 1), ("Active", 2), ("Resolved", 3), ("Closed", 4));

    private static AdoBadRequestException TransitionError(string from, string to)
        => new($"TF401320: state transition from '{from}' to '{to}' is not allowed");

    [Fact]
    public async Task State_DirectRejected_ChainsThroughIntermediates()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("New").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _processConfigProvider.GetConfiguration().Returns(BuildChainConfig());

        var updated = new WorkItemBuilder(42, "story").AsUserStory().InState("Closed").Build();
        // First Fetch is for ConflictResolutionFlow / executor's expectedRevision; then resync.
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item, updated);

        // Direct New → Closed: rejected
        _adoService.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Closed" && c.Single().OldValue == "New"),
                0, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("New", "Closed"));
        _adoService.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Active" && c.Single().OldValue == "New"),
                0, Arg.Any<CancellationToken>())
            .Returns(1);
        _adoService.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Resolved" && c.Single().OldValue == "Active"),
                1, Arg.Any<CancellationToken>())
            .Returns(2);
        _adoService.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Closed" && c.Single().OldValue == "Resolved"),
                2, Arg.Any<CancellationToken>())
            .Returns(3);

        var result = await CreateMutationSut().State("Closed");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("state").GetString().ShouldBe("Closed");
        root.GetProperty("previousState").GetString().ShouldBe("New");
        root.GetProperty("transitionCount").GetInt32().ShouldBe(3);
        var path = root.GetProperty("path");
        path.GetArrayLength().ShouldBe(4);
        path[0].GetString().ShouldBe("New");
        path[1].GetString().ShouldBe("Active");
        path[2].GetString().ShouldBe("Resolved");
        path[3].GetString().ShouldBe("Closed");
    }

    [Fact]
    public async Task State_ChainStopsMidPath_ReturnsErrorWithReachedStates()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("New").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _processConfigProvider.GetConfiguration().Returns(BuildChainConfig());

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Closed" && c.Single().OldValue == "New"),
                0, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("New", "Closed"));
        _adoService.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().NewValue == "Active" && c.Single().OldValue == "New"),
                0, Arg.Any<CancellationToken>())
            .Returns(1);
        // All paths from Active are rejected
        _adoService.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "Active"),
                1, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("Active", "?"));

        var result = await CreateMutationSut().State("Closed");

        result.IsError.ShouldBe(true);
        var msg = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        msg.ShouldContain("chain stopped at 'Active'");
        msg.ShouldContain("New → Active");
    }

    [Fact]
    public async Task State_ChainFieldValidation_ReturnsValidationCodeAndReachedState()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("New").Build();
        var active = new WorkItemBuilder(42, "story").AsUserStory().InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _processConfigProvider.GetConfiguration().Returns(BuildChainConfig());
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item, active);

        _adoService.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "New" && c.Single().NewValue == "Closed"),
                0, Arg.Any<CancellationToken>())
            .ThrowsAsync(TransitionError("New", "Closed"));
        _adoService.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "New" && c.Single().NewValue == "Active"),
                0, Arg.Any<CancellationToken>())
            .Returns(1);
        _adoService.PatchAsync(42,
                Arg.Is<IReadOnlyList<FieldChange>>(c => c.Single().OldValue == "Active" && c.Single().NewValue == "Resolved"),
                1, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoBadRequestException(
                "Rule Error for field Substate. Value Ready is not allowed."));

        var result = await CreateMutationSut().State("Closed");

        result.IsError.ShouldBe(true);
        var error = ParseResult(result).GetProperty("error");
        error.GetProperty("code").GetString().ShouldBe("ADO_VALIDATION_FAILED");
        var message = error.GetProperty("message").GetString();
        message.ShouldNotBeNull();
        message.ShouldContain("chain stopped at 'Active'");
        message.ShouldContain("Substate");
    }

    [Fact]
    public async Task State_SingleHop_OmitsPathFromJson()
    {
        var item = new WorkItemBuilder(42, "story").AsUserStory().InState("New").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _processConfigProvider.GetConfiguration().Returns(BuildChainConfig());

        var updated = new WorkItemBuilder(42, "story").AsUserStory().InState("Active").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item, updated);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
                Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await CreateMutationSut().State("Active");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("state").GetString().ShouldBe("Active");
        // Single-hop chains omit the path/transitionCount fields to keep the
        // common-case envelope minimal.
        root.TryGetProperty("path", out _).ShouldBeFalse();
        root.TryGetProperty("transitionCount", out _).ShouldBeFalse();
    }
}
