using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Claims;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Services.Claims;
using Xunit;

namespace Twig.Infrastructure.Tests.Services.Claims;

/// <summary>
/// AB#737 §Mint step 2 requires the projection to verify by readback that
/// ADO's <c>System.AssignedTo</c> resolves to the intended holder before
/// reporting success. AB#737 §Release requires the same verified boundary on
/// clear. These tests exercise the seam directly against an in-memory ADO
/// fake so a normalization-losing patch fails-loud rather than promoting the
/// claim silently.
/// </summary>
public sealed class AdoClaimProjectionTests
{
    [Fact]
    public async Task Project_holder_returns_ok_when_readback_matches_intended_holder()
    {
        var ado = new FakeAdo("");
        var proj = new AdoClaimProjection(ado);
        var res = await proj.ProjectHolderAsync("42", new ClaimHolderDescriptor("jane@example.com", "Jane Doe"));
        res.IsSuccess.ShouldBeTrue(res.Error);
        ado.AssignedTo.ShouldBe("Jane Doe");
        ado.FetchCount.ShouldBe(2);
    }

    [Fact]
    public async Task Project_holder_returns_ok_no_write_when_readback_already_matches_intended()
    {
        var ado = new FakeAdo("Jane Doe");
        var proj = new AdoClaimProjection(ado);
        var res = await proj.ProjectHolderAsync("42", new ClaimHolderDescriptor("jane@example.com", "Jane Doe"));
        res.IsSuccess.ShouldBeTrue(res.Error);
        ado.PatchCount.ShouldBe(0);
        ado.AssignedTo.ShouldBe("Jane Doe");
    }

    [Fact]
    public async Task Project_holder_fails_when_readback_shows_a_different_identity()
    {
        var ado = new FakeAdo("") { NormalizeWriteTo = "Somebody Else" };
        var proj = new AdoClaimProjection(ado);
        var res = await proj.ProjectHolderAsync("42", new ClaimHolderDescriptor("jane@example.com", "Jane Doe"));
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldStartWith(AdoClaimProjection.ReadbackMismatch);
    }

    [Fact]
    public async Task Project_holder_fails_when_readback_shows_an_empty_assignment()
    {
        var ado = new FakeAdo("") { NormalizeWriteTo = "" };
        var proj = new AdoClaimProjection(ado);
        var res = await proj.ProjectHolderAsync("42", new ClaimHolderDescriptor("jane@example.com", "Jane Doe"));
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(AdoClaimProjection.ReadbackMissing);
    }

    [Fact]
    public async Task Clear_holder_returns_ok_no_write_when_already_empty()
    {
        var ado = new FakeAdo("");
        var proj = new AdoClaimProjection(ado);
        var res = await proj.ClearHolderAsync("42");
        res.IsSuccess.ShouldBeTrue();
        ado.PatchCount.ShouldBe(0);
    }

    [Fact]
    public async Task Clear_holder_fails_when_readback_still_shows_an_assignee()
    {
        var ado = new FakeAdo("Jane Doe") { RejectClear = true };
        var proj = new AdoClaimProjection(ado);
        var res = await proj.ClearHolderAsync("42");
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldStartWith(AdoClaimProjection.ClearReadbackNotEmpty);
    }

    [Fact]
    public async Task Project_holder_preserves_optimistic_concurrency_conflict_signal()
    {
        var ado = new FakeAdo("") { ThrowConflict = true };
        var proj = new AdoClaimProjection(ado);
        var res = await proj.ProjectHolderAsync("42", new ClaimHolderDescriptor("j@e.com", "Jane"));
        res.IsSuccess.ShouldBeFalse();
        res.Error.ShouldBe(AdoClaimProjection.ConflictAfterRetry);
    }

