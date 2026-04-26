using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using System.IO;
using System.Net.Http;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Unit tests for <see cref="MutationTools.State"/> (twig_state MCP tool).
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
    //  Backward transition without force — succeeds (no guard)
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_BackwardTransition_WithoutForce_Succeeds()
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

        var result = await CreateMutationSut().State("Doing", force: false);

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("state").GetString().ShouldBe("Doing");
        root.GetProperty("previousState").GetString().ShouldBe("Done");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Backward transition with force — also succeeds
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
    //  AdoException on FetchAsync — returns structured error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_FetchThrowsAdoAuthException_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoAuthenticationException());

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("Authentication failed");
    }

    // ═══════════════════════════════════════════════════════════════
    //  AdoException on PatchAsync — returns structured error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_PatchThrowsAdoServerException_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoServerException(503));

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("503");
    }

    // ═══════════════════════════════════════════════════════════════
    //  AdoRateLimitException — returns structured error with detail
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_FetchThrowsAdoRateLimitException_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoRateLimitException(TimeSpan.FromSeconds(30)));

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("Rate limited");
    }

    // ═══════════════════════════════════════════════════════════════
    //  AdoUnexpectedResponseException — returns structured error
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_FetchThrowsAdoUnexpectedResponseException_ReturnsError()
    {
        var item = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);

        var config = BuildTaskProcessConfig();
        _processConfigProvider.GetConfiguration().Returns(config);

        _adoService.FetchAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoUnexpectedResponseException(200, "text/html", "https://dev.azure.com/test", "<html>..."));

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBe(true);
        result.Content[0].ShouldBeOfType<TextContentBlock>()
            .Text.ShouldContain("non-JSON response");
    }

    // ═══════════════════════════════════════════════════════════════
    //  AutoPushNotesHelper failure — non-fatal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_AutoPushNotesFails_StillReturnsSuccess()
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

        // AutoPushNotesHelper calls GetChangesAsync — make it throw
        _pendingChangeStore.GetChangesAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Notes push failure"));

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("state").GetString().ShouldBe("Doing");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Prompt state writer failure — non-fatal
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_PromptStateWriterFails_StillReturnsSuccess()
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

        _promptStateWriter.WritePromptStateAsync()
            .ThrowsAsync(new IOException("Disk full"));

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("state").GetString().ShouldBe("Doing");
    }

    // ═══════════════════════════════════════════════════════════════
    //  Parent propagation — transition to InProgress
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task State_ToInProgress_PropagatesParentFromProposedToInProgress()
    {
        // Use Basic process config: Task & Issue both have To Do (Proposed) / Doing (InProgress) / Done (Completed)
        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Basic());

        // Child Task in Proposed with parent Issue
        var child = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").WithParent(100).Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(child);

        // Parent Issue in Proposed (To Do)
        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().InState("To Do").Build();
        parent.MarkSynced(5);

        // Child: ADO fetch (conflict check) + patch + resync fetch
        var updatedChild = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(child, updatedChild);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        // Parent: cache hit + ADO fetch for revision + patch
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.PatchAsync(100, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(6);
        _pendingChangeStore.GetDirtyItemIdsAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<int>());

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBeNull();
        // Parent Issue should have been patched to Doing
        await _adoService.Received().PatchAsync(
            100,
            Arg.Is<IReadOnlyList<FieldChange>>(c =>
                c.Count == 1 &&
                c[0].FieldName == "System.State" &&
                c[0].OldValue == "To Do" &&
                c[0].NewValue == "Doing"),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_ToInProgress_ParentAlreadyActive_NoPropagationPatch()
    {
        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Basic());

        var child = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").WithParent(100).Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(child);

        // Parent already in InProgress (Doing) — propagation is a no-op
        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().InState("Doing").Build();

        var updatedChild = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(child, updatedChild);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);

        var result = await CreateMutationSut().State("Doing");

        result.IsError.ShouldBeNull();
        // No PatchAsync for parent — it is already in InProgress
        await _adoService.DidNotReceive().PatchAsync(
            100, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task State_PropagationFailure_DoesNotAffectToolResult()
    {
        _processConfigProvider.GetConfiguration().Returns(ProcessConfigBuilder.Basic());

        // Child with parent
        var child = new WorkItemBuilder(42, "My Task").AsTask().InState("To Do").WithParent(100).Build();
        _contextStore.GetActiveWorkItemIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _workItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(child);

        var parent = new WorkItemBuilder(100, "Parent Issue").AsIssue().InState("To Do").Build();
        parent.MarkSynced(5);

        // Child succeeds
        var updatedChild = new WorkItemBuilder(42, "My Task").AsTask().InState("Doing").WithParent(100).Build();
        _adoService.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(child, updatedChild);
        _adoService.PatchAsync(42, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);

        // Parent: cache hit + fetch succeeds, but patch fails
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.FetchAsync(100, Arg.Any<CancellationToken>()).Returns(parent);
        _adoService.PatchAsync(100, Arg.Any<IReadOnlyList<FieldChange>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Parent patch failed"));

        var result = await CreateMutationSut().State("Doing");

        // Tool still returns success — propagation is best-effort
        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("state").GetString().ShouldBe("Doing");
    }
}
