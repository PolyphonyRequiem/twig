using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Core;
using Twig.Providers.Ado;
using Twig.Store.Sqlite;
using Xunit;

namespace Twig.Providers.Ado.Tests;

public sealed class AdoProviderTests
{
    [Fact]
    public async Task PlanningSourcesRetainActualQueryHierarchyAndBacklogIdentityAndBrowseNativeMembership()
    {
        using var f = await Fixture.Create();
        var sources = await f.Provider.DiscoverPlanningSourcesAsync(f.Connection, "Selected team", default);
        sources.IsSuccess.ShouldBeTrue(sources.IsSuccess ? "" : sources.Error.Message);
        sources.Value.Team.ShouldBe(Fixture.Process);
        sources.Value.Entries.Count(source => source.IsFolder).ShouldBe(1);
        sources.Value.Entries.ShouldContain(source => source.Id == NativeHttp.FlatQuery && source.Path == "Shared Queries/Flat query");
        sources.Value.Entries.ShouldContain(source => source.Kind == PlanningSourceKind.Backlog && source.Id == "Native.Requirements");
        var flat = await f.Provider.BrowsePlanningAsync(f.Connection, "Selected team", PlanningSourceKind.SavedQuery, NativeHttp.FlatQuery, Fixture.Process, default);
        flat.IsSuccess.ShouldBeTrue(flat.IsSuccess ? "" : flat.Error.Message);
        flat.Value.Page.Items.Select(item => item.Key).ShouldBe([f.Key(2)]);
        var tree = await f.Provider.BrowsePlanningAsync(f.Connection, "Selected team", PlanningSourceKind.SavedQuery, NativeHttp.TreeQuery, Fixture.Process, default);
        tree.IsSuccess.ShouldBeTrue(tree.IsSuccess ? "" : tree.Error.Message);
        tree.Value.Page.Items.Select(item => item.Key).ShouldBe([f.Key(1), f.Key(2)]);
        tree.Value.Page.Relationships.ShouldBeEmpty("native query link rows are not invented hierarchy observations");
        var backlog = await f.Provider.BrowsePlanningAsync(f.Connection, "Selected team", PlanningSourceKind.Backlog, "Native.Requirements", Fixture.Process, default);
        backlog.IsSuccess.ShouldBeTrue(backlog.IsSuccess ? "" : backlog.Error.Message);
        backlog.Value.Page.Items.Select(item => item.Key).ShouldBe([f.Key(1), f.Key(2)]);
        f.Http.BacklogMembershipReads.ShouldBe(1);
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Version.ShouldBe(0, "the provider boundary returns observations; the native host owns mirroring");
    }

    [Theory]
    [InlineData("truncated-folder")]
    [InlineData("changed-folder")]
    [InlineData("sources-denied")]
    [InlineData("query-denied")]
    [InlineData("unknown-query-shape")]
    [InlineData("oversized-membership")]
    [InlineData("incomplete-batch")]
    public async Task PlanningSourceBrowseRefusesIncompleteChangedDeniedOrUnboundedNativeEvidence(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.SourceDefect = defect;
        var result = await f.Provider.BrowsePlanningAsync(f.Connection, "Selected team", PlanningSourceKind.SavedQuery, NativeHttp.FlatQuery, Fixture.Process, default);
        result.IsSuccess.ShouldBeFalse();
        if (defect is "sources-denied" or "query-denied") result.Error.Evidence.ShouldContain("http:403");
        f.Http.WorkItemReads.ShouldBe(0);
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Version.ShouldBe(0);
    }

