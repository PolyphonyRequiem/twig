using Microsoft.Data.Sqlite;
using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Plan;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

public sealed class SqliteReviewAssessmentRepositoryTests
{
    private static readonly PlanOrigin Origin = new(
        "checkout", "fingerprint", 1, "connection", "binding", 1, "explicit", 1,
        "native-account", "test", "credential-reference", "tenant", "object", "issuer", "authority", null);

    [Fact]
    public async Task FeedbackIsDurableLiteralAndNeverAuthorization()
    {
        var directory = NewDirectory();
        try
        {
            RetainedAssessmentReview retained;
            string journalBefore;
            using (var store = Open(directory))
            {
                var model = await ImportModel(store);
                journalBefore = JournalSnapshot(store);
                var repository = Repository(store);
                retained = repository.Retain(model, Origin);
                retained.ModelJson.ShouldBe(ChangeProposalReviewModelJson.Serialize(model));
                retained.ModelVersion.ShouldBe(model.ModelVersion);
                var result = repository.Append(retained.Digest, retained.Fingerprint, Origin, 0,
                    "reported reviewer", "canvas-feedback", "effects", "change-title", "<b>Accept review</b>\n not authorization");
                result.Recorded.ShouldBeTrue();
                result.Review.Revision.ShouldBe(1);
                result.Review.Assessments.Single().Text.ShouldBe("<b>Accept review</b>\n not authorization");
                JournalSnapshot(store).ShouldBe(journalBefore);
            }
            using (var reopened = Open(directory))
            {
                var review = Repository(reopened).Read(retained.Digest, retained.Fingerprint, Origin);
                review.Revision.ShouldBe(1);
                review.Assessments.Single().Caller.ShouldBe("reported reviewer");
                review.Assessments.Single().Surface.ShouldBe("canvas-feedback");
                review.Assessments.Single().OperationId.ShouldBe("change-title");
                review.CapturedAt.ShouldBe(retained.CapturedAt);
                review.ModelJson.ShouldBe(retained.ModelJson);
                JournalSnapshot(reopened).ShouldBe(journalBefore);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task RepeatedRetentionDoesNotEraseHistoryOrMoveCaptureTime()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var model = await ImportModel(store);
        var repository = Repository(store);
        var first = repository.Retain(model, Origin);
        Append(repository, first, 0, "Original");
        var repeated = repository.Retain(model, Origin);
        repeated.Fingerprint.ShouldBe(first.Fingerprint);
        repeated.CapturedAt.ShouldBe(first.CapturedAt);
        repeated.Revision.ShouldBe(1);
        repeated.Assessments.Single().Text.ShouldBe("Original");
    }

    [Fact]
    public async Task ChangedObservationWithSameProposalDigestStartsUnreviewedAndKeepsOldFeedback()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var model = await ImportModel(store);
        var repository = Repository(store);
        var first = repository.Retain(model, Origin);
        Append(repository, first, 0, "Only the first observation was reviewed");
        var second = repository.Retain(model with { Rationale = "Different context" }, Origin);
        second.Digest.ShouldBe(first.Digest);
        second.Fingerprint.ShouldNotBe(first.Fingerprint);
        second.Revision.ShouldBe(0);
        second.Assessments.ShouldBeEmpty();
        repository.Read(first.Digest, first.Fingerprint, Origin).Assessments.Single().Text
            .ShouldBe("Only the first observation was reviewed");
        Should.Throw<InvalidOperationException>(() => repository.Read(new string('f', 64), first.Fingerprint, Origin))
            .Message.ShouldContain("review-not-found");
    }

    [Fact]
    public async Task TwoConnectionsWithSameExpectedRevisionCannotOverwriteEachOther()
    {
        var directory = NewDirectory();
        try
        {
            using var firstStore = Open(directory);
            var model = await ImportModel(firstStore);
            var firstRepository = Repository(firstStore);
            var captured = firstRepository.Retain(model, Origin);
            using var secondStore = Open(directory);
            var secondRepository = Repository(secondStore);
            var secondView = secondRepository.Read(captured.Digest, captured.Fingerprint, Origin);
            secondView.Revision.ShouldBe(0);
            Append(firstRepository, captured, 0, "Winner").Recorded.ShouldBeTrue();
            var loser = Append(secondRepository, secondView, 0, "Unsent contender text");
            loser.Recorded.ShouldBeFalse();
            loser.Review.Revision.ShouldBe(1);
            loser.Review.Assessments.Single().Text.ShouldBe("Winner");
            Append(secondRepository, loser.Review, 1, "Explicit resubmission").Recorded.ShouldBeTrue();
            firstRepository.Read(captured.Digest, captured.Fingerprint, Origin).Assessments
                .Select(item => item.Text).ShouldBe(["Winner", "Explicit resubmission"]);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task SupersessionIsExplicitScopedAndAppendOnly()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = Repository(store);
        var capture = repository.Retain(await ImportModel(store), Origin);
        Append(repository, capture, 0, "Original");
        var replacement = Append(repository, capture, 1, "Correction", 1).Review;
        replacement.Assessments.Count.ShouldBe(2);
        replacement.Assessments[0].Text.ShouldBe("Original");
        replacement.Assessments[1].SupersedesRevision.ShouldBe(1);
        Should.Throw<ArgumentException>(() => Append(repository, capture, 2, "Second branch", 1));
        Should.Throw<ArgumentException>(() => repository.Append(capture.Digest, capture.Fingerprint, Origin, 2,
            "another reported caller", "test", "review", null, "Not my entry", 2));
        Should.Throw<ArgumentException>(() => repository.Append(capture.Digest, capture.Fingerprint, Origin, 2,
            "reported caller", "test", "effects", "change-title", "Not the same scope", 2));
        repository.Read(capture.Digest, capture.Fingerprint, Origin).Revision.ShouldBe(2);
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("workspace")]
    [InlineData("operation")]
    [InlineData("coverage")]
    public async Task RetentionRejectsAnotherOriginWorkspaceOrOperationSet(string change)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var model = await ImportModel(store);
        var origin = Origin;
        switch (change)
        {
            case "origin": origin = Origin with { BindingRevision = 2 }; break;
            case "workspace": model = model with { Workspace = model.Workspace with { Project = "elsewhere" } }; break;
            case "operation": model = model with { Operations = [model.Operations[0] with { OpId = "other" }] }; break;
            case "coverage": model = model with { Operations = [] }; break;
        }
        Should.Throw<InvalidOperationException>(() => Repository(store).Retain(model, origin));
        using var count = store.GetConnection().CreateCommand();
        count.CommandText = "SELECT count(*) FROM pending.review_observations;";
        Convert.ToInt32(count.ExecuteScalar()).ShouldBe(0);
    }

    [Fact]
    public async Task ChangedOriginOrUnknownEffectCannotReceiveFeedback()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = Repository(store);
        var capture = repository.Retain(await ImportModel(store), Origin);
        var changed = Origin with { IdentityId = "another-account" };
        Should.Throw<InvalidOperationException>(() => repository.Read(capture.Digest, capture.Fingerprint, changed))
            .Message.ShouldContain("review-origin-mismatch");
        Should.Throw<InvalidOperationException>(() => repository.Append(capture.Digest, capture.Fingerprint, changed,
            0, "reported caller", "test", "review", null, "Wrong origin"))
            .Message.ShouldContain("review-origin-mismatch");
        Should.Throw<ArgumentException>(() => repository.Append(capture.Digest, capture.Fingerprint, Origin,
            0, "reported caller", "test", "effects", "future-effect", "Wrong effect"));
        repository.Read(capture.Digest, capture.Fingerprint, Origin).Revision.ShouldBe(0);
    }

