using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task NativePublicRelationshipsLateEndpointReadCannotRecaptureDisplayedEvidence(bool retained, bool fields, bool localDraft)
    {
        var host = await CreateHumanHostAsync();
        var key = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "42");
        var other = ItemKey.Remote(host.Stamp.Connection.Provider, host.Stamp.Connection.ResourceScope, "43");
        var user = UserBenchStore.ForRegistry(host.Registry);
        using var root = EditorServices(host.Root);
        using var origin = (await root.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(default)).Value;
        using var editor = (await OpenHumanHostAsync(host.Root, host.Bench, host.Stamp, default)).Value;
        var form = (await editor.SelectRelationshipsAsync(key, other)).Value;
        var intent = ImmutableArray.Create(new CreationLink(other, "Native.DependencyForward", true));
        form.Choices.ShouldContain(intent.Single());
        form.Target.RelationshipsComplete.ShouldBeTrue();
        form.Endpoint.RelationshipsComplete.ShouldBeTrue();
        var displayedRevision = form.Endpoint.Revision.ShouldNotBeNull();
        var endpointNumber = _transport.RevisionOf(43);
        if (retained)
        {
            var staged = await editor.StageRelationshipsAsync(form.Fields.Ticket,
                fields ? FieldValues.Empty.Set("System.Title", FieldValue.String("Original retained fields")) : FieldValues.Empty, intent);
            staged.IsSuccess.ShouldBeTrue(staged.IsSuccess ? "" : staged.Error.Message);
        }
        var before = (await origin.Workspace.SnapshotAsync(default)).Value;
        var beforeDraft = origin.Workspace.DraftFingerprint(before.Drafts.SingleOrDefault(draft => draft.Key == key));
        var beforeCapture = (await user.ReadEditorCapturesAsync(default)).Value.SingleOrDefault();
        if (retained)
        {
            beforeCapture.ShouldNotBeNull().Endpoints.ShouldHaveSingleItem().Revision.ShouldBe(displayedRevision.Token);
            beforeDraft.ShouldNotBe("none");
        }
        else { beforeCapture.ShouldBeNull(); beforeDraft.ShouldBe("none"); }
        var reads = new List<int>();
        var endpointReads = 0;
        var injected = false;
        _transport.BeforeCurrentItemLookup = async id =>
        {
            reads.Add(id);
            if (id != 43 || ++endpointReads != 2) return;
            reads.ShouldBe([43, 42, 43], "The first read is the facade freshness check, followed by its target read, then the later low-level Stage endpoint read.");
            _transport.PatchAttempts.ShouldBe(0);
            _transport.RevisionOf(43).ShouldBe(endpointNumber);
            _transport.BeforeCurrentItemLookup = null;
            injected = true;
            if (localDraft)
            {
                var changed = await origin.Workspace.StageEditAsync(other,
                    FieldValues.Empty.Set("System.Title", FieldValue.String("Late independent endpoint debt")), default);
                changed.IsSuccess.ShouldBeTrue(changed.IsSuccess ? "" : changed.Error.Message);
            }
            else _transport.SetItem(43, "Late endpoint revision", endpointNumber + 1, Actor);
        };
        var saved = await editor.SaveRelationshipsAsync(form.Fields.Ticket,
            fields ? FieldValues.Empty.Set("System.Title", FieldValue.String("Must not publish after late endpoint change")) : FieldValues.Empty, intent);
        injected.ShouldBeTrue("The transport injection must reach the later low-level Stage read, not an early guard.");
        endpointReads.ShouldBe(2);
        reads.ShouldBe([43, 42, 43]);
        var after = (await origin.Workspace.SnapshotAsync(default)).Value;
        var afterCapture = (await user.ReadEditorCapturesAsync(default)).Value.SingleOrDefault();
        var afterDraft = origin.Workspace.DraftFingerprint(after.Drafts.SingleOrDefault(draft => draft.Key == key));
        var capturePreserved = beforeCapture is null ? afterCapture is null : afterCapture is not null && afterCapture.SameAs(beforeCapture);
        var detail = $"Displayed={displayedRevision.Token}; captured={afterCapture?.Endpoints.Single().Revision}; " +
            $"verified={saved.IsSuccess && saved.Value.Verified}; capturePreserved={capturePreserved}; draftPreserved={beforeDraft == afterDraft}";
        _transport.PatchAttempts.ShouldBe(0, detail);
        saved.IsSuccess.ShouldBeFalse(detail);
        saved.Error.Code.ShouldBe(ErrorCode.Conflict);
        capturePreserved.ShouldBeTrue(detail);
        afterDraft.ShouldBe(beforeDraft);
        after.Proposals.ShouldBeEmpty();
        after.Journal.ShouldBeEmpty();
        if (afterCapture is not null) afterCapture.Endpoints.ShouldHaveSingleItem().Revision.ShouldBe(displayedRevision.Token);
        if (localDraft)
            after.Drafts.Single(draft => draft.Key == other).Changes["System.Title"].Text.ShouldBe("Late independent endpoint debt");
    }
}
