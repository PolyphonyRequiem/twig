using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Twig.Infrastructure.Auth;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class AdoRestClientPublishMetadataTests
{
    [Fact]
    public async Task ConfirmedCleanupPreservesCurrentHumanDescriptionAndNonownedTags()
    {
        var correlation = Correlation(ownsTag: true);
        const string before = "<section data-human=\"yes\"><p>Human edit &amp; details.</p></section>";
        const string after = "\n<p>Human footer.</p>";
        using var server = new CleanupServer(correlation, before + correlation.DescriptionMarkerHtml + after,
            "twig; twig-publishing; urgent; twig-publishing-human");
        using var http = new HttpClient(server);

        await Client(http, new ConfirmedAuth(correlation)).ClearPublishMetadataAsync(333, correlation);

        server.Description.ShouldBe(before + after);
        server.Tags.ShouldBe("twig; urgent; twig-publishing-human");
        server.Revision.ShouldBe(5);
    }

    [Fact]
    public async Task CallerPreexistingSharedTagIsRetainedWhileExactMarkerIsRemoved()
    {
        var correlation = Correlation(ownsTag: false);
        const string tags = "TWIG-PUBLISHING; urgent; twig";
        using var server = new CleanupServer(correlation, "<p>Human edit.</p>" + correlation.DescriptionMarkerHtml, tags);
        using var http = new HttpClient(server);

        await Client(http, new ConfirmedAuth(correlation)).ClearPublishMetadataAsync(333, correlation);

        server.Description.ShouldBe("<p>Human edit.</p>");
        server.Tags.ShouldBe(tags);
    }

    [Fact]
    public async Task MissingConfirmationGuardLeavesAllMetadataUntouchedWithoutHttpRequests()
    {
        var correlation = Correlation(ownsTag: true);
        using var server = new CleanupServer(correlation, correlation.DescriptionMarkerHtml, PublishIntent.IntentTag);
        using var http = new HttpClient(server);

        await Client(http, new FakeAuthProvider()).ClearPublishMetadataAsync(333, correlation);

        server.RequestCount.ShouldBe(0);
        server.Description.ShouldBe(correlation.DescriptionMarkerHtml);
        server.Tags.ShouldBe(PublishIntent.IntentTag);
    }

    [Fact]
    public async Task UnconfirmedNativeCreateLeavesMetadataUntouchedWithoutHttpRequests()
    {
        var correlation = Correlation(ownsTag: true);
        using var server = new CleanupServer(correlation, correlation.DescriptionMarkerHtml, PublishIntent.IntentTag);
        using var http = new HttpClient(server);

        await Client(http, new ConfirmedAuth(correlation, confirmed: false)).ClearPublishMetadataAsync(333, correlation);

        server.RequestCount.ShouldBe(0);
        server.Description.ShouldBe(correlation.DescriptionMarkerHtml);
        server.Tags.ShouldBe(PublishIntent.IntentTag);
    }

    [Fact]
    public async Task CallerCannotPromoteUnownedTagToOwnedThroughCleanupMetadata()
    {
        var recorded = Correlation(ownsTag: false);
        var tampered = recorded with { OwnsIntentTag = true };
        using var server = new CleanupServer(recorded, recorded.DescriptionMarkerHtml, PublishIntent.IntentTag);
        using var http = new HttpClient(server);

        await Client(http, new ConfirmedAuth(recorded)).ClearPublishMetadataAsync(333, tampered);

        server.RequestCount.ShouldBe(0);
        server.Description.ShouldBe(recorded.DescriptionMarkerHtml);
        server.Tags.ShouldBe(PublishIntent.IntentTag);
    }

    [Fact]
    public async Task PreviouslyRemovedMarkerStillAllowsConfirmedOwnedTagCleanup()
    {
        var correlation = Correlation(ownsTag: true);
        const string description = "<p>Human removed the bookkeeping marker.</p>";
        using var server = new CleanupServer(correlation, description, "urgent; twig-publishing");
        using var http = new HttpClient(server);

        await Client(http, new ConfirmedAuth(correlation)).ClearPublishMetadataAsync(333, correlation);

        server.Description.ShouldBe(description);
        server.Tags.ShouldBe("urgent");
    }

    [Fact]
    public async Task MissingMarkerAndUnownedSharedTagProduceNoMutation()
    {
        var correlation = Correlation(ownsTag: false);
        const string description = "<p>Human description.</p>";
        using var server = new CleanupServer(correlation, description, PublishIntent.IntentTag);
        using var http = new HttpClient(server);

        await Client(http, new ConfirmedAuth(correlation)).ClearPublishMetadataAsync(333, correlation);

        server.PatchCount.ShouldBe(0);
        server.Description.ShouldBe(description);
        server.Tags.ShouldBe(PublishIntent.IntentTag);
    }

    [Fact]
    public async Task LegacyCorrelationRetainsLegacyUniqueAndSharedTagsWithoutProvenOwnership()
    {
        var correlation = Correlation(ownsTag: false);
        const string description = "<p>Human description.</p>";
        var tags = "twig; " + PublishIntent.IntentTag + "; " + correlation.Tag;
        using var server = new CleanupServer(correlation, description, tags)
        {
            InitialDescription = description,
            InitialTags = tags,
        };
        using var http = new HttpClient(server);

        await Client(http, new ConfirmedAuth(correlation)).ClearPublishMetadataAsync(333, correlation);

        server.PatchCount.ShouldBe(0);
        server.Description.ShouldBe(description);
        server.Tags.ShouldBe(tags);
    }

    [Fact]
    public async Task UnsupportedOriginalCreationEvidenceCannotAuthorizeCleanup()
    {
        var correlation = Correlation(ownsTag: true);
        using var server = new CleanupServer(correlation, correlation.DescriptionMarkerHtml, PublishIntent.IntentTag)
        {
            InitialDescription = "<p>An unrelated creation.</p>",
        };
        using var http = new HttpClient(server);

        await Client(http, new ConfirmedAuth(correlation)).ClearPublishMetadataAsync(333, correlation);

        server.PatchCount.ShouldBe(0);
        server.Description.ShouldBe(correlation.DescriptionMarkerHtml);
        server.Tags.ShouldBe(PublishIntent.IntentTag);
    }

    [Fact]
    public async Task DuplicateCurrentMarkerRefusesBothDescriptionAndTagRemoval()
    {
        var correlation = Correlation(ownsTag: true);
        var description = correlation.DescriptionMarkerHtml + "<p>Human edit.</p>" + correlation.DescriptionMarkerHtml;
        using var server = new CleanupServer(correlation, description, PublishIntent.IntentTag);
        using var http = new HttpClient(server);

        await Client(http, new ConfirmedAuth(correlation)).ClearPublishMetadataAsync(333, correlation);

        server.PatchCount.ShouldBe(0);
        server.Description.ShouldBe(description);
        server.Tags.ShouldBe(PublishIntent.IntentTag);
    }

    [Fact]
    public async Task CleanupConflictPreservesConcurrentHumanChangesAndLeavesMetadataPending()
    {
        var correlation = Correlation(ownsTag: true);
        using var server = new CleanupServer(correlation, correlation.DescriptionMarkerHtml, "twig-publishing; urgent")
        {
            RaceBeforePatch = true,
        };
        using var http = new HttpClient(server);

        await Should.ThrowAsync<AdoConflictException>(() =>
            Client(http, new ConfirmedAuth(correlation)).ClearPublishMetadataAsync(333, correlation));

        server.Description.ShouldBe(correlation.DescriptionMarkerHtml + "<p>Concurrent human edit.</p>");
        server.Tags.ShouldBe("twig-publishing; urgent; concurrent-human-tag");
        server.PatchCount.ShouldBe(1);
        server.Revision.ShouldBe(5);
    }

    [Fact]
    public async Task LegacyClearIntentTagAlsoRejectsStaleRevisionWithoutOverwritingHumanTags()
    {
        var correlation = Correlation(ownsTag: false);
        using var server = new CleanupServer(correlation, "<p>Human description.</p>", "twig-publishing; urgent")
        {
            RaceBeforePatch = true,
        };
        using var http = new HttpClient(server);

        await Should.ThrowAsync<AdoConflictException>(() => Client(http, new FakeAuthProvider()).ClearIntentTagAsync(333));

        server.Tags.ShouldBe("twig-publishing; urgent; concurrent-human-tag");
        server.PatchCount.ShouldBe(1);
    }

    private static SeedPublishCorrelation Correlation(bool ownsTag)
        => new(StagedIdentity.New(), new DateTimeOffset(2026, 7, 27, 15, 2, 58, TimeSpan.Zero)) { OwnsIntentTag = ownsTag };

    private static AdoRestClient Client(HttpClient http, IAuthenticationProvider auth)
        => new(http, auth, "https://dev.azure.com/testorg", "testproject", new WorkItemMapper());

    private sealed class ConfirmedAuth(SeedPublishCorrelation recorded, bool confirmed = true)
        : IAuthenticationProvider, ISeedPublishConfirmationGuard, IConnectionRemoteWriteGuard
    {
        private readonly FakeAuthProvider _transport = new();
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => _transport.GetAccessTokenAsync(ct);
        public void InvalidateToken() => _transport.InvalidateToken();
        public Task<bool> CanCleanPublishedSeedAsync(int id, SeedPublishCorrelation correlation, CancellationToken ct = default)
            => Task.FromResult(confirmed && id == 333 && correlation == recorded);
        public Task<int?> FindAcknowledgedPublishedSeedAsync(SeedPublishCorrelation correlation, CancellationToken ct = default)
            => Task.FromResult<int?>(null);
        public Task<IConnectionRemoteWriteAdmission> BeginRemoteWriteAsync(ConnectionRemoteWriteRequest request, CancellationToken ct = default)
            => _transport.BeginRemoteWriteAsync(request, ct);
    }

    private sealed class CleanupServer(SeedPublishCorrelation correlation, string description, string tags) : HttpMessageHandler
    {
        public string InitialDescription { get; init; } = correlation.DescriptionMarkerHtml;
        public string InitialTags { get; init; } = "twig; twig-publishing";
        public string Description { get; private set; } = description;
        public string Tags { get; private set; } = tags;
        public int Revision { get; private set; } = 4;
        public bool RaceBeforePatch { get; init; }
        public int RequestCount { get; private set; }
        public int PatchCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            RequestCount++;
            if (request.Method == HttpMethod.Patch)
            {
                PatchCount++;
                if (RaceBeforePatch)
                {
                    Description += "<p>Concurrent human edit.</p>";
                    Tags += "; concurrent-human-tag";
                    Revision++;
                }
                using var patch = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var revisionTest = patch.RootElement.EnumerateArray().FirstOrDefault(operation =>
                    operation.GetProperty("path").GetString() == "/rev" && operation.GetProperty("op").GetString() == "test");
                if (revisionTest.ValueKind != JsonValueKind.Object
                    || revisionTest.GetProperty("value").GetInt32() != Revision
                    || !request.Headers.TryGetValues("If-Match", out var matches)
                    || matches.Single() != Revision.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    return Response(HttpStatusCode.PreconditionFailed, "{\"message\":\"The work item revision changed.\"}");
                foreach (var operation in patch.RootElement.EnumerateArray())
                {
                    var path = operation.GetProperty("path").GetString();
                    if (path == "/fields/System.Description")
                        Description = operation.GetProperty("op").GetString() == "remove" ? string.Empty : operation.GetProperty("value").GetString()!;
                    if (path == "/fields/System.Tags")
                        Tags = operation.GetProperty("value").GetString()!;
                }
                Revision++;
            }
            var isCreation = request.RequestUri!.AbsolutePath.EndsWith("/revisions/1", StringComparison.Ordinal);
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes))
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", 333);
                writer.WriteNumber("rev", isCreation ? 1 : Revision);
                writer.WriteStartObject("fields");
                writer.WriteString("System.Title", "Fixture seed");
                writer.WriteString("System.WorkItemType", "Fixture type");
                writer.WriteString("System.State", "Fixture state");
                writer.WriteString("System.AreaPath", "testproject");
                writer.WriteString("System.IterationPath", "testproject");
                writer.WriteString("System.Description", isCreation ? InitialDescription : Description);
                writer.WriteString("System.Tags", isCreation ? InitialTags : Tags);
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            return Response(HttpStatusCode.OK, Encoding.UTF8.GetString(bytes.ToArray()));
        }

        private static HttpResponseMessage Response(HttpStatusCode status, string body)
            => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
