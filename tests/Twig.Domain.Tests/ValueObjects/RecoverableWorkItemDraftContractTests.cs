using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Twig.Domain.Services.Plan;
using CanonicalChangeProposal = Twig.Domain.Services.ChangeProposals.ChangeProposal;
using ProjectionChangeProposal = Twig.Domain.Projections.ChangeProposal;
using Xunit;

namespace Twig.Domain.Tests.ValueObjects;

public class RecoverableWorkItemDraftContractTests
{
    private sealed class CapabilitySink(params string[] fields) : IChangeSink
    {
        public IReadOnlySet<string> PersistableFieldRefs { get; } =
            new HashSet<string>(fields, StringComparer.OrdinalIgnoreCase);

        public Task<SubmitOutcome> SubmitAsync(ProjectionChangeProposal proposal, CancellationToken ct = default)
            => Task.FromResult<SubmitOutcome>(new Saved(11));
    }

    [Fact]
    public void SharedNativeDraftContracts_ArePresentAndIdentityExcludesRevision()
    {
        var domain = typeof(FieldChange).Assembly;

        var identity = domain.GetType("Twig.Domain.ValueObjects.QualifiedWorkItemKey");
        var draft = domain.GetType("Twig.Domain.ValueObjects.RecoverableWorkItemDraft");
        var capture = domain.GetType("Twig.Domain.ValueObjects.DraftMutationCapture");
        var store = domain.GetType("Twig.Domain.Interfaces.IRecoverableWorkItemDraftStore");

        Assert.NotNull(identity);
        Assert.NotNull(draft);
        Assert.NotNull(capture);
        Assert.NotNull(store);
        Assert.Null(identity!.GetProperty("Revision"));
        Assert.NotNull(draft!.GetProperty("BaselineRevision"));
        Assert.NotNull(draft.GetProperty("Generation"));
        Assert.NotNull(draft.GetProperty("Changes"));
        Assert.NotNull(draft.GetProperty("Conflicts"));
        Assert.NotNull(capture!.GetProperty("Digest"));
        Assert.NotNull(capture.GetProperty("Authority"));
        Assert.NotNull(capture.GetProperty("CapturedDraftGeneration"));
    }

    [Fact]
    public void EditingAndRevisionObservation_AdvanceGenerationWithoutRebasingOrDroppingIntent()
    {
        var original = RecoverableWorkItemDraft.Create(Key(), baselineRevision: 7);
        var edited = original.SetField("System.Title", "before", "mine");
        var changedAgain = edited.SetField("system.title", "ignored-new-baseline", "mine again");
        var drifted = changedAgain.Observe(8,
            [new DraftFieldConflict("System.Title", "before", "server", "mine again")]);

        drifted.Generation.ShouldBe(3);
        drifted.BaselineRevision.ShouldBe(7);
        drifted.ObservedRevision.ShouldBe(8);
        drifted.HasRevisionDrift.ShouldBeTrue();
        drifted.HasConflicts.ShouldBeTrue();
        drifted.Changes.ShouldBe([
            new DraftFieldChange("system.title", "before", "mine again"),
        ]);
        drifted.Conflicts.ShouldHaveSingleItem().ObservedValue.ShouldBe("server");
    }

    [Fact]
    public void ReturningFieldToBaseline_RemovesEffectButStillInvalidatesEarlierCapture()
    {
        var original = RecoverableWorkItemDraft.Create(Key(), baselineRevision: 3);
        var edited = original.SetField("System.Title", "old", "new");
        var reverted = edited.SetField("System.Title", "old", "old");

        edited.Generation.ShouldBe(1);
        reverted.Generation.ShouldBe(2);
        reverted.Changes.ShouldBeEmpty();
        reverted.BaselineRevision.ShouldBe(3);
    }

