using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

public sealed class AdoRestClientFindPublishedIntentTests
{
    [Fact]
    public async Task ExactNativeCorrelationFindsItsSeedInsteadOfAnEarlierSameTitleLegacyCandidate()
    {
        var intent = Intent("Bob's staged seed");
        var tag = new SeedPublishCorrelation(intent.Identity, intent.RecordedAt).Tag;
        using var handler = new RecoveryServer(intent, [(12, PublishIntent.IntentTag), (333, PublishIntent.IntentTag + ";" + tag)]);
        using var http = new HttpClient(handler);
        var client = Client(http);

        (await client.FindPublishedIntentAsync(intent)).ShouldBe(333);
    }

    [Fact]
    public async Task ConstantMarkerAndMatchingTitleCannotAttributeAnUncertainCreate()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent, [(12, PublishIntent.IntentTag)]);
        using var http = new HttpClient(handler);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Client(http).FindPublishedIntentAsync(intent));
        error.Message.ShouldContain("seed-recovery-correlation-unsupported");
    }

    [Fact]
    public async Task DuplicateExactCorrelationsRefuseToGuessWhichCreateToAdopt()
    {
        var intent = Intent();
        var tag = new SeedPublishCorrelation(intent.Identity, intent.RecordedAt).Tag;
        using var handler = new RecoveryServer(intent, [(333, PublishIntent.IntentTag + ";" + tag), (444, PublishIntent.IntentTag + ";" + tag)]);
        using var http = new HttpClient(handler);

        var error = await Should.ThrowAsync<InvalidOperationException>(() => Client(http).FindPublishedIntentAsync(intent));
        error.Message.ShouldContain("seed-recovery-correlation-ambiguous");
    }

    [Fact]
    public async Task NoCorrelatedOrLegacyCandidateLeavesTheSeedUnpublished()
    {
        var intent = Intent();
        using var handler = new RecoveryServer(intent, []);
        using var http = new HttpClient(handler);

        (await Client(http).FindPublishedIntentAsync(intent)).ShouldBeNull();
    }

    private static PublishIntent Intent(string title = "A staged seed") => new()
    {
        Identity = StagedIdentity.New(),
        Title = title,
        TypeName = "Fixture type",
        RecordedAt = new DateTimeOffset(2026, 7, 27, 15, 2, 58, 900, TimeSpan.Zero),
    };

    private static AdoRestClient Client(HttpClient http)
        => new(http, new FakeAuthProvider(), "https://dev.azure.com/testorg", "testproject", new WorkItemMapper());

    // This server models observable ADO lookup/field behavior, not native intent settlement.
    private sealed class RecoveryServer(PublishIntent intent, (int Id, string Tags)[] rows) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/wiql", StringComparison.Ordinal))
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var query = body.RootElement.GetProperty("query").GetString()!;
                // Timed WIQL is rejected by ADO without this URL option; body options do not suffice.
                if (query.Contains("System.CreatedDate", StringComparison.Ordinal)
                    && !uri.Query.Contains("timePrecision=true", StringComparison.OrdinalIgnoreCase))
                    return Response(HttpStatusCode.BadRequest, "{\"message\":\"Timed WIQL requires timePrecision\"}");
                var exact = new SeedPublishCorrelation(intent.Identity, intent.RecordedAt).Tag;
                var wanted = query.Contains(exact, StringComparison.Ordinal) ? exact : PublishIntent.IntentTag;
                return JsonResponse(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("queryType", "flat");
                    writer.WriteStartArray("workItems");
                    foreach (var row in rows.Where(row => row.Tags.Split(';').Contains(wanted, StringComparer.OrdinalIgnoreCase)))
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
            if (uri.AbsolutePath.Contains("/workitems/", StringComparison.Ordinal)
                && int.TryParse(Path.GetFileName(uri.AbsolutePath), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                var row = rows.Single(row => row.Id == id);
                return JsonResponse(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteNumber("id", row.Id);
                    writer.WriteNumber("rev", 1);
                    writer.WriteStartObject("fields");
                    writer.WriteString("System.Title", intent.Title);
                    writer.WriteString("System.WorkItemType", intent.TypeName);
                    writer.WriteString("System.State", "Fixture state");
                    writer.WriteString("System.AreaPath", "testproject");
                    writer.WriteString("System.IterationPath", "testproject");
                    writer.WriteString("System.Tags", row.Tags);
                    writer.WriteString("System.CreatedDate", intent.RecordedAt.AddSeconds(1));
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