    [Fact]
    public async Task PlanningBrowseUsesDocumentedReadOnlyBatchesWithoutNarrowingQueriesToTwoHundredItems()
    {
        using var f = await Fixture.Create();
        f.Http.SourceResultIds = Enumerable.Range(1, 201).ToArray();
        var result = await f.Provider.BrowsePlanningAsync(f.Connection, "Selected team", PlanningSourceKind.SavedQuery, NativeHttp.FlatQuery, Fixture.Process, default);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Page.Items.Length.ShouldBe(201);
        result.Value.Page.Complete.ShouldBeTrue();
        f.Http.PlanningBatchSizes.ShouldBe([200, 1]);
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task PlanningBrowseRefusesAChangedCanonicalTeamBeforeExecutingMembershipAndGenericQueriesNeverBroadenReferences()
    {
        using var f = await Fixture.Create();
        var changed = await f.Provider.BrowsePlanningAsync(f.Connection, "Selected team", PlanningSourceKind.SavedQuery,
            NativeHttp.FlatQuery, Fixture.Actor, default);
        changed.Error.Code.ShouldBe(ErrorCode.Conflict);
        f.Http.PlanningMembershipReads.ShouldBe(0);
        var generic = await f.Provider.QueryAsync(f.Connection,
            new(QueryKind.SavedQuery, value: FieldValue.String(NativeHttp.FlatQuery), planningTeam: Fixture.Process), null, default);
        generic.Error.Code.ShouldBe(ErrorCode.Unsupported);
        f.Http.QueryAfterIds.ShouldBeEmpty();
        f.Http.WorkItemReads.ShouldBe(0);
        f.Http.PlanningBatchSizes.ShouldBeEmpty();
    }

    [Fact]
    public async Task PlanningDiscoveryUsesExactTeamCalendarWithoutReadingOrMutatingWorkItems()
    {
        using var f = await Fixture.Create();
        var result = await f.Provider.DiscoverPlanningAsync(f.Connection, "Selected team", default);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Team.ShouldBe(Fixture.Process);
        result.Value.Areas.ShouldContain("Native Project\\Area");
        result.Value.Iterations.Single(iteration => iteration.Path == "Native Project\\Sprint").SelectedTeam.ShouldBeTrue();
        result.Value.Iterations.Single(iteration => iteration.Path == "Native Project\\Other team's sprint").SelectedTeam.ShouldBeFalse();
        var absent = await f.Provider.DiscoverPlanningAsync(f.Connection, null, default);
        absent.IsSuccess.ShouldBeTrue();
        absent.Value.Team.ShouldBeNull();
        absent.Value.Iterations.ShouldAllBe(iteration => !iteration.SelectedTeam);
        absent.Value.Gaps.ShouldNotBeEmpty();
        f.Http.WorkItemReads.ShouldBe(0);
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Version.ShouldBe(0);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("denied")]
    [InlineData("foreign-team")]
    public async Task PlanningDiscoveryRefusesIncompleteDeniedAndWrongTeamMetadata(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.PlanningDefect = defect;
        var result = await f.Provider.DiscoverPlanningAsync(f.Connection, "Selected team", default);
        result.IsSuccess.ShouldBeFalse();
        if (defect == "denied") result.Error.Evidence.ShouldContain("http:403");
        f.Http.WorkItemReads.ShouldBe(0);
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task PresentationRetainsNativeAppearanceColumnsOrderingAndExactProcessTypeWithoutChangingMutationDigest()
    {
        using var f = await Fixture.Create();
        f.Http.LayoutJson = """
            {"pages":[{"id":"details","label":"Details","pageType":"custom","sections":[
              {"id":"left","groups":[{"id":"planning","label":"Planning","controls":[
                {"id":"Custom.Closure","label":"Closure","controlType":"FieldControl","order":20,"readOnly":true},
                {"id":"Custom.Empty","label":"Summary","controlType":"HtmlFieldControl","order":10,"watermark":"Describe the work","height":120}
              ]}]},
              {"id":"right","groups":[{"id":"custom","label":"Custom","controls":[
                {"id":"extension.control","label":"Extension","isContribution":true,"visible":false}
              ]}]}
            ]}],"systemControls":[{"id":"System.State","label":"Status","controlType":"FieldControl"}]}
            """;
        f.Http.DivergentProjectType = true;
        var ordinary = await f.Schema();
        f.Http.LayoutRequests.ShouldBeEmpty();
        var result = await ((IProvider)f.Provider).DiscoverSchemaAsync(f.Connection, default, includePresentation: true);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        var schema = result.Value;
        schema.Fingerprint.ShouldBe(ordinary.Fingerprint);
        schema.WebUrl.ShouldBe($"https://example.test/organization/{Fixture.Project}/");
        var presentation = schema.Types.Single().Presentation.ShouldNotBeNull();
        presentation.Name.ShouldBe(f.Http.Type);
        presentation.Color.ShouldBe("B4009E");
        presentation.IconId.ShouldBe("icon_crown");
        presentation.IconSvg.ShouldNotBeNull().ShouldContain("<svg");
        var layout = presentation.Layout.ShouldNotBeNull();
        layout.Availability.ShouldBe(LayoutAvailability.Served);
        layout.ProcessId.ShouldBe(Fixture.Process);
        layout.TypeReference.ShouldBe(f.Http.Type);
        var sections = layout.Pages.Single().Sections;
        sections.Select(s => s.Id).ShouldBe(["left", "right"]);
        var controls = sections[0].Groups.Single().Controls;
        controls.Select(c => c.Id).ShouldBe(["Custom.Empty", "Custom.Closure"]);
        controls[0].Visible.ShouldBeTrue();
        controls[0].Watermark.ShouldBe("Describe the work");
        controls[0].Height.ShouldBe(120);
        controls[1].ReadOnly.ShouldBeTrue();
        sections[1].Groups.Single().Controls.Single().IsContribution.ShouldBeTrue();
        sections[1].Groups.Single().Controls.Single().Visible.ShouldBeFalse();
        layout.SystemControls.Single().Id.ShouldBe("System.State");
        f.Http.LayoutRequests.ShouldBe([$"/organization/_apis/work/processes/{Fixture.Process}/workItemTypes/{f.Http.Type}/layout"]);
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData(200, LayoutAvailability.Served)]
    [InlineData(404, LayoutAvailability.Unavailable)]
    [InlineData(400, LayoutAvailability.Locked)]
    public async Task PresentationDistinguishesEmptyMissingAndLockedLayout(int status, LayoutAvailability expected)
    {
        using var f = await Fixture.Create();
        f.Http.LayoutJson = """{"pages":[],"systemControls":[]}""";
        f.Http.LayoutStatus = status;
        var result = await ((IProvider)f.Provider).DiscoverSchemaAsync(f.Connection, default, true);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        var layout = result.Value.Types.Single().Presentation.ShouldNotBeNull().Layout.ShouldNotBeNull();
        layout.Availability.ShouldBe(expected);
        layout.Pages.ShouldBeEmpty();
        layout.SystemControls.ShouldBeEmpty();
        f.Http.LayoutRequests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task PresentationDoesNotDisguiseAuthorizationOrTransportFailureAsMissingLayout(int status)
    {
        using var f = await Fixture.Create();
        f.Http.LayoutJson = """{"pages":[]}""";
        f.Http.LayoutStatus = status;
        var result = await ((IProvider)f.Provider).DiscoverSchemaAsync(f.Connection, default, true);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Evidence.ShouldContain($"http:{status}");
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreationLinksDispatchExactDirectionsAndRequireEveryReadback(bool missingReadback)
    {
        using var f = await Fixture.Create();
        f.Http.Defect = "creation-links";
        var target = (await f.Read(1)).Items.Single();
        var dependency = (await f.Read(2)).Items.Single();
        var schema = await f.Schema();
        schema.Gaps.ShouldBeEmpty();
        schema.Relations.ShouldContain(relation => relation.Reference == "Native.Association"
            && relation.Shape == RelationShape.Association);
        f.Provider.Capabilities.NativeRelations.ShouldContain("Native.Association");
        var seed = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Initial)),
            ("Custom.Empty", FieldValue.String("Retained dependency seed")));
        CreationLink[] links = [new(target.Key, "Native.Association", true),
            new(dependency.Key, "Native.DependencyForward", false)];
        var stored = await f.Stored();
        (await f.Store.CommitAsync(f.Connection, stored.Version,
            new(stored.Version + 1, [target, dependency], drafts: [new(seed, f.Http.Type, null, fields, links: links)]),
            default)).IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic, links: links));
        var imported = await f.Provider.ImportAsync(proposal, proposal.Digest);
        imported.IsSuccess.ShouldBeTrue(imported.IsSuccess ? "" : imported.Error.Message);
        var result = await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Status.ShouldBe(OpStatus.Applied);
        f.Http.Writes.ShouldBe(1);
        f.Access.SawDurableFence.ShouldBeTrue();
        using var payload = JsonDocument.Parse(f.Http.LastPayload!);
        var nativeLinks = payload.RootElement.EnumerateArray().Where(entry => entry.GetProperty("path").GetString() == "/relations/-")
            .Select(entry => entry.GetProperty("value")).ToArray();
        nativeLinks.Length.ShouldBe(2);
        nativeLinks[0].GetProperty("rel").GetString().ShouldBe("Native.Association");
        nativeLinks[0].GetProperty("url").GetString().ShouldEndWith("/1");
        nativeLinks[1].GetProperty("rel").GetString().ShouldBe("Native.DependencyReverse");
        nativeLinks[1].GetProperty("url").GetString().ShouldEndWith("/2");
        if (missingReadback) f.Http.Defect = "creation-links-readback-mismatch";
        var verified = await f.Provider.VerifyAsync(proposal, result.Value, default);
        verified.IsSuccess.ShouldBeTrue();
        verified.Value.Status.ShouldBe(missingReadback ? OpStatus.Partial : OpStatus.Verified);
        var retained = await f.Stored();
        retained.Journal.Single().Status.ShouldBe(missingReadback ? OpStatus.Partial : OpStatus.Verified);
        retained.Mappings.Single().Local.ShouldBe(seed);
        retained.Drafts.Single().Links.ShouldBe(links);
        retained.Proposals.Single().Operations.Single().Links.ShouldBe(links);
        f.Http.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData("Hyperlink", "https://example.test/evidence?a=%2F", null, "exact comment")]
    [InlineData("ArtifactLink", "vstfs:///Git/Commit/project%2Frepository%2Fcommit", "Fixed in Commit", null)]
    public async Task ResourceLinksAreOneRevisionCheckedEffectWithExactPersistentIntentAndReadback(
        string relation, string target, string? name, string? comment)
    {
        using var f = await Fixture.Create();
        var item = (await f.Read(1)).Items.Single();
        item.ResourcesComplete.ShouldBeTrue();
        item.Resources.ShouldBeEmpty();
        var resource = new ResourceLink(relation, target, name, comment);
        var proposal = await f.Proposal(new("resource", OpKind.LinkResource, item.Key, item.Revision, resource: resource));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        f.Access.SawDurableFence.ShouldBeTrue();
        f.Http.Writes.ShouldBe(1);
        using var patch = JsonDocument.Parse(f.Http.LastPayload!);
        patch.RootElement.GetArrayLength().ShouldBe(2);
        patch.RootElement[0].GetProperty("path").GetString().ShouldBe("/rev");
        var payload = patch.RootElement[1].GetProperty("value");
        payload.GetProperty("rel").GetString().ShouldBe(relation);
        payload.GetProperty("url").GetString().ShouldBe(target);
        var attributes = payload.GetProperty("attributes");
        attributes.TryGetProperty(name is null ? "name" : "comment", out _).ShouldBeFalse();
        attributes.GetProperty(name is null ? "comment" : "name").GetString().ShouldBe(name ?? comment);
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        var reopened = (await SqliteStore.OpenAsync(f.Path)).Value;
        var retained = (await reopened.ReadAsync(f.Connection, default)).Value;
        retained.Proposals.Single().Digest.ShouldBe(proposal.Digest);
        retained.Proposals.Single().Operations.Single().Resource.ShouldBe(resource);
        retained.Items.Single().Resources.ShouldBe([resource]);
        var again = await f.Proposal(new("again", OpKind.LinkResource, item.Key,
            retained.Items.Single().Revision, resource: resource));
        (await f.Provider.ImportAsync(again, again.Digest)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, OpStatus.Failed)]
    [InlineData(true, OpStatus.Partial)]
    public async Task ResourceRevisionRaceOrDifferentReadbackCannotBecomeVerifiedOrReplay(bool changedReadback, OpStatus expected)
    {
        using var f = await Fixture.Create();
        var item = (await f.Read(1)).Items.Single();
        var resource = new ResourceLink("Hyperlink", "https://example.test/retained", comment: "why this link");
        var proposal = await f.Proposal(new("resource", OpKind.LinkResource, item.Key, item.Revision, resource: resource));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Http.MoveAtDispatch = !changedReadback;
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        if (changedReadback)
        {
            ack.Status.ShouldBe(OpStatus.Applied);
            f.Http.Defect = "resource-readback-mismatch";
            (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(expected);
            (await f.Stored()).Journal.Single().Receipts[0].Status.ShouldBe(OpStatus.Applied);
        }
        else ack.Status.ShouldBe(expected);
        (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task BatchFieldsAndReviewedNoteUseOneAtomicRevisionCheckedRequest()
    {
        using var f = await Fixture.Create();
        var item = (await f.Read(1)).Items.Single();
        var proposal = await f.Proposal(new("batch-note", OpKind.Batch, item.Key, item.Revision,
            Fields(("Custom.Empty", FieldValue.String("updated"))), body: FieldValue.RichText("<p>Reviewed note</p>", FieldKind.Html)));
        var imported = await f.Provider.ImportAsync(proposal, proposal.Digest);
        imported.IsSuccess.ShouldBeTrue(imported.IsSuccess ? "" : imported.Error.Message);
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        f.Http.Writes.ShouldBe(1);
        using var patch = JsonDocument.Parse(f.Http.LastPayload!);
        patch.RootElement.GetArrayLength().ShouldBe(3);
        patch.RootElement[0].GetProperty("path").GetString().ShouldBe("/rev");
        patch.RootElement[2].GetProperty("path").GetString().ShouldBe("/fields/System.History");
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        (await f.Stored()).Journal.Single().Receipts.Length.ShouldBe(2);
    }

    [Fact]
    public async Task TypeOnlyChangeUsesOneNativeRevisionTestAndNoSyntheticFieldEdit()
    {
        using var f = await Fixture.Create();
        f.Http.AdditionalType = "Other.NativeType";
        var item = (await f.Read(1)).Items.Single();
        item.Type.ShouldNotBe(f.Http.AdditionalType);
        var proposal = await f.Proposal(new("type", OpKind.Batch, item.Key, item.Revision, type: f.Http.AdditionalType));
        var imported = await f.Provider.ImportAsync(proposal, proposal.Digest);
        imported.IsSuccess.ShouldBeTrue(imported.IsSuccess ? "" : imported.Error.Message);
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        using var patch = JsonDocument.Parse(f.Http.LastPayload!);
        patch.RootElement.GetArrayLength().ShouldBe(2);
        patch.RootElement[0].GetProperty("path").GetString().ShouldBe("/rev");
        patch.RootElement[1].GetProperty("path").GetString().ShouldBe("/fields/System.WorkItemType");
        patch.RootElement[1].GetProperty("value").GetString().ShouldBe(f.Http.AdditionalType);
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        (await f.Stored()).Items.Single().Type.ShouldBe(f.Http.AdditionalType);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task DisabledResourceCapabilityDoesNotDisableOrdinaryReadsOrAuthorizeArtifactWrites()
    {
        using var f = await Fixture.Create();
        f.Http.Defect = "resource-disabled";
        var item = (await f.Read(1)).Items.Single();
        item.ResourcesComplete.ShouldBeTrue();
        var schema = await f.Schema();
        schema.Gaps.ShouldBeEmpty();
        f.Provider.Capabilities.Operations.ShouldNotContain(OpKind.LinkResource);
        var proposal = await f.Proposal(new("resource", OpKind.LinkResource, item.Key, item.Revision,
            resource: new("Hyperlink", "https://example.test/resource")));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("Contoso.Initiative", "Waiting", "Delivered")]
    [InlineData("Other.Finding", "Queued", "Settled")]
    public async Task DiscoveryAndAtomicCloseUseNativeVocabularyAndRealStore(string type, string initial, string completed)
    {
        using var f = await Fixture.Create(type, initial, completed);
        var schema = await f.Schema();
        schema.Types.Single().Reference.ShouldBe(type);
        schema.Types.Single().States.Select(s => s.Name).ShouldBe([initial, completed]);
        schema.Gaps.ShouldBeEmpty();
        var read = await f.Read(1);
        var item = read.Items.Single();
        item.Type.ShouldBe(type);
        item.RelationshipsComplete.ShouldBeTrue();
        item.Fields["Custom.Points"].ShouldBe(FieldValue.Decimal(2.5m));
        item.Fields["Custom.Owner"].ShouldBe(FieldValue.Identity(Fixture.Actor));
        item.Fields["Custom.Unset"].ShouldBe(FieldValue.Null);
        item.Fields["Custom.Empty"].ShouldBe(FieldValue.String(""));
        var gateOnly = new Op("close", OpKind.Batch, item.Key, item.Revision,
            Fields(("System.State", FieldValue.String(completed))));
        var invalid = await f.Provider.ImportAsync(new(f.Connection, schema.Fingerprint, 0, [gateOnly]), "", default);
        invalid.IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0);

        var fields = Fields(("System.State", FieldValue.String(completed)), ("Custom.Closure", FieldValue.String("Evidence retained")));
        var proposal = await f.Proposal(new("close", OpKind.Batch, item.Key, item.Revision, fields));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var acknowledgement = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        acknowledgement.Status.ShouldBe(OpStatus.Applied);
        f.Access.SawDurableFence.ShouldBeTrue();
        f.Http.Writes.ShouldBe(1);
        using var patch = JsonDocument.Parse(f.Http.LastPayload!);
        patch.RootElement[0].GetProperty("op").GetString().ShouldBe("test");
        patch.RootElement[0].GetProperty("path").GetString().ShouldBe("/rev");
        patch.RootElement[0].GetProperty("value").GetInt32().ShouldBe(4);
        patch.RootElement.GetArrayLength().ShouldBe(3);
        var stored = await f.Stored();
        stored.Journal.Single().Status.ShouldBe(OpStatus.Applied);
        stored.Items.Single().Revision.ShouldBe(acknowledgement.Revision);
        var verified = (await f.Provider.VerifyAsync(proposal, acknowledgement, default)).Value;
        verified.Status.ShouldBe(OpStatus.Verified);
        (await f.Stored()).Journal.Single().Receipts.Length.ShouldBe(2);
    }

    [Theory]
    [InlineData("unknown-rule")]
    [InlineData("malformed-required")]
    [InlineData("missing-rules")]
    [InlineData("missing-transitions")]
    [InlineData("unknown-category")]
    [InlineData("unknown-field-kind")]
    [InlineData("default-value")]
    [InlineData("duplicate-json")]
    [InlineData("metadata-page")]
    public async Task IncompleteSchemaNeverBecomesPermission(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.Defect = defect;
        var discovered = await f.Provider.DiscoverSchemaAsync(f.Connection, default);
        if (discovered.IsSuccess)
        {
            var item = (await f.Read(1)).Items.Single();
            var proposal = new Proposal(f.Connection, discovered.Value.Fingerprint, 0,
                [new("edit", OpKind.Batch, item.Key, item.Revision, Fields(("Custom.Empty", FieldValue.String("edit"))))]);
            (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeFalse();
        }
        else discovered.Error.Code.ShouldBeOneOf(ErrorCode.Unavailable, ErrorCode.Unsupported);
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task OmittedOptionalRequiredFlagsPreserveExplicitRequiredFields()
    {
        using var f = await Fixture.Create();
        f.Http.Defect = "omitted-optional-required";
        var discovered = await f.Provider.DiscoverSchemaAsync(f.Connection, default);
        f.Http.SawOmittedRequiredFlag.ShouldBeTrue();
        f.Http.SawExplicitRequiredFlag.ShouldBeTrue();
        discovered.IsSuccess.ShouldBeTrue();
        var type = discovered.Value.Types.Single();
        type.Rules.ShouldContain(rule => rule.Field == "Custom.Unset"
            && rule.Action == RuleAction.Required && rule.Conditions.IsEmpty);
        type.Rules.ShouldNotContain(rule => rule.Field == "Custom.Empty"
            && rule.Action == RuleAction.Required);
        var item = (await f.Read(1)).Items.Single();
        var proposal = new Proposal(f.Connection, discovered.Value.Fingerprint, (await f.Stored()).Version,
            [new("edit", OpKind.Batch, item.Key, item.Revision,
                Fields(("Custom.Empty", FieldValue.String("edit"))))]);
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("Custom.Owner", FieldKind.Identity)]
    [InlineData("Custom.Points", FieldKind.Decimal)]
    [InlineData("Custom.Quantity", FieldKind.Integer)]
    [InlineData("Custom.Toggle", FieldKind.Boolean)]
    [InlineData("Custom.Date", FieldKind.Instant)]
    public async Task EmptyTypedRuleOperandsRetainUnsetConditionsAndSuppliedValues(string field, FieldKind expectedKind)
    {
        using var f = await Fixture.Create();
        f.Http.Defect = "empty-typed-rule";
        f.Http.EmptyRuleField = field;
        var discovered = await f.Provider.DiscoverSchemaAsync(f.Connection, default);
        f.Http.SawEmptyIdentityOperand.ShouldBeTrue();
        discovered.IsSuccess.ShouldBeTrue();
        var type = discovered.Value.Types.Single();
        type.Fields.Single(definition => definition.Reference == field).Kind.ShouldBe(expectedKind);
        var rules = type.Rules;
        rules.ShouldContain(rule => rule.Field == "Custom.Empty" && rule.Action == RuleAction.Required
            && rule.Conditions.Any(condition => condition.Field == field
                && condition.Kind == ConditionKind.Equal && condition.Value == FieldValue.Null));
        rules.ShouldContain(rule => rule.Field == field && rule.Action == RuleAction.Supplied
            && rule.Values.Contains(FieldValue.Null));
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData(400, OpStatus.Failed)]
    [InlineData(401, OpStatus.Failed)]
    [InlineData(403, OpStatus.Failed)]
    [InlineData(409, OpStatus.Failed)]
    [InlineData(412, OpStatus.Failed)]
    [InlineData(429, OpStatus.Failed)]
    [InlineData(500, OpStatus.Indeterminate)]
    [InlineData(503, OpStatus.Indeterminate)]
    [InlineData(202, OpStatus.Indeterminate)]
    public async Task NativeRejectionsAndUnknownDispatchHaveDifferentDurableReceipts(int status, OpStatus expected)
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Http.WriteStatus = status;
        var result = await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Status.ShouldBe(expected);
        result.Value.Evidence.ShouldContain($"http:{status}");
        (await f.Stored()).Journal.Single().Status.ShouldBe(expected);
        (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task DiscoveryHttpFailureIsExplicitAndNeverCachedAsEmpty(int status)
    {
        using var f = await Fixture.Create();
        (await f.Schema()).Types.ShouldNotBeEmpty();
        f.Http.MetadataStatus = status;
        var result = await f.Provider.DiscoverSchemaAsync(f.Connection, default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Evidence.ShouldContain($"http:{status}");
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("canceled")]
    [InlineData("malformed")]
    [InlineData("lost-create-response")]
    public async Task AmbiguousCreateIsFencedAcrossChangedDigestsAndReopen(string failure)
    {
        using var f = await Fixture.Create();
        var seed = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Initial)), ("Custom.Empty", FieldValue.String("seed")));
        var stored = await f.Stored();
        (await f.Store.CommitAsync(f.Connection, stored.Version,
            new(stored.Version + 1, drafts: [new(seed, f.Http.Type, null, fields)]), default)).IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Http.WriteFailure = failure;
        var acknowledgement = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        acknowledgement.Status.ShouldBe(OpStatus.Indeterminate);
        f.Http.Writes.ShouldBe(1);
        var reopened = (await SqliteStore.OpenAsync(f.Path)).Value;
        using var provider = new AdoProvider(f.Connection, f.Access, reopened, f.Http.NewHandle());
        (await provider.RecoverAsync(proposal, 0)).Value.Status.ShouldBe(OpStatus.Indeterminate);
        var snapshot = (await reopened.ReadAsync(f.Connection, default)).Value;
        var replacement = new Proposal(f.Connection, proposal.SchemaFingerprint, snapshot.Version,
            [new("replacement", OpKind.Create, seed, fields: fields, type: f.Http.Type, concurrency: ConcurrencyMode.ObservedNonAtomic)]);
        (await provider.ImportAsync(replacement, replacement.Digest)).IsSuccess.ShouldBeFalse();
        (await provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
        snapshot.Mappings.ShouldBeEmpty();
        snapshot.Drafts.Single().Key.ShouldBe(seed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrderedCreationsResolveOnlyTheVerifiedOriginalParentUuid(bool unknownParent)
    {
        using var f = await Fixture.Create();
        f.Http.Defect = "creation-links";
        var parent = ItemKey.Local(Guid.CreateVersion7());
        var child = ItemKey.Local(Guid.CreateVersion7());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Initial)), ("Custom.Empty", FieldValue.String("seed")));
        CreationLink[] links = [new(parent, "Native.DependencyForward", false)];
        var drafts = new[] { new Draft(parent, f.Http.Type, null, fields),
            new Draft(child, f.Http.Type, null, fields, parent, "Native.ParentForward", links: links) };
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: drafts), default)).IsSuccess.ShouldBeTrue();
        var schema = await f.Schema();
        var proposal = new Proposal(f.Connection, schema.Fingerprint, 1, [
            new("parent", OpKind.Create, parent, fields: fields, type: f.Http.Type, concurrency: ConcurrencyMode.ObservedNonAtomic),
            new("child", OpKind.Create, child, fields: fields, type: f.Http.Type, other: parent,
                relation: "Native.ParentForward", concurrency: ConcurrencyMode.ObservedNonAtomic, links: links),
        ]);
        var imported = await f.Provider.ImportAsync(proposal, proposal.Digest);
        imported.IsSuccess.ShouldBeTrue(imported.IsSuccess ? "" : imported.Error.Message);
        (await f.Provider.ApplyAsync(proposal, 1, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0, "a local reference is not publication authority before its exact predecessor verifies");
        if (unknownParent) f.Http.WriteFailure = "lost-create-response";
        var parentAck = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.ApplyAsync(proposal, 1, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        if (unknownParent)
        {
            parentAck.Status.ShouldBe(OpStatus.Indeterminate);
            f.Http.Writes.ShouldBe(1);
            var fenced = await f.Stored();
            fenced.Mappings.ShouldBeEmpty();
            fenced.Drafts.ShouldContain(draft => draft.Key == child && draft.Links.SequenceEqual(links));
            (await f.Provider.RecoverAsync(proposal, 0)).Value.Status.ShouldBe(OpStatus.Indeterminate);
            (await f.Provider.ApplyAsync(proposal, 1, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
            f.Http.Writes.ShouldBe(1);
            return;
        }
        (await f.Provider.VerifyAsync(proposal, parentAck, default)).Value.Status.ShouldBe(OpStatus.Verified);
        var childAck = (await f.Provider.ApplyAsync(proposal, 1, Guid.NewGuid(), default)).Value;
        childAck.Status.ShouldBe(OpStatus.Applied);
        (await f.Provider.VerifyAsync(proposal, childAck, default)).Value.Status.ShouldBe(OpStatus.Verified);
        var snapshot = await f.Stored();
        snapshot.Mappings.Length.ShouldBe(2);
        snapshot.Relationships.ShouldContain(new Relationship(parentAck.PublishedKey!, childAck.PublishedKey!,
            "Native.ParentForward", RelationshipOrigin.Provider));
        snapshot.Proposals.Single().Operations[1].Other.ShouldBe(parent, "retained intent must not be rewritten to the native ID");
        snapshot.Proposals.Single().Operations[1].Links.Single().Other.ShouldBe(parent);
        snapshot.Relationships.ShouldContain(new Relationship(parentAck.PublishedKey!, childAck.PublishedKey!,
            "Native.DependencyForward", RelationshipOrigin.Provider));
        f.Http.Writes.ShouldBe(2);
    }

    [Fact]
    public async Task CoreCreationPreservesDescriptionMarkupWithoutTheRetiredOriginParser()
    {
        using var f = await Fixture.Create();
        f.Http.Defect = "description-html";
        var schema = await f.Schema();
        schema.Types.Single().Fields.Single(field => field.Reference == "System.Description").Kind.ShouldBe(FieldKind.Html);
        const string description = "<p><em><token></em></p>";
        var seed = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Initial)),
            ("System.Description", FieldValue.RichText(description, FieldKind.Html)));
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: [new(seed, f.Http.Type, null, fields)]), default))
            .IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        var imported = await f.Provider.ImportAsync(proposal, proposal.Digest);
        imported.IsSuccess.ShouldBeTrue(imported.IsSuccess ? "" : imported.Error.Message);
        var result = await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? "" : result.Error.Message);
        result.Value.Status.ShouldBe(OpStatus.Applied);
        using var payload = JsonDocument.Parse(f.Http.LastPayload!);
        payload.RootElement.EnumerateArray().Single(entry => entry.GetProperty("path").GetString() == "/fields/System.Description")
            .GetProperty("value").GetString().ShouldBe(description);
        (await f.Provider.VerifyAsync(proposal, result.Value, default)).Value.Status.ShouldBe(OpStatus.Verified);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task CreationBindsParentAndResourceIntoOneNativeCallAndPersistentDraft()
    {
        using var f = await Fixture.Create();
        var seed = ItemKey.Local(Guid.CreateVersion7());
        var parent = f.Key(1);
        var fields = Fields(("System.State", FieldValue.String(f.Http.Initial)), ("Custom.Empty", FieldValue.String("parented seed")));
        var resource = new ResourceLink("Hyperlink", "https://example.test/creation-evidence", comment: "reviewed during creation");
        var draft = new Draft(seed, f.Http.Type, null, fields, parent, "Native.ParentForward", resource);
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: [draft]), default)).IsSuccess.ShouldBeTrue();
        var reopened = (await SqliteStore.OpenAsync(f.Path)).Value;
        var retained = (await reopened.ReadAsync(f.Connection, default)).Value.Drafts.Single();
        retained.Parent.ShouldBe(parent);
        retained.Resource.ShouldBe(resource);
        var omitted = await f.Proposal(new("omitted", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(omitted, omitted.Digest)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0);
        var proposal = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            other: parent, relation: "Native.ParentForward", resource: resource, concurrency: ConcurrencyMode.ObservedNonAtomic));
        var imported = await f.Provider.ImportAsync(proposal, proposal.Digest);
        imported.IsSuccess.ShouldBeTrue(imported.IsSuccess ? "" : imported.Error.Message);
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        f.Http.Writes.ShouldBe(1);
        using var payload = JsonDocument.Parse(f.Http.LastPayload!);
        payload.RootElement.EnumerateArray().Count(entry => entry.GetProperty("path").GetString() == "/relations/-").ShouldBe(2);
        var verified = (await f.Provider.VerifyAsync(proposal, ack, default)).Value;
        verified.Status.ShouldBe(OpStatus.Verified);
        var snapshot = await f.Stored();
        snapshot.Mappings.Single().Local.ShouldBe(seed);
        snapshot.Relationships.ShouldContain(new Relationship(parent, ack.PublishedKey!, "Native.ParentForward", RelationshipOrigin.Provider));
        snapshot.Items.Single(item => item.Key == ack.PublishedKey).Resources.ShouldContain(resource);
        (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task CreateAcknowledgementMappingMirrorAndReadbackSurviveReopen()
    {
        using var f = await Fixture.Create();
        var seed = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Initial)), ("Custom.Empty", FieldValue.String("seed")));
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: [new(seed, f.Http.Type, null, fields)]), default)).IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        ack.Status.ShouldBe(OpStatus.Applied);
        var reopened = (await SqliteStore.OpenAsync(f.Path)).Value;
        var snapshot = (await reopened.ReadAsync(f.Connection, default)).Value;
        snapshot.Mappings.Single().ShouldBe(new PublishMapping(seed, ack.PublishedKey!, proposal.Digest, 0));
        snapshot.Items.ShouldContain(i => i.Key == ack.PublishedKey);
        snapshot.Drafts.Single().Key.ShouldBe(seed);
        using var provider = new AdoProvider(f.Connection, f.Access, reopened, f.Http.NewHandle());
        (await provider.RecoverAsync(proposal, 0)).Value.Status.ShouldBe(OpStatus.Verified);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task DurableApplyingRecoveryNeverInventsNonDispatchOrReplaysComment()
    {
        using var f = await Fixture.Create();
        var item = (await f.Read(1)).Items.Single();
        var proposal = await f.Proposal(new("comment", OpKind.Comment, item.Key, item.Revision, body: FieldValue.String("note")));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var stored = await f.Stored();
        var applying = stored.Journal.Single().Begin(Guid.NewGuid()).Value;
        (await f.Store.CommitAsync(f.Connection, stored.Version,
            new(stored.Version + 1, stored.Items, stored.Relationships, stored.Drafts, stored.Proposals, [applying]), default)).IsSuccess.ShouldBeTrue();
        (await f.Provider.RecoverAsync(proposal, 0)).Value.Status.ShouldBe(OpStatus.Indeterminate);
        var replacement = await f.Proposal(new("different-digest", OpKind.Comment, item.Key, item.Revision, body: FieldValue.String("changed")));
        (await f.Provider.ImportAsync(replacement, replacement.Digest)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("readback-denied", OpStatus.Indeterminate)]
    [InlineData("readback-mismatch", OpStatus.Partial)]
    [InlineData("readback-canceled", OpStatus.Indeterminate)]
    public async Task VerificationDoesNotTurnAcknowledgementIntoNoEffect(string defect, OpStatus expected)
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        f.Http.Defect = defect;
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(expected);
        var journal = (await f.Stored()).Journal.Single();
        journal.Receipts[0].Status.ShouldBe(OpStatus.Applied);
        journal.Receipts.Length.ShouldBe(2);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task RevisionMovedBeforeAcquisitionAndAtTransportCannotWriteStaleFields()
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Http.Items[1]["rev"] = 5;
        var refused = await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        refused.IsSuccess.ShouldBeFalse();
        refused.Error.Code.ShouldBe(ErrorCode.Conflict);
        f.Http.Writes.ShouldBe(0);
        f.Http.Items[1]["rev"] = 4;
        f.Http.MoveAtDispatch = true;
        var rejected = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        rejected.Status.ShouldBe(OpStatus.Failed);
        f.Http.Items[1]["fields"]!["Custom.Empty"]!.GetValue<string>().ShouldBe("");
    }

    [Fact]
    public async Task CompetingAdapterAcquisitionHasOneWinnerWithoutNetworkInsideStoreTransaction()
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        using var other = new AdoProvider(f.Connection, f.Access, (await SqliteStore.OpenAsync(f.Path)).Value, f.Http.NewHandle());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Access.BeforeAdmission = async () => { entered.SetResult(); await release.Task; };
        var first = f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var loser = await other.ApplyAsync(proposal, 0, Guid.NewGuid(), default);
        loser.IsSuccess.ShouldBeFalse();
        // A separate real SQLite writer can commit while admission/transport are paused.
        var current = await f.Stored();
        (await f.Store.CommitAsync(f.Connection, current.Version, new(current.Version + 1, current.Items,
            current.Relationships, current.Drafts, current.Proposals, current.Journal, current.Mappings, current.Benches), default)).IsSuccess.ShouldBeTrue();
        release.SetResult();
        (await first).Value.Status.ShouldBe(OpStatus.Applied);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task FieldTypingClearsAndHistoryCommentsSendExactIntent()
    {
        using var f = await Fixture.Create();
        var item = (await f.Read(1)).Items.Single();
        var bad = await f.Proposal(new("bad", OpKind.Batch, item.Key, item.Revision,
            Fields(("Custom.Points", FieldValue.String("3")))));
        (await f.Provider.ImportAsync(bad, bad.Digest)).IsSuccess.ShouldBeFalse();
        var edit = await f.Proposal(new("clear", OpKind.Batch, item.Key, item.Revision,
            Fields(("Custom.Points", FieldValue.Null), ("Custom.Empty", FieldValue.String("")))));
        (await f.Provider.ImportAsync(edit, edit.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(edit, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(edit, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        using (var patch = JsonDocument.Parse(f.Http.LastPayload!))
            patch.RootElement.EnumerateArray().Single(p => p.GetProperty("path").GetString() == "/fields/Custom.Points")
                .GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        item = (await f.Read(1)).Items.Single();
        var comment = await f.Proposal(new("comment", OpKind.Comment, item.Key, item.Revision,
            body: FieldValue.RichText("<p>Reviewed &amp; retained</p>", FieldKind.Html)));
        (await f.Provider.ImportAsync(comment, comment.Digest)).IsSuccess.ShouldBeTrue();
        ack = (await f.Provider.ApplyAsync(comment, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(comment, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        using var commentPatch = JsonDocument.Parse(f.Http.LastPayload!);
        commentPatch.RootElement[1].GetProperty("value").GetString().ShouldBe("<p>Reviewed &amp; retained</p>");
    }

    [Fact]
    public async Task RelationshipsNormalizeProvenanceVerifyBothEndsAndPreserveLocalTombstonesDraftsAndBench()
    {
        using var f = await Fixture.Create();
        var read = await f.Read(1, 2);
        var edge = new Relationship(f.Key(1), f.Key(2), "Native.ParentForward", RelationshipOrigin.Local, 1, true);
        var draft = new Draft(ItemKey.Local(Guid.NewGuid()), f.Http.Type, null, Fields(("System.State", FieldValue.String(f.Http.Initial))));
        var bench = new Bench(Guid.NewGuid(), "Retained", [new(SelectorKind.Pin, f.Key(1))]);
        (await f.Store.CommitAsync(f.Connection, 0, new(1, read.Items, [edge], [draft], benches: [bench]), default)).IsSuccess.ShouldBeTrue();
        var op = new Op("link", OpKind.Relate, f.Key(1), read.Items[0].Revision, other: f.Key(2), relation: "Native.ParentForward");
        var proposal = await f.Proposal(op);
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        var mirrored = await f.Stored();
        mirrored.Relationships.ShouldContain(edge);
        mirrored.Relationships.ShouldContain(edge with { Origin = RelationshipOrigin.Provider, LocalVersion = 0, Deleted = false });
        mirrored.Drafts.Single().ShouldBe(draft);
        mirrored.Benches.Single().Id.ShouldBe(bench.Id);
        var item = (await f.Read(1)).Items.Single();
        var unlink = await f.Proposal(new("unlink", OpKind.Unrelate, f.Key(1), item.Revision, other: f.Key(2), relation: op.Relation));
        (await f.Provider.ImportAsync(unlink, unlink.Digest)).IsSuccess.ShouldBeTrue();
        ack = (await f.Provider.ApplyAsync(unlink, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(unlink, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        (await f.Stored()).Relationships.ShouldBe([edge]);
    }

    [Fact]
    public async Task ExactQueryUsesOrdinalTypedAstAndRefusesMalformedCursorAndMissingNativeItems()
    {
        using var f = await Fixture.Create();
        f.Http.Items[1]["fields"]!["Custom.Empty"] = "One";
        f.Http.Items[2]["fields"]!["Custom.Empty"] = "one";
        var query = new Query(QueryKind.And, children: [new(QueryKind.Not, children:
            [new(QueryKind.Equal, "Custom.Empty", FieldValue.String("One"))]), new(QueryKind.All)]);
        var result = (await f.Provider.QueryAsync(f.Connection, query, null, default)).Value;
        result.Items.Single().Key.ShouldBe(f.Key(2));
        result.Complete.ShouldBeTrue();
        (await f.Provider.QueryAsync(f.Connection, new(QueryKind.Equal, "Not.Native", FieldValue.String("x")), null, default))
            .Error.Code.ShouldBe(ErrorCode.Unsupported);
        (await f.Provider.QueryAsync(f.Connection, query, "guessed", default)).Error.Code.ShouldBe(ErrorCode.InvalidInput);
        f.Http.Defect = "oversize-query";
        (await f.Provider.QueryAsync(f.Connection, new(QueryKind.All), null, default)).Error.Code.ShouldBe(ErrorCode.NotFound);
    }

    [Fact]
    public async Task ExactQueryFindsMatchesBeyondTheFirstKeysetPageWithoutDuplicatesOrMutation()
    {
        using var f = await Fixture.Create();
        f.Http.KeysetQueries = true;
        var prototype = f.Http.Items[1];
        f.Http.Items.Clear();
        for (var id = 1; id <= 205; id++)
        {
            var item = prototype.DeepClone().AsObject();
            item["id"] = id;
            item["fields"]!["Custom.Empty"] = id == 205 ? "later match" : "not selected";
            f.Http.Items.Add(id, item);
        }
        f.Http.Items.Count.ShouldBe(205);
        var query = new Query(QueryKind.Equal, "Custom.Empty", FieldValue.String("later match"));
        var first = await f.Provider.QueryAsync(f.Connection, query, null, default);
        first.IsSuccess.ShouldBeTrue(first.IsSuccess ? "" : first.Error.Message);
        first.Value.Items.ShouldBeEmpty();
        first.Value.Complete.ShouldBeFalse();
        first.Value.Continuation.ShouldNotBeNullOrWhiteSpace();
        f.Http.QueryAfterIds.ShouldBe([0]);

        var wrongQuery = await f.Provider.QueryAsync(f.Connection,
            new(QueryKind.Equal, "Custom.Empty", FieldValue.String("different query")),
            first.Value.Continuation, default);
        wrongQuery.IsSuccess.ShouldBeFalse();
        wrongQuery.Error.Code.ShouldBe(ErrorCode.InvalidInput);
        f.Http.QueryAfterIds.ShouldBe([0]);

        var second = await f.Provider.QueryAsync(f.Connection, query, first.Value.Continuation, default);
        second.IsSuccess.ShouldBeTrue(second.IsSuccess ? "" : second.Error.Message);
        second.Value.Items.Single().Key.ShouldBe(f.Key(205));
        second.Value.Items.Single().RelationshipsComplete.ShouldBeTrue();
        second.Value.Items.Single().ResourcesComplete.ShouldBeTrue();
        second.Value.Complete.ShouldBeTrue();
        second.Value.Continuation.ShouldBeNull();
        f.Http.QueryAfterIds.ShouldBe([0, 200]);
        f.Http.Writes.ShouldBe(0);
        (await f.Stored()).Journal.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public async Task QueryRejectsDuplicatedOrUnorderedNativePagesBeforeReadingItems(int first, int second)
    {
        using var f = await Fixture.Create();
        f.Http.KeysetQueries = true;
        f.Http.QueryIdsOverride = [first, second];
        var result = await f.Provider.QueryAsync(f.Connection, new(QueryKind.All), null, default);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Unavailable);
        f.Http.WorkItemReads.ShouldBe(0);
        f.Http.Writes.ShouldBe(0);
    }

    [Theory]
    [InlineData("wrong-collection")]
    [InlineData("wrong-project")]
    [InlineData("wrong-principal")]
    [InlineData("wrong-item-project")]
    public async Task ChangedNativeBindingCannotAuthorizeReadsOrWrites(string defect)
    {
        using var f = await Fixture.Create();
        f.Http.Defect = defect;
        (await f.Provider.ReadAsync(f.Connection, [f.Key(1)], default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task InitialTransitionsSuppliersPicklistsAndDisabledRulesRemainSchemaBound()
    {
        using var f = await Fixture.Create();
        f.Http.Defect = "supported-rules";
        var schema = await f.Schema();
        schema.Gaps.ShouldBeEmpty();
        schema.Types.Single().Rules.ShouldContain(r => r.Disabled && r.Action == RuleAction.Unknown);
        var seed = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Completed)), ("Custom.Closure", FieldValue.String("explicit")));
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: [new(seed, f.Http.Type, null, fields)]), default)).IsSuccess.ShouldBeTrue();
        var invalid = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(invalid, invalid.Digest)).IsSuccess.ShouldBeFalse();
        var item = (await f.Read(1)).Items.Single();
        var close = await f.Proposal(new("close-with-provider-supplier", OpKind.Batch, item.Key, item.Revision,
            Fields(("System.State", FieldValue.String(f.Http.Completed)), ("Custom.Points", FieldValue.Null))));
        (await f.Provider.ImportAsync(close, close.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(close, 0, Guid.NewGuid(), default)).Value;
        (await f.Provider.VerifyAsync(close, ack, default)).Value.Status.ShouldBe(OpStatus.Verified);
        f.Http.Items[1]["fields"]!["Custom.Closure"]!.GetValue<string>().ShouldBe("from native supplier");
        f.Http.Defect = "changed-disabled-rule";
        (await f.Schema()).Fingerprint.ShouldNotBe(schema.Fingerprint);
    }

    [Fact]
    public async Task ReceiptCommitFailureKeepsApplyingAndCreationFenceWithoutInventingMapping()
    {
        using var f = await Fixture.Create();
        var seed = ItemKey.Local(Guid.NewGuid());
        var fields = Fields(("System.State", FieldValue.String(f.Http.Initial)), ("Custom.Empty", FieldValue.String("seed")));
        (await f.Store.CommitAsync(f.Connection, 0, new(1, drafts: [new(seed, f.Http.Type, null, fields)]), default)).IsSuccess.ShouldBeTrue();
        var proposal = await f.Proposal(new("create", OpKind.Create, seed, fields: fields, type: f.Http.Type,
            concurrency: ConcurrencyMode.ObservedNonAtomic));
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Http.AfterWrite = async () =>
        {
            await using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString());
            await db.OpenAsync();
            await using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER reject_receipt BEFORE INSERT ON core_snapshots BEGIN SELECT RAISE(ABORT, 'synthetic write failure'); END;";
            await command.ExecuteNonQueryAsync();
        };
        var result = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        result.Status.ShouldBe(OpStatus.Indeterminate);
        result.Error!.Code.ShouldBe(ErrorCode.StorageUnavailable);
        var snapshot = await f.Stored();
        snapshot.Journal.Single().Status.ShouldBe(OpStatus.Applying);
        snapshot.Mappings.ShouldBeEmpty();
        snapshot.Items.ShouldBeEmpty();
        (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).IsSuccess.ShouldBeFalse();
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task ContradictoryOrForeignRelationshipEvidenceIsNotCertifiedComplete()
    {
        using var f = await Fixture.Create();
        f.Http.Items[1]["relations"]!.AsArray().Add(new JsonObject
        {
            ["rel"] = "Native.ParentForward", ["url"] = "https://example.test/organization/_apis/wit/workItems/2",
        });
        (await f.Provider.ReadAsync(f.Connection, [f.Key(1), f.Key(2)], default)).Error.Code.ShouldBe(ErrorCode.Unavailable);
        f.Http.Items[1]["relations"]![0]!["url"] = "https://another.test/organization/_apis/wit/workItems/2";
        (await f.Provider.ReadAsync(f.Connection, [f.Key(1)], default)).Error.Code.ShouldBe(ErrorCode.Unsupported);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProjectQualifiedRelationshipUrlsRequireTheBoundImmutableProject(bool sameProject)
    {
        using var f = await Fixture.Create();
        f.Http.Items[1]["relations"]!.AsArray().ShouldBeEmpty();
        f.Http.Items[2]["relations"]!.AsArray().ShouldBeEmpty();
        var project = sameProject ? Fixture.Project : "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
        foreach (var (from, to, relation) in new[]
        {
            (1, 2, "Native.ParentForward"),
            (2, 1, "Native.ParentReverse"),
        })
            f.Http.Items[from]["relations"]!.AsArray().Add(new JsonObject
            {
                ["rel"] = relation,
                ["url"] = $"https://example.test/organization/{project}/_apis/wit/workItems/{to}",
            });
        var result = await f.Provider.ReadAsync(f.Connection, [f.Key(1), f.Key(2)], default);
        if (sameProject)
        {
            result.IsSuccess.ShouldBeTrue();
            result.Value.Relationships.ShouldBe([
                new Relationship(f.Key(1), f.Key(2), "Native.ParentForward", RelationshipOrigin.Provider),
            ]);
            result.Value.Items.ShouldAllBe(item => item.RelationshipsComplete);
        }
        else
        {
            result.IsSuccess.ShouldBeFalse();
            result.Error.Code.ShouldBe(ErrorCode.Unsupported);
            (await f.Stored()).Items.ShouldBeEmpty();
        }
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task NativeDuplicateTopologyDoesNotBecomeAParentChildHierarchy()
    {
        using var f = await Fixture.Create();
        f.Http.Defect = "native-duplicate-topology";
        var result = await f.Provider.DiscoverSchemaAsync(f.Connection, default);
        result.IsSuccess.ShouldBeTrue();
        result.Value.Relations.ShouldContain(relation => relation.Reference == "System.LinkTypes.Duplicate-Forward"
            && relation.Shape == RelationShape.Association && relation.Directed);
        result.Value.Relations.Where(relation => relation.Shape == RelationShape.Hierarchy)
            .Select(relation => relation.Reference).ShouldBe(["Native.ParentForward"]);
        f.Http.Writes.ShouldBe(0);
    }

    [Fact]
    public async Task ValidButDifferentNativeActorCannotVerifyOrReuseOriginalIdentityBinding()
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        var ack = (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value;
        var originalActor = $"actor:{Fixture.Actor}";
        ack.Evidence.ShouldContain(originalActor);
        f.Access.Principal = Fixture.Process;
        f.Http.NativeActor = Fixture.Process;
        (await f.Provider.VerifyAsync(proposal, ack, default)).Value.Status.ShouldBe(OpStatus.Indeterminate);
        (await f.Stored()).Journal.Single().Receipts[0].Evidence.ShouldContain(originalActor);
        var replacement = await f.Edit();
        (await f.Provider.ImportAsync(replacement, replacement.Digest)).Error.Code.ShouldBe(ErrorCode.Conflict);
        f.Http.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task DigestSchemaAdmissionUnsupportedEffectsAndCancellationAreNotBypassed()
    {
        using var f = await Fixture.Create();
        var proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, "not-the-digest")).Error.Code.ShouldBe(ErrorCode.DigestMismatch);
        f.Http.Initial = "ChangedInitial";
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeFalse();
        f.Http.Initial = "Waiting";
        var item = (await f.Read(1)).Items.Single();
        var delete = await f.Proposal(new("delete", OpKind.Delete, item.Key, item.Revision));
        (await f.Provider.ImportAsync(delete, delete.Digest)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        var markdown = await f.Proposal(new("markdown", OpKind.Comment, item.Key, item.Revision,
            body: FieldValue.RichText("**not converted**", FieldKind.Markdown)));
        (await f.Provider.ImportAsync(markdown, markdown.Digest)).IsSuccess.ShouldBeTrue();
        (await f.Provider.ApplyAsync(markdown, 0, Guid.NewGuid(), default)).Error.Code.ShouldBe(ErrorCode.Unsupported);
        var stored = await f.Stored();
        stored.Journal.Single().Status.ShouldBe(OpStatus.Planned);
        proposal = await f.Edit();
        (await f.Provider.ImportAsync(proposal, proposal.Digest)).IsSuccess.ShouldBeTrue();
        f.Access.DenyWrite = true;
        (await f.Provider.ApplyAsync(proposal, 0, Guid.NewGuid(), default)).Value.Status.ShouldBe(OpStatus.Failed);
        f.Http.Writes.ShouldBe(0);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => f.Provider.DiscoverSchemaAsync(f.Connection, canceled.Token));
    }

    private static FieldValues Fields(params (string Name, FieldValue Value)[] fields)
        => new(fields.Select(f => KeyValuePair.Create(f.Name, f.Value)));

    private sealed class Fixture : IDisposable
    {
        internal const string Collection = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        internal const string Project = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        internal const string Actor = "cccccccc-cccc-cccc-cccc-cccccccccccc";
        internal const string Process = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        internal Connection Connection { get; } = new(new("ado"), Collection, Project, "test-actor-binding");
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"twig-ado-{Guid.NewGuid():N}.db");
        internal SqliteStore Store { get; private set; } = null!;
        internal NativeHttp Http { get; }
        internal TestAccess Access { get; private set; } = null!;
        internal AdoProvider Provider { get; private set; } = null!;
        private Fixture(string type, string initial, string completed) => Http = new(type, initial, completed);
        internal static async Task<Fixture> Create(string type = "Contoso.Initiative", string initial = "Waiting", string completed = "Delivered")
        {
            var f = new Fixture(type, initial, completed);
            f.Store = (await SqliteStore.CreateAsync(f.Path)).Value;
            f.Access = new(f.Connection, f.Store);
            f.Provider = new(f.Connection, f.Access, f.Store, f.Http);
            return f;
        }
        internal ItemKey Key(int id) => ItemKey.Remote(Connection.Provider, Collection, id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        internal async Task<ProcessSchema> Schema() => (await Provider.DiscoverSchemaAsync(Connection, default)).Value;
        internal async Task<QueryPage> Read(params int[] ids) => (await Provider.ReadAsync(Connection, ids.Select(Key).ToImmutableArray(), default)).Value;
        internal async Task<StoreSnapshot> Stored() => (await Store.ReadAsync(Connection, default)).Value;
        internal async Task<Proposal> Proposal(Op op) => new(Connection, (await Schema()).Fingerprint, (await Stored()).Version, [op]);
        internal async Task<Proposal> Edit()
        {
            var item = (await Read(1)).Items.Single();
            return await Proposal(new("edit", OpKind.Batch, item.Key, item.Revision, Fields(("Custom.Empty", FieldValue.String("edited")))));
        }
        public void Dispose()
        {
            Provider.Dispose();
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(Path + suffix);
        }
    }

    private sealed class TestAccess(Connection connection, IStore store) : AdoAccess
    {
        internal bool SawDurableFence;
        internal bool DenyWrite;
        internal string Principal = Fixture.Actor;
        internal Func<Task>? BeforeAdmission;
        public override Task<Result<AdoSession>> AcquireAsync(Connection requested, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            requested.ShouldBe(connection);
            return Task.FromResult(Result<AdoSession>.Ok(new Session(this, connection, store)));
        }
        private sealed class Session(TestAccess owner, Connection connection, IStore store) : AdoSession
        {
            public override Connection Connection => connection;
            public override Uri OrganizationUrl => new("https://example.test/organization");
            public override string PrincipalId => owner.Principal;
            public override AuthenticationHeaderValue Authorization => new("Bearer", "synthetic");
            public override async Task<Result<AdoWriteAdmission>> AdmitAsync(AdoWriteIntent intent, CancellationToken ct)
            {
                var stored = (await store.ReadAsync(connection, ct)).Value;
                var row = stored.Journal.Single(j => j.Digest == intent.Proposal.Digest && j.Operation == intent.Operation);
                row.Status.ShouldBe(OpStatus.Applying);
                row.Attempt.ShouldBe(intent.Attempt);
                owner.SawDurableFence = true;
                if (owner.BeforeAdmission is not null) await owner.BeforeAdmission();
                return owner.DenyWrite
                    ? Result<AdoWriteAdmission>.Fail(ErrorCode.Unavailable, "Native admission denied.")
                    : Result<AdoWriteAdmission>.Ok(new Admission());
            }
            public override void Dispose() { }
        }
        private sealed class Admission : AdoWriteAdmission
        {
            public override Task ObserveAsync(int statusCode, string body, CancellationToken ct) => Task.CompletedTask;
            public override void Dispose() { }
        }
    }

    private sealed class NativeHttp : HttpMessageHandler
    {
        internal const string QueryFolder = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
        internal const string FlatQuery = "ffffffff-ffff-ffff-ffff-ffffffffffff";
        internal const string TreeQuery = "11111111-1111-1111-1111-111111111111";
        internal string? SourceDefect;
        internal int BacklogMembershipReads;
        internal int PlanningMembershipReads;
        internal int[]? SourceResultIds;
        internal List<int> PlanningBatchSizes = [];
        internal string Type;
        internal string? AdditionalType;
        internal string Initial;
        internal string Completed;
        internal string NativeActor = Fixture.Actor;
        internal string? Defect;
        internal bool SawOmittedRequiredFlag;
        internal bool SawExplicitRequiredFlag;
        internal bool SawEmptyIdentityOperand;
        internal string EmptyRuleField = "Custom.Owner";
        internal string? WriteFailure;
        internal int? WriteStatus;
        internal int? MetadataStatus;
        internal bool MoveAtDispatch;
        internal int Writes;
        internal bool KeysetQueries;
        internal int[]? QueryIdsOverride;
        internal List<int> QueryAfterIds = [];
        internal int WorkItemReads;
        internal string? PlanningDefect;
        internal string? LayoutJson;
        internal int LayoutStatus = 200;
        internal bool DivergentProjectType;
        internal List<string> LayoutRequests = [];
        internal string? LastPayload;
        internal Func<Task>? AfterWrite;
        internal Dictionary<int, JsonObject> Items = [];
        private readonly Dictionary<(int, int), JsonObject> _revisions = [];
        internal NativeHttp(string type, string initial, string completed)
        {
            (Type, Initial, Completed) = (type, initial, completed);
            Items.Add(1, Item(1)); Items.Add(2, Item(2));
        }
        internal HttpMessageHandler NewHandle() => new Forward(this);
        private sealed class Forward(NativeHttp owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => owner.SendAsync(request, ct);
        }
        private JsonObject Item(int id) => new()
        {
            ["id"] = id, ["rev"] = 4,
            ["fields"] = new JsonObject
            {
                ["System.TeamProject"] = "Native Project", ["System.WorkItemType"] = Type,
                ["System.State"] = Initial, ["System.History"] = "",
                ["Custom.Closure"] = "old cached closure", ["Custom.Points"] = 2.5m,
                ["Custom.Owner"] = new JsonObject { ["id"] = Fixture.Actor, ["displayName"] = "Not identity" },
                ["Custom.Empty"] = "",
            },
            ["relations"] = new JsonArray(),
        };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            request.Headers.Authorization!.Parameter.ShouldBe("synthetic");
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            var query = request.RequestUri.Query;
            if (request.Method == HttpMethod.Patch || request.Method == HttpMethod.Post && path.Contains("/workitems/$", StringComparison.OrdinalIgnoreCase))
                return await Write(request, path, ct);
            if (path.EndsWith("/connectionData"))
                return Json($$"""{"instanceId":"{{(Defect == "wrong-collection" ? Fixture.Process : Fixture.Collection)}}","authenticatedUser":{"id":"{{(Defect == "wrong-principal" ? Fixture.Process : NativeActor)}}"} }""");
            if (path.EndsWith("/_apis/wit/workitemsbatch"))
            {
                request.Method.ShouldBe(HttpMethod.Post);
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                payload.RootElement.GetProperty("$expand").GetString().ShouldBe("relations");
                payload.RootElement.GetProperty("errorPolicy").GetString().ShouldBe("fail");
                var ids = payload.RootElement.GetProperty("ids").EnumerateArray().Select(value => value.GetInt32()).ToArray();
                ids.Length.ShouldBeInRange(1, 200);
                PlanningBatchSizes.Add(ids.Length);
                var values = new JsonArray();
                if (SourceDefect != "incomplete-batch")
                    foreach (var id in ids) values.Add(Items.TryGetValue(id, out var item) ? item.DeepClone() : Item(id));
                return Json(new JsonObject { ["count"] = values.Count, ["value"] = values }.ToJsonString());
            }
            if (path.EndsWith("/_apis/wit/queries"))
                return SourceDefect == "sources-denied" ? Status(403)
                    : Json($$"""{"count":1,"value":[{"id":"{{QueryFolder}}","name":"Shared Queries","isFolder":true,"hasChildren":true}]}""");
            if (path.Contains("/_apis/wit/queries/"))
            {
                var children = new JsonArray();
                if (SourceDefect != "truncated-folder")
                {
                    children.Add(new JsonObject { ["id"] = FlatQuery, ["name"] = "Flat query", ["isFolder"] = false,
                        ["queryType"] = "flat", ["wiql"] = "SELECT [System.Id] FROM WorkItems" });
                    children.Add(new JsonObject { ["id"] = TreeQuery, ["name"] = "Tree query", ["isFolder"] = false,
                        ["queryType"] = "tree", ["wiql"] = "SELECT [System.Id] FROM WorkItemLinks" });
                }
                return Json(new JsonObject { ["id"] = SourceDefect == "changed-folder" ? Fixture.Process : QueryFolder,
                    ["name"] = "Shared Queries", ["isFolder"] = true, ["hasChildren"] = true, ["children"] = children }.ToJsonString());
            }
            if (path.EndsWith("/_apis/work/backlogs"))
                return Json("""{"count":1,"value":[{"id":"Native.Requirements","name":"Native backlog","workItemTypes":[{"name":"Not a membership filter"}]}]}""");
            if (path.Contains("/_apis/work/backlogs/") && path.EndsWith("/workItems"))
            {
                BacklogMembershipReads++;
                return Json("""{"workItems":[{"rel":null,"source":null,"target":{"id":1}},{"rel":"Native.Link","source":{"id":1},"target":{"id":2}}]}""");
            }
            if (path.Contains("/_apis/wit/wiql/"))
            {
                PlanningMembershipReads++;
                if (SourceDefect == "query-denied") return Status(403);
                if (SourceDefect == "unknown-query-shape") return Json("""{"queryType":"unknown","asOf":"2026-11-03T22:30:00Z"}""");
                if (SourceDefect == "oversized-membership")
                    return Json(new JsonObject { ["queryType"] = "flat", ["asOf"] = "2026-11-03T22:30:00Z",
                        ["workItems"] = new JsonArray(Enumerable.Range(1, 10001).Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()) }.ToJsonString());
                return path.EndsWith(TreeQuery)
                    ? Json("""{"queryType":"tree","asOf":"2026-11-03T22:30:00Z","workItemRelations":[{"source":null,"target":{"id":1}},{"source":{"id":1},"target":{"id":2}}]}""")
                    : Json(new JsonObject { ["queryType"] = "flat", ["asOf"] = "2026-11-03T22:30:00Z",
                        ["workItems"] = new JsonArray((SourceResultIds ?? [2]).Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()) }.ToJsonString());
            }
            if (path.Contains("/classificationnodes/"))
            {
                if (PlanningDefect == "denied") return Status(403);
                var area = path.EndsWith("/areas");
                var structure = area ? "Area" : "Iteration";
                var child = area ? "Area" : "Sprint";
                var children = new JsonArray();
                if (PlanningDefect != "truncated")
                {
                    children.Add(new JsonObject { ["path"] = $"\\Native Project\\{structure}\\{child}", ["hasChildren"] = false });
                    children.Add(new JsonObject { ["path"] = $"\\Native Project\\{structure}\\Other team's sprint", ["hasChildren"] = false });
                }
                return Json(new JsonObject { ["path"] = $"\\Native Project\\{structure}",
                    ["hasChildren"] = true, ["children"] = children }.ToJsonString());
            }
            if (path.Contains("/teams/"))
                return Json($$"""{"id":"{{Fixture.Process}}","name":"{{(PlanningDefect == "foreign-team" ? "Different team" : "Selected team")}}"}""");
            if (path.EndsWith("/teamsettings/iterations"))
                return Json("""{"count":1,"value":[{"path":"Native Project\\Sprint","attributes":{"startDate":"2026-11-01T00:00:00Z","finishDate":"2026-11-07T23:59:59Z"}}]}""");
            if (path.Contains("/_apis/projects/"))
                return Json($$"""{"id":"{{(Defect == "wrong-project" ? Fixture.Process : Fixture.Project)}}","name":"Native Project","capabilities":{"processTemplate":{"templateTypeId":"{{Fixture.Process}}"} } }""");
            if (path.Contains("/workitemicons/"))
            {
                request.Headers.Accept.Single().MediaType.ShouldBe("image/svg+xml");
                query.ShouldContain("color=B4009E");
                return new(HttpStatusCode.OK) { Content = new StringContent(
                    """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 16 16"><path fill="#B4009E" d="M1 1h14v14H1z"/></svg>""",
                    Encoding.UTF8, "image/svg+xml") };
            }
            if (path.EndsWith("/layout"))
            {
                LayoutRequests.Add(path);
                return LayoutStatus == 200 ? Json(LayoutJson ?? """{"pages":[]}""")
                    : LayoutStatus == 400
                        ? new(HttpStatusCode.BadRequest) { Content = new StringContent("""{"typeKey":"ProcessWorkItemTypeIsLockedException","message":"VS403115: locked"}""") }
                        : Status(LayoutStatus);
            }
            if (path.EndsWith("/wiql"))
            {
                if (KeysetQueries)
                {
                    using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    var text = document.RootElement.GetProperty("query").GetString()!;
                    var match = System.Text.RegularExpressions.Regex.Match(text, @"\[System\.Id\]\s*>\s*(\d+)");
                    var after = match.Success
                        ? int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
                    QueryAfterIds.Add(after);
                    var ids = QueryIdsOverride ?? Items.Keys.Where(id => id > after).Order().Take(201).ToArray();
                    return Json(new JsonObject { ["queryType"] = "flat",
                        ["workItems"] = new JsonArray(ids.Select(id => (JsonNode)new JsonObject { ["id"] = id }).ToArray()) }.ToJsonString());
                }
                var count = Defect == "oversize-query" ? 201 : Items.Count;
                return Json(new JsonObject { ["queryType"] = "flat",
                    ["workItems"] = new JsonArray(Enumerable.Range(1, count).Select(i => (JsonNode)new JsonObject { ["id"] = i }).ToArray()) }.ToJsonString());
            }
            if (path.Contains("/workitems/", StringComparison.OrdinalIgnoreCase))
            {
                WorkItemReads++;
                var tail = path[(path.IndexOf("/workitems/", StringComparison.OrdinalIgnoreCase) + 11)..].Split('/');
                var id = int.Parse(tail[0], System.Globalization.CultureInfo.InvariantCulture);
                if (tail.Length > 1)
                {
                    if (Defect == "readback-denied") return Status(403);
                    if (Defect == "readback-canceled") throw new OperationCanceledException("Synthetic readback cancellation");
                    var revision = int.Parse(tail[2], System.Globalization.CultureInfo.InvariantCulture);
                    var root = _revisions[(id, revision)].DeepClone();
                    if (Defect == "readback-mismatch") root["fields"]!["Custom.Empty"] = "not edited";
                    if (Defect == "resource-readback-mismatch") root["relations"] = new JsonArray();
                    if (Defect == "creation-links-readback-mismatch") root["relations"]!.AsArray().RemoveAt(1);
                    return Json(root.ToJsonString());
                }
                if (!Items.TryGetValue(id, out var item)) return Status(404);
                var read = item.DeepClone();
                if (Defect == "wrong-item-project") read["fields"]!["System.TeamProject"] = "Another Project";
                return Json(read.ToJsonString());
            }
            if (MetadataStatus is { } status) return Status(status);
            if (Defect == "duplicate-json") return Json("""{"count":0,"value":[],"value":[]}""");
            HttpResponseMessage response;
            if (path.EndsWith("/workitemrelationtypes"))
            {
                response = Rows($$"""
                    {"referenceName":"Native.ParentForward","attributes":{"usage":"workItemLink","enabled":true,"directional":true,"isForward":true,"topology":"tree","oppositeEndReferenceName":"Native.ParentReverse"} },
                    {"referenceName":"Native.ParentReverse","attributes":{"usage":"workItemLink","enabled":true,"directional":true,"isForward":false,"topology":"tree","oppositeEndReferenceName":"Native.ParentForward"} },
                    {"referenceName":"AttachedFile","attributes":{"usage":"resourceLink"} },
                    {"referenceName":"Hyperlink","attributes":{"usage":"resourceLink","enabled":{{(Defect != "resource-disabled" ? "true" : "false")}} } },
                    {"referenceName":"ArtifactLink","attributes":{"usage":"resourceLink","enabled":{{(Defect != "resource-disabled" ? "true" : "false")}} } }
                    """);
                if (Defect == "native-duplicate-topology")
                {
                    var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
                    var values = body["value"]!.AsArray();
                    values.Add(JsonNode.Parse("""
                        {"referenceName":"System.LinkTypes.Duplicate-Forward","attributes":{"usage":"workItemLink","enabled":true,"directional":true,"isForward":true,"topology":"tree","oppositeEndReferenceName":"System.LinkTypes.Duplicate-Reverse"}}
                        """));
                    values.Add(JsonNode.Parse("""
                        {"referenceName":"System.LinkTypes.Duplicate-Reverse","attributes":{"usage":"workItemLink","enabled":true,"directional":true,"isForward":false,"topology":"tree","oppositeEndReferenceName":"System.LinkTypes.Duplicate-Forward"}}
                        """));
                    body["count"] = values.Count;
                    response.Dispose();
                    response = Json(body.ToJsonString());
                }
                if (Defect?.StartsWith("creation-links", StringComparison.Ordinal) == true)
                {
                    var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
                    var values = body["value"]!.AsArray();
                    values.Add(JsonNode.Parse("""
                        {"referenceName":"Native.Association","attributes":{"usage":"workItemLink","enabled":true,"directional":false,"topology":"network"}}
                        """));
                    values.Add(JsonNode.Parse("""
                        {"referenceName":"Native.DependencyForward","attributes":{"usage":"workItemLink","enabled":true,"directional":true,"isForward":true,"topology":"dependency","oppositeEndReferenceName":"Native.DependencyReverse"}}
                        """));
                    values.Add(JsonNode.Parse("""
                        {"referenceName":"Native.DependencyReverse","attributes":{"usage":"workItemLink","enabled":true,"directional":true,"isForward":false,"topology":"dependency","oppositeEndReferenceName":"Native.DependencyForward"}}
                        """));
                    body["count"] = values.Count;
                    response.Dispose();
                    response = Json(body.ToJsonString());
                }
            }
            else if (path.EndsWith("/workitemtypecategories")) response = Rows("");
            else if (path.EndsWith("/workitemtypes", StringComparison.OrdinalIgnoreCase) && query.Contains("$expand=all"))
                response = Defect == "missing-transitions" ? Rows("") : Rows(string.Join(",",
                    NativeTypes().Concat(DivergentProjectType ? ["System.Parent"] : []).Select(reference => $$"""
                    {"referenceName":"{{reference}}","transitions":{"":[{"to":"{{Initial}}"}],"{{Initial}}":[{"to":"{{Completed}}"}],"{{Completed}}":[{"to":"{{Initial}}"}]} }
                    """)));
            else if (path.EndsWith("/workItemTypes"))
                response = Rows(string.Join(",", NativeTypes().Select(reference =>
                    $$"""{"referenceName":"{{reference}}","name":"{{reference}}","isDisabled":false,"color":"B4009E","icon":{"id":"icon_crown"} }""")));
            else if (path.EndsWith("/states"))
                response = Rows($$"""{"name":"{{Initial}}","stateCategory":"Proposed"},{"name":"{{Completed}}","stateCategory":"{{(Defect == "unknown-category" ? "Alien" : "Completed")}}"}""");
            else if (path.EndsWith("/rules"))
            {
                if (Defect == "missing-rules") return Status(403);
                var required = $$"""
                    {"isDisabled":false,"conditions":[{"conditionType":"when","field":"System.State","value":"{{Completed}}"}],"actions":[{"actionType":"{{(Defect == "unknown-rule" ? "unsupported" : "makeRequired")}}","targetField":"Custom.Closure"}]}
                    """;
                if (Defect == "empty-typed-rule")
                {
                    required += $$"""
                        ,{"isDisabled":false,"conditions":[{"conditionType":"when","field":"{{EmptyRuleField}}","value":""}],"actions":[{"actionType":"makeRequired","targetField":"Custom.Empty"}]},
                        {"isDisabled":false,"conditions":[],"actions":[{"actionType":"copyValue","targetField":"{{EmptyRuleField}}","value":""}]}
                        """;
                    SawEmptyIdentityOperand = true;
                }
                if (Defect is "supported-rules" or "changed-disabled-rule")
                    required += $$"""
                        ,{"isDisabled":false,"conditions":[{"conditionType":"$when","field":"System.State","value":"{{Completed}}"}],"actions":[{"actionType":"$copyValue","targetField":"Custom.Closure","value":"from native supplier"}]},
                        {"isDisabled":true,"conditions":[{"unknown":"retained"}],"actions":[{"unknown":"{{Defect}}"}]}
                        """;
                response = Rows(required);
            }
            else if (path.Contains("/processes/lists/"))
                response = Json($$"""{"id":"{{Fixture.Process}}","isSuggested":false,"items":["1.5","2.5","3.5"]}""");
            else if (path.EndsWith("/fields"))
            {
                var names = new[] { "System.State", "System.History", "Custom.Closure", "Custom.Points", "Custom.Owner", "Custom.Empty", "Custom.Unset" }
                    .Concat(Defect == "empty-typed-rule" ? ["Custom.Quantity", "Custom.Toggle", "Custom.Date"] : [])
                    .Concat(Defect == "description-html" ? ["System.Description"] : []);
                var global = !path.Contains("/work/processes/");
                response = Rows(string.Join(",", names.Select(name =>
                {
                    var field = new JsonObject { ["referenceName"] = name, ["type"] = name == "Custom.Points" ? "double" : "string" };
                    if (name == "System.Description") field["type"] = "html";
                    if (name == "Custom.Quantity") field["type"] = "integer";
                    if (name == "Custom.Toggle") field["type"] = "boolean";
                    if (name == "Custom.Date") field["type"] = "dateTime";
                    if (Defect == "unknown-field-kind" && name == "Custom.Empty") field["type"] = "unrecognized";
                    if (global)
                    {
                        field["isIdentity"] = name == "Custom.Owner"; field["readOnly"] = false; field["isPicklist"] = false;
                        if (Defect == "supported-rules" && name == "Custom.Points")
                        {
                            field["isPicklist"] = true; field["isPicklistSuggested"] = false; field["picklistId"] = Fixture.Process;
                        }
                    }
                    else
                    {
                        if (Defect == "omitted-optional-required")
                        {
                            if (name == "Custom.Unset") field["required"] = true;
                            SawOmittedRequiredFlag |= !field.ContainsKey("required");
                            SawExplicitRequiredFlag |= field["required"]?.GetValue<bool>() == true;
                        }
                        else if (Defect == "malformed-required") field["required"] = "not-a-boolean";
                        else field["required"] = false;
                        if (Defect == "default-value" && name == "Custom.Empty") field["defaultValue"] = "server default";
                    }
                    return field.ToJsonString();
                })));
            }
            else throw new InvalidOperationException($"Unexpected native route {request.Method} {path}{query}");
            if (Defect == "metadata-page") response.Headers.Add("x-ms-continuationtoken", "more");
            return response;
        }
        private IEnumerable<string> NativeTypes() => AdditionalType is null ? [Type] : [Type, AdditionalType];
        private async Task<HttpResponseMessage> Write(HttpRequestMessage request, string path, CancellationToken ct)
        {
            Writes++;
            LastPayload = await request.Content!.ReadAsStringAsync(ct);
            if (WriteFailure == "network") throw new HttpRequestException("Synthetic connection loss after possible dispatch");
            if (WriteFailure == "canceled") throw new OperationCanceledException("Synthetic dispatch cancellation");
            if (WriteFailure == "malformed") return Json("{broken");
            if (WriteStatus is { } status) return Status(status);
            var create = request.Method == HttpMethod.Post;
            var id = create ? Items.Keys.Max() + 1 : int.Parse(path.Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture);
            var item = create ? Item(id) : Items[id];
            if (MoveAtDispatch) item["rev"] = item["rev"]!.GetValue<int>() + 1;
            using var patch = JsonDocument.Parse(LastPayload);
            foreach (var entry in patch.RootElement.EnumerateArray())
            {
                var op = entry.GetProperty("op").GetString();
                var field = entry.GetProperty("path").GetString()!;
                if (op == "test")
                {
                    if (entry.GetProperty("value").GetInt32() != item["rev"]!.GetValue<int>()) return Status(412);
                }
                else if (field.StartsWith("/fields/"))
                    item["fields"]![field[8..]] = JsonNode.Parse(entry.GetProperty("value").GetRawText());
                else if (field == "/relations/-")
                {
                    var relation = JsonNode.Parse(entry.GetProperty("value").GetRawText())!;
                    item["relations"]!.AsArray().Add(relation.DeepClone());
                    if (relation["rel"]!.GetValue<string>() == "Native.ParentForward")
                    {
                        var other = Items[2];
                        other["relations"]!.AsArray().Add(new JsonObject { ["rel"] = "Native.ParentReverse",
                            ["url"] = $"https://example.test/organization/_apis/wit/workItems/{id}" });
                    }
                    else if (relation["rel"]!.GetValue<string>() == "Native.ParentReverse")
                    {
                        var parentId = int.Parse(relation["url"]!.GetValue<string>().Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture);
                        Items[parentId]["relations"]!.AsArray().Add(new JsonObject { ["rel"] = "Native.ParentForward",
                            ["url"] = $"https://example.test/organization/_apis/wit/workItems/{id}" });
                    }
                    else if (relation["rel"]!.GetValue<string>() is "Native.Association" or "Native.DependencyForward" or "Native.DependencyReverse")
                    {
                        var endpoint = int.Parse(relation["url"]!.GetValue<string>().Split('/')[^1], System.Globalization.CultureInfo.InvariantCulture);
                        var inverse = relation["rel"]!.GetValue<string>() switch
                        {
                            "Native.DependencyForward" => "Native.DependencyReverse",
                            "Native.DependencyReverse" => "Native.DependencyForward",
                            _ => "Native.Association",
                        };
                        Items[endpoint]["relations"]!.AsArray().Add(new JsonObject { ["rel"] = inverse,
                            ["url"] = $"https://example.test/organization/_apis/wit/workItems/{id}" });
                    }
                }
                else if (op == "remove" && field.StartsWith("/relations/"))
                {
                    item["relations"]!.AsArray().RemoveAt(int.Parse(field[11..], System.Globalization.CultureInfo.InvariantCulture));
                    Items[2]["relations"] = new JsonArray();
                }
                else throw new InvalidOperationException("Unexpected patch operation");
            }
            item["rev"] = item["rev"]!.GetValue<int>() + 1;
            if (Defect == "supported-rules" && item["fields"]!["System.State"]!.GetValue<string>() == Completed)
                item["fields"]!["Custom.Closure"] = "from native supplier";
            Items[id] = item;
            _revisions[(id, item["rev"]!.GetValue<int>())] = item.DeepClone().AsObject();
            if (WriteFailure == "lost-create-response") throw new HttpRequestException("Native create applied but response lost");
            if (AfterWrite is not null) await AfterWrite();
            return Json(item.ToJsonString());
        }
        private static HttpResponseMessage Rows(string rows) => Json($$"""{"value":[{{rows}}]}""");
        private static HttpResponseMessage Status(int status) => new((HttpStatusCode)status)
        { Content = new StringContent("""{"message":"Synthetic native error"}""", Encoding.UTF8, "application/json") };
        private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
