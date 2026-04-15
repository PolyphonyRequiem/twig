using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="MutationTools.State"/> (twig.state MCP tool).
/// </summary>
public sealed class MutationToolsStateTests : MutationToolsTestBase
{
    private static ProcessConfiguration BuildTaskProcessConfig() =>
        BuildProcessConfig(WorkItemType.Task,
            ("To Do", 1), ("Doing", 2), ("Done", 3));

    // ═══════════════════════════════════════════════════════════════
    //  Validation — empty state name
    // ═══════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task State_EmptyStateName_ReturnsError(string stateName)
    {
        var result = await CreateMutationSut().State(stateName);

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("requires a target state name");
    }

    // ═══════════════════════════════════════════════════════════════
    //  No context — no active item
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_NoContext_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("No active work item");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Unreachable — item in context but not in cache or ADO
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_Unreachable_ReturnsError()
    {
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>())
            .Returns(999);
        _workItemRepo.GetByIdAsync(999, Arg.Any<CancellationToken>())
            .Returns((WorkItem?)null);
        _adoService.FetchAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("not found"));

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("not found in cache");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Unknown type — not in process config
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_UnknownType_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Feature").AsFeature().InState("Active").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        // Process config only has Task — Feature is unknown
        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("No process configuration found for type");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Invalid state name — StateResolver returns error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_InvalidStateName_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        var result = await CreateMutationSut().State("Nonexistent");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("Unknown state");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Already in target state — no ADO call
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_AlreadyInTargetState_ReturnsSuccess()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBeNull();
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("Already in state");

        // No ADO patch call should have been made
        await _adoService.DidNotReceive().PatchAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Forward transition — happy path
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_ForwardTransition_PushesAndReturnsNewState()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        // FetchAsync for ConflictRetryHelper pre-patch fetch
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        // Post-state-change resync: return updated item
        var updatedItem = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(item, updatedItem);

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(42);
        root.GetProperty("state").GetString().ShouldBe("Doing");
        root.GetProperty("previousState").GetString().ShouldBe("To Do");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Backward transition without force — confirmation required
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_BackwardTransition_WithoutForce_ReturnsConfirmationError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Done").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        var result = await CreateMutationSut().State("Doing", force: false);

        result.IsError.ShouldBe(true);
        var text = result.Content[0].ShouldBeOfType<TextContentBlock>().Text;
        text.ShouldContain("requires confirmation");
        text.ShouldContain("force");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Backward transition with force — succeeds
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_BackwardTransition_WithForce_Succeeds()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("Done").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        var updatedItem = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(item, updatedItem);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().State("Doing", force: true);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("state").GetString().ShouldBe("Doing");
        root.GetProperty("previousState").GetString().ShouldBe("Done");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Disallowed transition — type not in transition rules
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_DisallowedTransition_ReturnsError()
    {
        // Build a config for Feature only — transition from state not in Feature config
        var featureConfig = BuildProcessConfig(WorkItemType.Feature,
            ("New", 1), ("Active", 2), ("Closed", 3));

        var item = new WorkItemBuilder(42, "My Feature").AsFeature().InState("Custom").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _processConfigProvider.GetConfiguration().Returns(featureConfig);

        // "Active" resolves fine via StateResolver, but "Custom" → "Active" won't be in transition rules
        var result = await CreateMutationSut().State("Active");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("not allowed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Resync failure — non-fatal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_ResyncFails_StillReturnsSuccess()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        // First FetchAsync call (for ConflictRetryHelper) succeeds
        // PatchAsync succeeds
        // Second FetchAsync call (resync) fails
        var callCount = 0;
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callCount++;
                if (callCount == 1)
                    return item; // For ConflictRetryHelper
                throw new InvalidOperationException("Resync network failure");
            });
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().State("Doing");

        // Tool should still return success even though resync failed
        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(42);
        // Falls back to original item state since resync failed
        root.GetProperty("previousState").GetString().ShouldBe("To Do");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Prompt state writer called after success
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_PromptStateWriterCalledAfterSuccess()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        await CreateMutationSut().State("Doing");

        await _promptStateWriter.Received(1).WritePromptStateAsync();
    }

    // ═══════════════════════════════════════════════════════════════
    //  Response JSON contains previousState
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_ReturnJson_ContainsPreviousState()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        var updatedItem = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .Returns(item, updatedItem);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.TryGetProperty("previousState", out var previousStateProp).ShouldBe(true);
        previousStateProp.GetString().ShouldBe("To Do");
    }
}