    [Fact]
    public void CaptureBindsExactNativeEffectsDigestAuthorityAndDraftGeneration()
    {
        var draft = RecoverableWorkItemDraft.Create(Key(), baselineRevision: 11)
            .SetField("System.Title", "old", "new");
        var authority = Authority();
        var capture = DraftMutationCapture.FromCanonicalProposal(draft, Proposal(), authority, Capability("System.Title"));

        capture.ItemKey.ShouldBe(draft.ItemKey);
        capture.ExpectedRevision.ShouldBe(11);
        capture.CapturedDraftGeneration.ShouldBe(1);
        capture.Digest.ShouldBe(Proposal().Digest);
        capture.Authority.ShouldBe(authority);
        capture.Effects.ShouldBe(draft.Changes);
        capture.IsCurrentFor(draft, 11, authority).ShouldBeTrue();
        capture.IsCurrentFor(draft.SetField("System.State", "New", "Active"), 11, authority).ShouldBeFalse();
        capture.IsCurrentFor(draft, 12, authority).ShouldBeFalse();
        capture.IsCurrentFor(draft, 11, authority with { IdentityId = "different-identity" }).ShouldBeFalse();
        capture.IsCurrentFor(draft, 11, authority with { BindingId = "different-binding" }).ShouldBeFalse();
    }

    [Fact]
    public void CaptureRefusesARevisionDriftedDraftWithoutDiscardingIt()
    {
        var draft = RecoverableWorkItemDraft.Create(Key(), baselineRevision: 4)
            .SetField("System.Title", "old", "intent")
            .Observe(5);

        Should.Throw<InvalidOperationException>(() =>
            DraftMutationCapture.FromCanonicalProposal(draft, Proposal(expectedRevision: 4), Authority(), Capability("System.Title")));

        draft.Changes.ShouldContain(change => change.ProposedValue == "intent");
        draft.BaselineRevision.ShouldBe(4);
        draft.ObservedRevision.ShouldBe(5);
    }

    [Fact]
    public void CaptureRefusesProposalWhoseEffectsDifferFromDraft()
    {
        var draft = RecoverableWorkItemDraft.Create(Key(), baselineRevision: 11)
            .SetField("System.Title", "old", "mine");

        Should.Throw<InvalidOperationException>(() =>
            DraftMutationCapture.FromCanonicalProposal(draft, Proposal("other"), Authority(), Capability("System.Title")));
    }

    [Fact]
    public void CaptureRefusesProposalWithDigestThatDoesNotBindCanonicalJson()
    {
        var draft = RecoverableWorkItemDraft.Create(Key(), baselineRevision: 11)
            .SetField("System.Title", "old", "new");
        var proposal = Proposal() with { Digest = new string('a', 64) };

        Should.Throw<InvalidOperationException>(() =>
            DraftMutationCapture.FromCanonicalProposal(draft, proposal, Authority(), Capability("System.Title")));
    }

    [Fact]
    public void CaptureRefusesFieldsNotPersistableByNativeCapability()
    {
        var draft = RecoverableWorkItemDraft.Create(Key(), baselineRevision: 11)
            .SetField("System.Title", "old", "new");

        Should.Throw<InvalidOperationException>(() =>
            DraftMutationCapture.FromCanonicalProposal(draft, Proposal(), Authority(), Capability("System.State")));
    }

    private static QualifiedWorkItemKey Key()
        => new(@"C:\repo", "connection", "binding", 42);

    private static DraftAuthorityContext Authority()
        => new("binding", 2, "identity", 9, "storage");

    private static EditCapability Capability(params string[] fields)
        => new(new CapabilitySink(fields), WorkItemType.Task, isSeed: false);

    private static CanonicalChangeProposal Proposal(string value = "new", int expectedRevision = 11)
    {
        const string canonicalJson = "{\"canonical\":true}";
        return new()
        {
            CanonicalJson = canonicalJson,
            Digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson))),
            Definition = new PlanDefinition
            {
                Version = 1,
                Workspace = new PlanWorkspace { Organization = "org", Project = "project" },
                Operations =
                [
                    new BatchOperation
                    {
                        Id = "edit-item",
                        WorkItemId = 42,
                        ExpectedRevision = expectedRevision,
                        Fields = new Dictionary<string, string?> { ["System.Title"] = value },
                    },
                ],
            },
        };
    }
}