    [Fact]
    public async Task FailedInsertRollsBackRevisionAndAllowsExplicitRetry()
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = Repository(store);
        var capture = repository.Retain(await ImportModel(store), Origin);
        using (var trigger = store.GetConnection().CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER pending.refuse_assessment BEFORE INSERT ON review_assessments
                BEGIN SELECT RAISE(ABORT, 'fixture insert failure'); END;
                """;
            trigger.ExecuteNonQuery();
        }
        Should.Throw<SqliteException>(() => Append(repository, capture, 0, "Must not advance head"));
        repository.Read(capture.Digest, capture.Fingerprint, Origin).Revision.ShouldBe(0);
        using (var drop = store.GetConnection().CreateCommand())
        {
            drop.CommandText = "DROP TRIGGER pending.refuse_assessment;";
            drop.ExecuteNonQuery();
        }
        Append(repository, capture, 0, "Explicit retry after known rollback").Recorded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("caller")]
    [InlineData("surface")]
    [InlineData("text")]
    [InlineData("section")]
    [InlineData("revision")]
    public async Task InvalidInputCannotAdvanceRevision(string invalid)
    {
        using var store = new SqliteCacheStore("Data Source=:memory:");
        var repository = Repository(store);
        var capture = repository.Retain(await ImportModel(store), Origin);
        Should.Throw<ArgumentException>(() => repository.Append(capture.Digest, capture.Fingerprint, Origin,
            invalid == "revision" ? -1 : 0, invalid == "caller" ? "" : "reported caller",
            invalid == "surface" ? "" : "test", invalid == "section" ? "apply" : "review", null,
            invalid == "text" ? new string('x', 16385) : "Feedback"));
        repository.Read(capture.Digest, capture.Fingerprint, Origin).Revision.ShouldBe(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("preparing")]
    [InlineData("active")]
    [InlineData("rolled-back")]
    [InlineData("cancelled")]
    public void UpgradeFrom18PreservesDataAndRefusesRetainedPreservationIntent(string? state)
    {
        var directory = NewDirectory();
        try
        {
            using (var old = Open(directory))
            using (var downgrade = old.GetConnection().CreateCommand())
            {
                downgrade.CommandText = """
                    DROP TABLE pending.review_assessments;
                    DROP TABLE pending.review_observations;
                    PRAGMA pending.user_version = 18;
                    INSERT INTO pending.workspace_context(key,value) VALUES ('fixture-untouched','preserve me');
                    """;
                downgrade.ExecuteNonQuery();
                if (state is not null)
                {
                    downgrade.CommandText = """
                        INSERT INTO pending.preservation_migrations
                          (intent_id,slot,source_generation,source_digest,state,coverage_version,backup_directory)
                        VALUES ('fixture',@slot,'generation','digest',@state,1,'backup');
                        """;
                    downgrade.Parameters.AddWithValue("@state", state);
                    downgrade.Parameters.AddWithValue("@slot", state == "cancelled" ? DBNull.Value : 1);
                    downgrade.ExecuteNonQuery();
                }
            }
            if (state is not null and not "cancelled")
            {
                Should.Throw<InvalidOperationException>(() =>
                {
                    using var refused = Open(directory);
                }).Message.ShouldContain("preservation-upgrade-blocked: this v18");
                using var pending = new SqliteConnection($"Data Source={Path.Combine(directory, "pending.db")};Pooling=False");
                pending.Open();
                using var check = pending.CreateCommand();
                check.CommandText = "PRAGMA user_version;";
                Convert.ToInt32(check.ExecuteScalar()).ShouldBe(18);
                check.CommandText = "SELECT state FROM preservation_migrations WHERE intent_id='fixture';";
                check.ExecuteScalar().ShouldBe(state);
            }
            else
            {
                using var upgraded = Open(directory);
                using var check = upgraded.GetConnection().CreateCommand();
                check.CommandText = "SELECT value FROM pending.workspace_context WHERE key='fixture-untouched';";
                check.ExecuteScalar().ShouldBe("preserve me");
                check.CommandText = "PRAGMA pending.user_version;";
                Convert.ToInt32(check.ExecuteScalar()).ShouldBe(19);
                PreservationInventory.AllTables.Any(table => table.Table == "review_observations").ShouldBeTrue();
                PreservationInventory.AllTables.Any(table => table.Table == "review_assessments").ShouldBeTrue();
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static AssessmentAppendResult Append(SqliteReviewAssessmentRepository repository,
        RetainedAssessmentReview capture, long revision, string text, long? supersedes = null)
        => repository.Append(capture.Digest, capture.Fingerprint, Origin, revision,
            "reported caller", "test", "review", null, text, supersedes);

    private static SqliteReviewAssessmentRepository Repository(SqliteCacheStore store) => new(store, TimeProvider.System);
    private static SqliteCacheStore Open(string directory)
        => new($"Data Source={Path.Combine(directory, "twig.db")};Pooling=False");

    private static string NewDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"twig-review-assessment-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<ChangeProposalReviewModel> ImportModel(SqliteCacheStore store)
    {
        var parsed = new PlanDocumentParser().Parse("""
            {"version":1,"workspace":{"organization":"fixture","project":"review"},
             "operations":[{"id":"change-title","kind":"batch","workItemId":42,"expectedRevision":1,
                "fields":{"System.Title":"Updated title"}}]}
            """);
        parsed.IsValid.ShouldBeTrue();
        var plan = parsed.Plan.ShouldNotBeNull();
        var digest = parsed.Digest.ShouldNotBeNull();
        await new SqlitePlanJournalRepository(store).ImportAsync(plan, parsed.CanonicalJson.ShouldNotBeNull(),
            digest, "fixture.json", DateTimeOffset.UtcNow, origin: Origin);
        var workItems = new SqliteWorkItemRepository(store, new WorkItemMapper());
        return await new ChangeProposalReviewModelBuilder(workItems).BuildAsync(plan, digest, [], [], canApply: true);
    }

    private static string JournalSnapshot(SqliteCacheStore store)
    {
        using var query = store.GetConnection().CreateCommand();
        query.CommandText = "SELECT * FROM pending.proposal_journals;";
        using var rows = query.ExecuteReader();
        rows.Read().ShouldBeTrue();
        var fields = new string?[rows.FieldCount];
        for (var index = 0; index < fields.Length; index++)
            fields[index] = rows.IsDBNull(index) ? null : Convert.ToString(rows.GetValue(index), System.Globalization.CultureInfo.InvariantCulture);
        return string.Join("\n", fields.Select(value => value is null ? "NULL" : $"{value.Length}:{value}"));
    }
}
