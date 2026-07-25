using System.Text.Json;
using Shouldly;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Dtos;
using Twig.Infrastructure.Serialization;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Contract tests for <see cref="WorkItemHistoryProjector"/> (twig#241).
/// The projection is a pure function over parsed DTOs, so each contract rule
/// (bookkeeping suppression, changedAt normalization, both-null suppression,
/// null-vs-absent handling, relation extraction, field filtering) is exercised directly
/// rather than through a full fake HTTP response.
/// </summary>
public class WorkItemHistoryProjectorTests
{
    // ── changedAt normalization ─────────────────────────────────────

    [Fact]
    public void ChangedAt_PrefersSystemChangedDate_OverRevisedDate()
    {
        var update = Parse("""
        {
          "id": 5, "rev": 5,
          "revisedDate": "2020-01-01T00:00:00Z",
          "fields": { "System.ChangedDate": { "newValue": "2026-07-25T02:45:38.09Z" } }
        }
        """);

        var evt = ProjectOne(update);

        evt.ChangedAt.ShouldNotBeNull();
        evt.ChangedAt!.Value.ToUniversalTime().ShouldBe(
            DateTimeOffset.Parse("2026-07-25T02:45:38.09Z").ToUniversalTime());
    }

    [Fact]
    public void ChangedAt_RelationOnlyRecord_FallsBackToRevisedDate()
    {
        // Relation-only update records carry no `fields` at all and therefore no
        // System.ChangedDate — and those are precisely the reparenting events.
        var update = Parse("""
        {
          "id": 2, "rev": 1,
          "revisedDate": "2026-07-25T02:45:11.053Z",
          "relations": { "added": [] }
        }
        """);

        var evt = ProjectOne(update);

        evt.ChangedAt.ShouldNotBeNull();
        evt.ChangedAt!.Value.ToUniversalTime().ShouldBe(
            DateTimeOffset.Parse("2026-07-25T02:45:11.053Z").ToUniversalTime());
    }

    [Fact]
    public void ChangedAt_SentinelRevisedDate_IsNeverEmitted()
    {
        // ADO's top-level revisedDate carries 9999-01-01T00:00:00Z on the current revision.
        var update = Parse("""
        { "id": 1, "rev": 1, "revisedDate": "9999-01-01T00:00:00Z" }
        """);

        ProjectOne(update).ChangedAt.ShouldBeNull();
    }

    [Fact]
    public void ChangedAt_SentinelRevisedDate_StillUsesChangedDateWhenPresent()
    {
        var update = Parse("""
        {
          "id": 1, "rev": 1,
          "revisedDate": "9999-01-01T00:00:00Z",
          "fields": { "System.ChangedDate": { "newValue": "2026-07-25T02:45:09.883Z" } }
        }
        """);

        ProjectOne(update).ChangedAt!.Value.Year.ShouldBe(2026);
    }

    // ── Ordering and identity ───────────────────────────────────────

    [Fact]
    public void Events_AreOrderedByUpdateId()
    {
        var updates = new[] { Parse("""{"id":3,"rev":1}"""), Parse("""{"id":1,"rev":1}"""), Parse("""{"id":2,"rev":1}""") };

        var history = WorkItemHistoryProjector.Project(42, updates, WorkItemHistoryOptions.Brief);

        history.Events.Select(e => e.UpdateId).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void MultipleUpdateIds_MayShareOneRevision()
    {
        // Verified on real items: relation changes emit their own update records
        // without bumping the revision.
        var updates = new[]
        {
            Parse("""{"id":1,"rev":1}"""),
            Parse("""{"id":2,"rev":1,"relations":{"added":[{"rel":"System.LinkTypes.Hierarchy-Forward","url":"https://dev.azure.com/o/p/_apis/wit/workItems/99"}]}}"""),
            Parse("""{"id":3,"rev":1,"relations":{"removed":[{"rel":"System.LinkTypes.Hierarchy-Forward","url":"https://dev.azure.com/o/p/_apis/wit/workItems/99"}]}}"""),
        };

        var history = WorkItemHistoryProjector.Project(42, updates, WorkItemHistoryOptions.Brief);

        history.Events.Count.ShouldBe(3);
        history.Events.Select(e => e.Revision).ShouldAllBe(r => r == 1);
        history.Events.Select(e => e.UpdateId).ShouldBe([1, 2, 3]);
    }

    // ── Field projection ────────────────────────────────────────────

    [Fact]
    public void BothNullFieldEntry_IsSuppressed()
    {
        // ADO emits field entries on creation for fields that were never set —
        // observed as System.AssignedTo: {} on a freshly created item.
        var update = Parse("""
        {
          "id": 1, "rev": 1, "revisedDate": "2026-01-01T00:00:00Z",
          "fields": { "System.AssignedTo": {}, "System.State": { "newValue": "To Do" } }
        }
        """);

        var evt = ProjectOne(update, new WorkItemHistoryOptions(DetailAll: true));

        evt.Fields.Select(f => f.ReferenceName).ShouldNotContain("System.AssignedTo");
        evt.ChangedFields.ShouldNotContain("System.AssignedTo");
        evt.ChangedFields.ShouldContain("System.State");
    }

    [Fact]
    public void InitialValue_PreservesNullOldSide()
    {
        var update = Parse("""
        { "id": 1, "rev": 1, "fields": { "System.State": { "newValue": "To Do" } } }
        """);

        var change = ProjectOne(update, new WorkItemHistoryOptions(DetailAll: true))
            .Fields.Single(f => f.ReferenceName == "System.State");

        change.OldValue.ShouldBeNull();
        change.NewValue.ShouldBe("To Do");
    }

    [Fact]
    public void ClearedField_PreservesNullNewSide()
    {
        var update = Parse("""
        { "id": 2, "rev": 2, "fields": { "System.AssignedTo": { "oldValue": "Daniel Green" } } }
        """);

        var change = ProjectOne(update, new WorkItemHistoryOptions(DetailAll: true))
            .Fields.Single(f => f.ReferenceName == "System.AssignedTo");

        change.OldValue.ShouldBe("Daniel Green");
        change.NewValue.ShouldBeNull();
    }

    [Fact]
    public void IdentityValuedField_ProjectsDisplayName()
    {
        var update = Parse("""
        {
          "id": 2, "rev": 2,
          "fields": {
            "System.AssignedTo": {
              "newValue": { "displayName": "Daniel Green", "uniqueName": "dangreen@microsoft.com" }
            }
          }
        }
        """);

        ProjectOne(update, new WorkItemHistoryOptions(DetailAll: true))
            .Fields.Single().NewValue.ShouldBe("Daniel Green");
    }

    [Theory]
    [InlineData("System.Rev")]
    [InlineData("System.AuthorizedDate")]
    [InlineData("System.RevisedDate")]
    [InlineData("System.ChangedDate")]
    [InlineData("System.Watermark")]
    public void BookkeepingFields_AreSuppressedFromBriefChangedList(string referenceName)
    {
        var update = Parse($$"""
        {
          "id": 2, "rev": 2,
          "fields": {
            "{{referenceName}}": { "oldValue": "1", "newValue": "2" },
            "System.State": { "oldValue": "To Do", "newValue": "Doing" }
          }
        }
        """);

        var evt = ProjectOne(update);

        evt.ChangedFields.ShouldNotContain(referenceName);
        evt.ChangedFields.ShouldContain("System.State");
    }

    [Fact]
    public void BookkeepingField_IsRetainedWhenNamedExplicitlyByFilter()
    {
        var update = Parse("""
        { "id": 2, "rev": 2, "fields": { "System.Rev": { "oldValue": 1, "newValue": 2 } } }
        """);

        var options = new WorkItemHistoryOptions(
            Fields: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System.Rev" });

        ProjectOne(update, options).ChangedFields.ShouldContain("System.Rev");
    }

    // ── Brief vs detail ─────────────────────────────────────────────

    [Fact]
    public void BriefEvent_OmitsFieldValues_ButKeepsChangedList()
    {
        var update = Parse("""
        { "id": 2, "rev": 2, "fields": { "System.State": { "oldValue": "To Do", "newValue": "Doing" } } }
        """);

        var evt = ProjectOne(update);

        evt.Detailed.ShouldBeFalse();
        evt.Fields.ShouldBeEmpty();
        evt.ChangedFields.ShouldBe(["System.State"]);
    }

    [Fact]
    public void DetailByUpdateId_AppliesOnlyToNamedEvents()
    {
        var updates = new[]
        {
            Parse("""{"id":1,"rev":1,"fields":{"System.State":{"newValue":"To Do"}}}"""),
            Parse("""{"id":2,"rev":2,"fields":{"System.State":{"oldValue":"To Do","newValue":"Doing"}}}"""),
        };

        var options = new WorkItemHistoryOptions(DetailUpdateIds: new HashSet<int> { 2 });
        var history = WorkItemHistoryProjector.Project(42, updates, options);

        history.Events.Single(e => e.UpdateId == 1).Detailed.ShouldBeFalse();
        history.Events.Single(e => e.UpdateId == 1).Fields.ShouldBeEmpty();
        history.Events.Single(e => e.UpdateId == 2).Detailed.ShouldBeTrue();
        history.Events.Single(e => e.UpdateId == 2).Fields.Count.ShouldBe(1);
    }

    // ── Relation extraction ─────────────────────────────────────────

    [Fact]
    public void RelationAddAndRemove_AreBothExtracted()
    {
        var update = Parse("""
        {
          "id": 2, "rev": 1,
          "relations": {
            "added": [{ "rel": "System.LinkTypes.Hierarchy-Forward", "url": "https://dev.azure.com/o/p/_apis/wit/workItems/3319" }],
            "removed": [{ "rel": "System.LinkTypes.Hierarchy-Reverse", "url": "https://dev.azure.com/o/p/_apis/wit/workItems/3300" }]
          }
        }
        """);

        var relations = ProjectOne(update).Relations;

        relations.Count.ShouldBe(2);
        var added = relations.Single(r => r.Kind == RelationChangeKind.Added);
        added.TargetId.ShouldBe(3319);
        // The raw ADO relation type is preserved verbatim.
        added.RelationType.ShouldBe("System.LinkTypes.Hierarchy-Forward");
        relations.Single(r => r.Kind == RelationChangeKind.Removed).TargetId.ShouldBe(3300);
    }

    [Theory]
    // Real production shape: no numeric tail, not an HTTP URL. A naive parser throws on this.
    [InlineData("vstfs:///Git/Commit/7b282744-ea82-42ad-b76d-5f795cffb133%2f7b282744%2fabc123")]
    [InlineData("https://github.com/PolyphonyRequiem/twig")]
    [InlineData("https://dev.azure.com/o/p/_apis/wit/attachments/abc-def")]
    [InlineData("")]
    [InlineData(null)]
    public void NonWorkItemRelations_AreSkippedWithoutFailing(string? url)
    {
        WorkItemHistoryProjector.TryExtractWorkItemId(url, out _).ShouldBeFalse();
    }

    [Fact]
    public void ArtifactLinkRelation_DoesNotProduceARelationEvent()
    {
        var update = Parse("""
        {
          "id": 4, "rev": 2,
          "relations": {
            "added": [
              { "rel": "ArtifactLink", "url": "vstfs:///Git/Commit/aaa%2fbbb%2fccc" },
              { "rel": "System.LinkTypes.Hierarchy-Forward", "url": "https://dev.azure.com/o/p/_apis/wit/workItems/7" }
            ]
          }
        }
        """);

        var relations = ProjectOne(update).Relations;

        relations.Count.ShouldBe(1);
        relations[0].TargetId.ShouldBe(7);
    }

    // ── Relation target enrichment ──────────────────────────────────

    [Fact]
    public void UnresolvableTarget_IsReportedAsDeleted_NotNullTitle()
    {
        var update = Parse("""
        {
          "id": 2, "rev": 1,
          "relations": { "added": [{ "rel": "System.LinkTypes.Hierarchy-Forward", "url": "https://dev.azure.com/o/p/_apis/wit/workItems/3323" }] }
        }
        """);

        var history = WorkItemHistoryProjector.Project(
            42, [update], WorkItemHistoryOptions.Brief,
            enrichment: new Dictionary<int, WorkItemRelationTarget>());

        var target = history.Events.Single().Relations.Single().Target;
        target.ShouldNotBeNull();
        target!.Deleted.ShouldBeTrue();
        target.Id.ShouldBe(3323);
    }

    [Fact]
    public void ResolvedTarget_CarriesTitleTypeAndState()
    {
        var update = Parse("""
        {
          "id": 2, "rev": 1,
          "relations": { "added": [{ "rel": "System.LinkTypes.Hierarchy-Forward", "url": "https://dev.azure.com/o/p/_apis/wit/workItems/3319" }] }
        }
        """);

        var enrichment = new Dictionary<int, WorkItemRelationTarget>
        {
            [3319] = new(3319, "Child item", "Task", "Doing", Deleted: false),
        };

        var target = WorkItemHistoryProjector
            .Project(42, [update], WorkItemHistoryOptions.Brief, enrichment)
            .Events.Single().Relations.Single().Target;

        target!.Deleted.ShouldBeFalse();
        target.Title.ShouldBe("Child item");
        target.Type.ShouldBe("Task");
        target.State.ShouldBe("Doing");
    }

    [Fact]
    public void CollectRelationTargetIds_DeduplicatesAndSkipsNonWorkItems()
    {
        var updates = new[]
        {
            Parse("""{"id":1,"rev":1,"relations":{"added":[{"rel":"x","url":"https://dev.azure.com/o/p/_apis/wit/workItems/5"}]}}"""),
            Parse("""{"id":2,"rev":1,"relations":{"removed":[{"rel":"x","url":"https://dev.azure.com/o/p/_apis/wit/workItems/5"}]}}"""),
            Parse("""{"id":3,"rev":1,"relations":{"added":[{"rel":"ArtifactLink","url":"vstfs:///Git/Commit/a%2fb%2fc"}]}}"""),
            Parse("""{"id":4,"rev":1,"relations":{"added":[{"rel":"x","url":"https://dev.azure.com/o/p/_apis/wit/workItems/9"}]}}"""),
        };

        WorkItemHistoryProjector.CollectRelationTargetIds(updates).ShouldBe([5, 9]);
    }

    // ── Field filtering ─────────────────────────────────────────────

    [Fact]
    public void FieldFilter_RemovesUnrelatedDeltas()
    {
        var update = Parse("""
        {
          "id": 2, "rev": 2,
          "fields": {
            "System.State": { "oldValue": "To Do", "newValue": "Doing" },
            "System.Title": { "oldValue": "a", "newValue": "b" }
          }
        }
        """);

        var options = new WorkItemHistoryOptions(
            Fields: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System.State" });

        ProjectOne(update, options).ChangedFields.ShouldBe(["System.State"]);
    }

    [Fact]
    public void FieldFilter_DoesNotHideRelationEvents()
    {
        // Filtering for State must not silently hide a reparent.
        var updates = new[]
        {
            Parse("""{"id":1,"rev":1,"fields":{"System.Title":{"oldValue":"a","newValue":"b"}}}"""),
            Parse("""{"id":2,"rev":1,"relations":{"added":[{"rel":"System.LinkTypes.Hierarchy-Reverse","url":"https://dev.azure.com/o/p/_apis/wit/workItems/77"}]}}"""),
        };

        var options = new WorkItemHistoryOptions(
            Fields: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "System.State" });

        var history = WorkItemHistoryProjector.Project(42, updates, options);

        // Update 1 has neither a matching field delta nor a relation event ⇒ dropped.
        // Update 2 has a relation event ⇒ retained despite the filter.
        history.Events.Select(e => e.UpdateId).ShouldBe([2]);
        history.Events.Single().Relations.Single().TargetId.ShouldBe(77);
    }

    // ── Fixtures captured from real ADO ─────────────────────────────

    [Fact]
    public void ProductionCapture_WithArtifactLinkAndRelationOnlyRecords_ProjectsWithoutThrowing()
    {
        var updates = LoadFixture("updates-3316.json");

        var history = WorkItemHistoryProjector.Project(3316, updates, WorkItemHistoryOptions.Brief);

        // updateIds 2, 3 and 4 all sit on revisions 1/1/2 — several update IDs, few revisions.
        history.Complete.ShouldBeTrue();
        history.Events.Select(e => e.UpdateId).ShouldBe([1, 2, 3, 4]);

        // The ArtifactLink (vstfs://) relation on update 4 is skipped, not thrown on.
        history.Events.Single(e => e.UpdateId == 4).Relations.ShouldBeEmpty();

        // Relation-only records (no `fields`) still get a real changedAt via revisedDate.
        var reparent = history.Events.Single(e => e.UpdateId == 2);
        reparent.ChangedAt.ShouldNotBeNull();
        reparent.ChangedAt!.Value.Year.ShouldNotBe(9999);
        reparent.Relations.Single().Kind.ShouldBe(RelationChangeKind.Added);

        // The creation record carries the 9999 sentinel as its revisedDate, but also a real
        // System.ChangedDate — the sentinel must be replaced, never emitted.
        var creation = history.Events.Single(e => e.UpdateId == 1);
        creation.ChangedAt.ShouldNotBeNull();
        creation.ChangedAt!.Value.Year.ShouldNotBe(9999);
    }

    [Fact]
    public void OrphanFixture_RelationAddAndRemoveAgainstDeletedTarget()
    {
        var updates = LoadFixture("updates-orphan.json");

        var history = WorkItemHistoryProjector.Project(
            3324, updates, WorkItemHistoryOptions.Brief,
            // The target was deleted after linking, so the batch enrichment omits it.
            enrichment: new Dictionary<int, WorkItemRelationTarget>());

        var relations = history.Events.SelectMany(e => e.Relations).ToList();
        relations.Count.ShouldBe(2);
        relations.Select(r => r.Kind).ShouldBe([RelationChangeKind.Added, RelationChangeKind.Removed]);
        relations.ShouldAllBe(r => r.Target!.Deleted);
    }

    [Fact]
    public void FieldLifecycleFixture_BriefIsDramaticallySmallerThanFull()
    {
        // The payload-size decision behind brief-by-default, measured on real data.
        var updates = LoadFixture("updates-3301.json");

        var brief = Domain.Services.WorkItemHistoryJsonWriter.Write(
            WorkItemHistoryProjector.Project(3301, updates, WorkItemHistoryOptions.Brief));
        var full = Domain.Services.WorkItemHistoryJsonWriter.Write(
            WorkItemHistoryProjector.Project(3301, updates, new WorkItemHistoryOptions(DetailAll: true)));

        brief.Length.ShouldBeLessThan(full.Length / 2);
    }

    [Fact]
    public void RealFixtures_NeverEmitTheSentinelTimestamp()
    {
        foreach (var name in new[] { "updates-3316.json", "updates-3320.json", "updates-3301.json", "updates-orphan.json" })
        {
            var history = WorkItemHistoryProjector.Project(
                1, LoadFixture(name), new WorkItemHistoryOptions(DetailAll: true));

            foreach (var evt in history.Events)
                evt.ChangedAt?.Year.ShouldNotBe(9999, $"{name} update {evt.UpdateId}");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static WorkItemHistoryEvent ProjectOne(
        AdoWorkItemUpdate update,
        WorkItemHistoryOptions? options = null)
        => WorkItemHistoryProjector
            .Project(42, [update], options ?? WorkItemHistoryOptions.Brief)
            .Events.Single();

    private static AdoWorkItemUpdate Parse(string json)
        => JsonSerializer.Deserialize(json, TwigJsonContext.Default.AdoWorkItemUpdate)!;

    internal static List<AdoWorkItemUpdate> LoadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "History", fileName);
        File.Exists(path).ShouldBeTrue($"Missing history fixture: {path}");

        var response = JsonSerializer.Deserialize(
            File.ReadAllText(path), TwigJsonContext.Default.AdoWorkItemUpdatesResponse)!;
        return response.Value ?? [];
    }
}