    [Fact]
    public async Task Project_holder_rejects_invalid_scope_id()
    {
        var proj = new AdoClaimProjection(new FakeAdo(""));
        (await proj.ProjectHolderAsync("not-an-int", new ClaimHolderDescriptor("j", "j"))).Error.ShouldBe(AdoClaimProjection.InvalidScopeId);
        (await proj.ProjectHolderAsync("-5", new ClaimHolderDescriptor("j", "j"))).Error.ShouldBe(AdoClaimProjection.InvalidScopeId);
        (await proj.ProjectHolderAsync("0", new ClaimHolderDescriptor("j", "j"))).Error.ShouldBe(AdoClaimProjection.InvalidScopeId);
    }

    [Fact]
    public async Task Project_holder_rejects_empty_holder_identity()
    {
        var proj = new AdoClaimProjection(new FakeAdo(""));
        var res = await proj.ProjectHolderAsync("42", new ClaimHolderDescriptor("", "Jane"));
        res.Error.ShouldBe(AdoClaimProjection.EmptyHolder);
    }

    [Theory]
    [InlineData("Jane <jane@example.com>", "jane@example.com", true)]
    [InlineData("jane@example.com", "Jane <jane@example.com>", true)]
    [InlineData("jane@example.com", "Jane <jack@example.com>", false)]
    [InlineData("Jane Doe", "Jane Doe", true)]
    [InlineData("Jane Doe", "Somebody Else", false)]
    public void Identity_matcher_accepts_upn_and_display_forms_but_refuses_a_different_upn(string observed, string intended, bool expected)
    {
        AdoClaimProjection.IdentityMatches(observed, intended).ShouldBe(expected);
    }

    private sealed class FakeAdo : IAdoWorkItemService
    {
        public string AssignedTo { get; set; }
        public int Revision { get; set; } = 1;
        public string? NormalizeWriteTo { get; set; }
        public bool RejectClear { get; set; }
        public bool ThrowConflict { get; set; }
        public int PatchCount { get; private set; }
        public int FetchCount { get; private set; }

        public FakeAdo(string assignedTo) => AssignedTo = assignedTo;

        public Task<WorkItem> FetchAsync(int id, CancellationToken ct = default)
        {
            FetchCount++;
            return Task.FromResult(new WorkItem
            {
                Id = id,
                Title = "fixture",
                AssignedTo = string.IsNullOrEmpty(AssignedTo) ? null : AssignedTo,
            });
        }

        public Task<int> PatchAsync(int id, IReadOnlyList<FieldChange> changes, int expectedRevision, CancellationToken ct = default)
        {
            if (ThrowConflict) throw new AdoConflictException(expectedRevision + 1, "conflict");
            PatchCount++;
            foreach (var c in changes)
            {
                if (c.FieldName != "System.AssignedTo") continue;
                if (RejectClear && c.NewValue is null) continue;
                if (c.NewValue is null) AssignedTo = string.Empty;
                else if (NormalizeWriteTo is not null) AssignedTo = NormalizeWriteTo;
                else AssignedTo = c.NewValue;
            }
            Revision++;
            return Task.FromResult(Revision);
        }

        public Task<(WorkItem Item, IReadOnlyList<WorkItemLink> Links)> FetchWithLinksAsync(int id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkItemLink> Links)> FetchBatchWithLinksAsync(IReadOnlyList<int> ids, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> FetchChildrenAsync(int parentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CreateAsync(CreateWorkItemRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int?> FindPublishedIntentAsync(PublishIntent intent, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ClearIntentTagAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddCommentAsync(int id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<int>> QueryByWiqlAsync(string wiql, int top, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<WorkItem>> FetchBatchAsync(IReadOnlyList<int> ids, CancellationToken ct = default) => throw new NotImplementedException();
        public Task AddLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default) => Task.CompletedTask;
        public Task AddLinkWithCommentAsync(int sourceId, int targetId, string adoLinkType, string? comment, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveLinkAsync(int sourceId, int targetId, string adoLinkType, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> AddArtifactLinkAsync(int workItemId, string url, string? name = null, CancellationToken ct = default) => Task.FromResult(false);
        public Task DeleteAsync(int id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<WorkItemHistory> FetchHistoryAsync(int id, WorkItemHistoryOptions options, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
