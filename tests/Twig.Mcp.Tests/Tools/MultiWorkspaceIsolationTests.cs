using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Mcp.Services;
using Twig.Mcp.Tools;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Integration tests verifying that two workspaces with independent mock sets keep
/// pending changes fully isolated. The active-context isolation cases that used to live
/// here went away with twig_set (wayfinder 0021) — a workspace is now resolved per call
/// rather than latched by a context-setting tool.
/// </summary>
public sealed class MultiWorkspaceIsolationTests : ReadToolsTestBase
{
    private static readonly Connection WsAlpha = new("orgA", "projectA");
    private static readonly Connection WsBeta = new("orgB", "projectB");

    // ═══════════════════════════════════════════════════════════════
    //  Pending changes: note in A does not stage in B
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Note_InWorkspaceA_DoesNotStagePendingInWorkspaceB()
    {
        var (resolver, mocks) = BuildMultiResolver(DefaultConfig, WsAlpha, WsBeta);

        // Item 42 is resolvable in workspace A only.
        var item = new WorkItemBuilder(42, "Alpha Feature").AsFeature().InState("Active").Build();
        mocks[WsAlpha].WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns(item);
        mocks[WsBeta].WorkItemRepo.GetByIdAsync(42, Arg.Any<CancellationToken>()).Returns((WorkItem?)null);

        // Make ADO AddComment fail to force local staging
        mocks[WsAlpha].AdoService.AddCommentAsync(42, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Network error")));

        var mutationTools = new MutationTools(resolver);
        // With two registered workspaces and no latching tool, the target workspace must be
        // named on the call — that is the point of 0021: resolution is explicit, not inherited.
        var result = await mutationTools.Note("Test note for A", id: 42, workspace: "orgA/projectA");

        result.IsError.ShouldBeNull();

        // Workspace A should have staged the pending change (text converted Markdown→HTML by default)
        await mocks[WsAlpha].PendingChangeStore.Received(1).AddChangeAsync(
            42, "note", Arg.Any<string?>(), Arg.Any<string?>(), "<p>Test note for A</p>\n", Arg.Any<CancellationToken>());

        // Workspace B should not have any pending change interactions
        await mocks[WsBeta].PendingChangeStore.DidNotReceive().AddChangeAsync(
            Arg.Any<int>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

}
