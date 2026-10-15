using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class AdoRestClientFindPublishedIntentTests
{
    [Fact]
    public async Task ExactMarkerFindsItsSeedInsteadOfAnEarlierSameTitleLegacyCandidate()
    {
        var intent = Intent("Bob's staged seed");
        using var handler = new RecoveryServer(intent,
            [new(12), new(333) { InitialDescription = Correlation(intent).DescriptionMarkerHtml }]);
        using var http = new HttpClient(handler);

        (await Client(http).FindPublishedIntentAsync(intent)).ShouldBe(333);
    }

    [Fact]
    public async Task RecoveryUsesCreationRevisionDespiteCurrentTitleTypeAreaAndDescriptionEdits()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent,
            [new(333)
            {
                InitialDescription = Correlation(intent).DescriptionMarkerHtml,
                CurrentTitle = "Human renamed this item",
                CurrentType = "Another fixture type",
                CurrentArea = "testproject\\Moved",
                CurrentDescription = "<p>Human replaced the description.</p>",
            }]);
        using var http = new HttpClient(handler);

        (await Client(http).FindPublishedIntentAsync(intent)).ShouldBe(333);
    }

    [Fact]
    public async Task SameTitleWithAnotherSupportedGuidIsNotThisCreate()
    {
        var intent = Intent();
        var other = new SeedPublishCorrelation(StagedIdentity.New(), intent.RecordedAt);
        using var handler = new RecoveryServer(intent, [new(12) { InitialDescription = other.DescriptionMarkerHtml }]);
        using var http = new HttpClient(handler);

        (await Client(http).FindPublishedIntentAsync(intent)).ShouldBeNull();
    }

    [Fact]
    public async Task IdenticalTitlesWithDifferentGuidsRecoverOnlyTheExactGuid()
    {
        var intent = Intent();
        var other = new SeedPublishCorrelation(StagedIdentity.New(), intent.RecordedAt);
        using var handler = new RecoveryServer(intent,
            [new(12) { InitialDescription = other.DescriptionMarkerHtml },
             new(333) { InitialDescription = Correlation(intent).DescriptionMarkerHtml }]);
        using var http = new HttpClient(handler);

        (await Client(http).FindPublishedIntentAsync(intent)).ShouldBe(333);
    }

    [Fact]
    public async Task LegacyUniqueTagRemainsReadableFromCreationRevisionWithoutBeingReemitted()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent,
            [new(333) { InitialTags = PublishIntent.IntentTag + ";" + Correlation(intent).Tag }]);
        using var http = new HttpClient(handler);

        (await Client(http).FindPublishedIntentAsync(intent)).ShouldBe(333);
    }

    [Fact]
    public async Task ConstantMarkerAndMatchingTitleCannotAttributeAnUncertainCreate()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent, [new(12)]);
        using var http = new HttpClient(handler);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Client(http).FindPublishedIntentAsync(intent));
        error.Message.ShouldContain("seed-recovery-correlation-unsupported");
    }

    [Fact]
    public async Task DuplicateExactGuidCandidatesRefuseToGuessWhichCreateToAdopt()
    {
        var intent = Intent();
        var marker = Correlation(intent).DescriptionMarkerHtml;
        using var handler = new RecoveryServer(intent,
            [new(333) { InitialDescription = marker }, new(444) { InitialDescription = marker }]);
        using var http = new HttpClient(handler);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Client(http).FindPublishedIntentAsync(intent));
        error.Message.ShouldContain("seed-recovery-correlation-ambiguous");
    }

    [Fact]
    public async Task DuplicateLegacyExactTagsAlsoRefuseToGuess()
    {
        var intent = Intent();
        var tags = PublishIntent.IntentTag + ";" + Correlation(intent).Tag;
        using var handler = new RecoveryServer(intent,
            [new(333) { InitialTags = tags }, new(444) { InitialTags = tags }]);
        using var http = new HttpClient(handler);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Client(http).FindPublishedIntentAsync(intent));
        error.Message.ShouldContain("seed-recovery-correlation-ambiguous");
    }

    [Fact]
    public async Task DuplicateParagraphMarkersAreUnsupportedRatherThanAnExactMatch()
    {
        var intent = Intent();
        var marker = Correlation(intent).DescriptionMarkerHtml;
        using var handler = new RecoveryServer(intent, [new(333) { InitialDescription = marker + marker }]);
        using var http = new HttpClient(handler);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Client(http).FindPublishedIntentAsync(intent));
        error.Message.ShouldContain("seed-recovery-correlation-unsupported");
    }

    [Theory]
    [InlineData("Different original title", "Fixture type")]
    [InlineData("A staged seed", "Different original type")]
    public async Task ExactGuidMustMatchOriginalTitleAndType(string title, string type)
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent,
            [new(333)
            {
                InitialDescription = Correlation(intent).DescriptionMarkerHtml,
                InitialTitle = title,
                InitialType = type,
            }]);
        using var http = new HttpClient(handler);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Client(http).FindPublishedIntentAsync(intent));
        error.Message.ShouldContain("seed-recovery-correlation-unsupported");
    }

    [Fact]
    public async Task MarkerAddedToCurrentDescriptionCannotAttributeTheCreation()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent,
            [new(333) { CurrentDescription = Correlation(intent).DescriptionMarkerHtml }]);
        using var http = new HttpClient(handler);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Client(http).FindPublishedIntentAsync(intent));
        error.Message.ShouldContain("seed-recovery-correlation-unsupported");
    }

    [Fact]
    public async Task ExactDescriptionGuidIsAuthoritativeDespiteClientServerClockSkew()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent,
            [new(333)
            {
                InitialDescription = Correlation(intent).DescriptionMarkerHtml,
                CreatedAt = intent.RecordedAt.AddMinutes(-1),
            }]);
        using var http = new HttpClient(handler);

        (await Client(http).FindPublishedIntentAsync(intent)).ShouldBe(333);
    }

    [Fact]
    public async Task SharedTagInAnotherProjectIsNotACandidate()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent,
            [new(333) { Project = "anotherproject", InitialDescription = Correlation(intent).DescriptionMarkerHtml }]);
        using var http = new HttpClient(handler);

        (await Client(http).FindPublishedIntentAsync(intent)).ShouldBeNull();
    }

    [Fact]
    public async Task NoCandidateLeavesTheSeedUnpublished()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent, []);
        using var http = new HttpClient(handler);

        (await Client(http).FindPublishedIntentAsync(intent)).ShouldBeNull();
    }

    [Fact]
    public async Task DurableAcknowledgmentRecoversAfterDiscoveryTagWasAlreadyRemoved()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent,
            [new(333)
            {
                InitialDescription = Correlation(intent).DescriptionMarkerHtml,
                CurrentTags = "twig; urgent",
                CurrentDescription = "<p>Human edits after cleanup.</p>",
            }]);
        using var http = new HttpClient(handler);

        (await Client(http, Correlation(intent)).FindPublishedIntentAsync(intent)).ShouldBe(333);
    }

    [Fact]
    public async Task DurableAcknowledgmentStillRequiresOriginalGuidEvidence()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent, [new(333) { CurrentTags = "twig" }]);
        using var http = new HttpClient(handler);

        var error = await Should.ThrowAsync<InvalidOperationException>(() =>
            Client(http, Correlation(intent)).FindPublishedIntentAsync(intent));
        error.Message.ShouldContain("seed-recovery-correlation-unsupported");
    }

    private static PublishIntent Intent(string title = "A staged seed") => new()
    {
        Identity = StagedIdentity.New(),
        Title = title,
        TypeName = "Fixture type",
        RecordedAt = new DateTimeOffset(2026, 7, 27, 15, 2, 58, 900, TimeSpan.Zero),
    };

    private static SeedPublishCorrelation Correlation(PublishIntent intent) => new(intent.Identity, intent.RecordedAt);

    private static AdoRestClient Client(HttpClient http, SeedPublishCorrelation? acknowledged = null)
        => new(http, acknowledged is null ? new FakeAuthProvider() : new AcknowledgedAuth(acknowledged),
            "https://dev.azure.com/testorg", "testproject", new WorkItemMapper());

    private sealed class AcknowledgedAuth(SeedPublishCorrelation recorded)
        : IAuthenticationProvider, ISeedPublishConfirmationGuard
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => Task.FromResult("fake-bearer-token");
        public void InvalidateToken() { }
        public Task<bool> CanCleanPublishedSeedAsync(int id, SeedPublishCorrelation correlation, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<int?> FindAcknowledgedPublishedSeedAsync(SeedPublishCorrelation correlation, CancellationToken ct = default)
            => Task.FromResult<int?>(correlation.Identity == recorded.Identity
                && correlation.IntentRecordedAt == recorded.IntentRecordedAt ? 333 : null);
    }

    private sealed record RecoveryRow(int Id)
    {
        public string Project { get; init; } = "testproject";
        public string? InitialTitle { get; init; }
        public string? InitialType { get; init; }
        public string InitialTags { get; init; } = PublishIntent.IntentTag;
        public string InitialDescription { get; init; } = "<p>Human description.</p>";
        public DateTimeOffset? CreatedAt { get; init; }
        public string? CurrentTitle { get; init; }
        public string? CurrentType { get; init; }
        public string? CurrentArea { get; init; }
        public string CurrentTags { get; init; } = PublishIntent.IntentTag;
        public string? CurrentDescription { get; init; }
    }

    // Immutable creation fields are distinct from edited current fields. This server
    // exercises candidate discovery and revision reads, not native intent settlement.
    private sealed class RecoveryServer(PublishIntent intent, RecoveryRow[] rows) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/wiql", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var query = body.RootElement.GetProperty("query").GetString()!;
                if (!query.Contains("[System.TeamProject] = 'testproject'", StringComparison.Ordinal))
                    return Response(HttpStatusCode.BadRequest, "{\"message\":\"Recovery candidate query must scope the project explicitly\"}");
                var candidates = rows.Where(row => row.Project == "testproject"
                    && row.CurrentTags.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Contains(PublishIntent.IntentTag, StringComparer.OrdinalIgnoreCase));
                if (query.Contains("[System.Title] =", StringComparison.Ordinal))
                    candidates = candidates.Where(row => (row.CurrentTitle ?? row.InitialTitle ?? intent.Title) == intent.Title);
                if (query.Contains("[System.WorkItemType] =", StringComparison.Ordinal))
                    candidates = candidates.Where(row => (row.CurrentType ?? row.InitialType ?? intent.TypeName) == intent.TypeName);
                return JsonResponse(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("queryType", "flat");
                    writer.WriteStartArray("workItems");
                    foreach (var row in candidates)
                    {
                        writer.WriteStartObject();
                        writer.WriteNumber("id", row.Id);
                        writer.WriteString("url", "");
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            var segments = uri.AbsolutePath.Split('/');
            var isCreation = uri.AbsolutePath.EndsWith("/revisions/1", StringComparison.Ordinal);
            var idSegment = isCreation ? segments[^3] : segments[^1];
            if (uri.AbsolutePath.Contains("/workitems/", StringComparison.Ordinal)
                && int.TryParse(idSegment, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var row = rows.Single(row => row.Id == id);
                return JsonResponse(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", row.Id);
                    writer.WriteNumber("rev", isCreation ? 1 : 3);
                    writer.WriteStartObject("fields");
                    writer.WriteString("System.Title", isCreation ? row.InitialTitle ?? intent.Title : row.CurrentTitle ?? row.InitialTitle ?? intent.Title);
                    writer.WriteString("System.WorkItemType", isCreation ? row.InitialType ?? intent.TypeName : row.CurrentType ?? row.InitialType ?? intent.TypeName);
                    writer.WriteString("System.State", "Fixture state");
                    writer.WriteString("System.TeamProject", row.Project);
                    writer.WriteString("System.AreaPath", isCreation ? "testproject" : row.CurrentArea ?? "testproject");
                    writer.WriteString("System.IterationPath", "testproject");
                    writer.WriteString("System.Tags", isCreation ? row.InitialTags : row.CurrentTags);
                    writer.WriteString("System.Description", isCreation ? row.InitialDescription : row.CurrentDescription ?? row.InitialDescription);
                    writer.WriteString("System.CreatedDate", row.CreatedAt ?? intent.RecordedAt.AddSeconds(1));
                    writer.WriteEndObject();
                    writer.WriteStartArray("relations");
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                });
            }
            return Response(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage JsonResponse(Action<Utf8JsonWriter> write)
        {
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes)) write(writer);
            return Response(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes.ToArray()));
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
