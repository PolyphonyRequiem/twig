using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.TestKit;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Integration tests for <see cref="ContextTools.Set"/> context change wiring.
/// Verifies that <c>ContextChangeService.ExtendWorkingSetAsync</c> is invoked
/// and that extension failures do not fail the tool call.
/// Child/link hydration behavior is covered by <c>ContextChangeServiceTests</c>.
/// </summary>
public sealed class ContextTools_ContextChangeTests : ContextToolsTestBase
{
    // ═══════════════════════════════════════════════════════════════
    //  Wiring — twig.set invokes ExtendWorkingSetAsync
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_ById_InvokesExtendWorkingSetAsync()
    {
        var item = new WorkItemBuilder(100, "Parent Story")
            .AsFeature().InState("Active").Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);

        var result = await CreateSut().Set("100");

        result.IsError.ShouldBeNull();
        // ExtendWorkingSetAsync calls SyncChildrenAsync → FetchChildrenAsync
        await _adoService.Received().FetchChildrenAsync(100, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Set_ByPattern_InvokesExtendWorkingSetAsync()
    {
        var item = new WorkItemBuilder(100, "Unique Story")
            .AsFeature().InState("Active").Build();
        _workItemRepo.FindByPatternAsync("Unique Story", Arg.Any<CancellationToken>())
            .Returns(new[] { item });
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);

        var result = await CreateSut().Set("Unique Story");

        result.IsError.ShouldBeNull();
        await _adoService.Received().FetchChildrenAsync(100, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Resilience — extension failure does not fail tool call
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_ExtensionFailure_DoesNotFailToolCall()
    {
        var item = new WorkItemBuilder(100, "Test Item")
            .AsFeature().InState("Active").Build();
        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);

        _adoService.FetchChildrenAsync(100, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network unreachable"));

        var result = await CreateSut().Set("100");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        root.GetProperty("id").GetInt32().ShouldBe(100);
        await _contextStore.Received().SetActiveWorkItemIdAsync(100, Arg.Any<CancellationToken>());
    }

    // ═══════════════════════════════════════════════════════════════
    //  Working set summary — response includes parent/child counts
    // ═══════════════════════════════════════════════════════════════

    [Fact]
    public async Task Set_ResponseIncludesWorkingSetSummary()
    {
        var parent = new WorkItemBuilder(50, "Parent").AsEpic().InState("Active").Build();
        var child = new WorkItemBuilder(201, "Child").AsTask().InState("New").WithParent(100).Build();
        var item = new WorkItemBuilder(100, "Feature")
            .AsFeature().InState("Active").WithParent(50).Build();

        _workItemRepo.GetByIdAsync(100, Arg.Any<CancellationToken>()).Returns(item);
        _workItemRepo.GetByIdAsync(50, Arg.Any<CancellationToken>()).Returns(parent);
        _workItemRepo.GetParentChainAsync(50, Arg.Any<CancellationToken>()).Returns(new[] { parent });
        _workItemRepo.GetChildrenAsync(100, Arg.Any<CancellationToken>()).Returns(new[] { child });

        var result = await CreateSut().Set("100");

        result.IsError.ShouldBeNull();
        var root = ParseResult(result);
        var workingSet = root.GetProperty("workingSet");
        workingSet.GetProperty("parentChainCount").GetInt32().ShouldBe(1);
        workingSet.GetProperty("childCount").GetInt32().ShouldBe(1);
    }
}
