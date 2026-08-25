using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// HTTP-contract tests for <see cref="AdoRestClient.PatchAsync"/>.
/// </summary>
public sealed class AdoRestClientPatchTests
{
    [Fact]
    public async Task PatchAsync_RevisionBoundClearAssignment_PrependsRevisionTest()
    {
        var handler = new PatchTrackingHandler();
        var client = CreateClient(handler);

        await client.PatchAsync(
            id: 645,
            changes: [new FieldChange("System.AssignedTo", "Daniel Green", null)],
            expectedRevision: 4);

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        document.RootElement.GetArrayLength().ShouldBe(2);

        var revisionTest = document.RootElement[0];
        revisionTest.GetProperty("op").GetString().ShouldBe("test");
        revisionTest.GetProperty("path").GetString().ShouldBe("/rev");
        revisionTest.GetProperty("value").GetInt32().ShouldBe(4);

        var operation = document.RootElement[1];
        operation.GetProperty("op").GetString().ShouldBe("remove");
        operation.GetProperty("path").GetString().ShouldBe("/fields/System.AssignedTo");
        operation.TryGetProperty("value", out _).ShouldBeFalse();
    }


    [Fact]
    public async Task FetchAtRevisionAsync_UsesRevisionEndpoint_AndMapsSnapshot()
    {
        var handler = new RevisionFetchHandler();
        var client = CreateClient(handler);

        var snapshot = await client.FetchAtRevisionAsync(645, 4);

        handler.RequestUrl.ShouldNotBeNull();
        handler.RequestUrl.ShouldContain("/workitems/645/revisions/4");
        snapshot.Revision.ShouldBe(4);
        snapshot.TypeName.ShouldBe("Task");
        snapshot.Title.ShouldBe("Test");
        snapshot.State.ShouldBe("Doing");
        snapshot.Fields["Custom.Gated"].ShouldBe("signed");
    }

    private static AdoRestClient CreateClient(HttpMessageHandler handler)
    {
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        return new AdoRestClient(
            http,
            new FakeAuthProvider(),
            "https://dev.azure.com/testorg",
            "testproject",
            new WorkItemMapper());
    }

    private sealed class FakeAuthProvider : IAuthenticationProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken ct = default)
            => Task.FromResult("fake-bearer-token");

        public void InvalidateToken() { }
    }

    private sealed class PatchTrackingHandler : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);

            const string responseJson =
                "{\"id\":645,\"rev\":5,\"fields\":{\"System.WorkItemType\":\"Task\",\"System.Title\":\"Test\",\"System.State\":\"To Do\"}}";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RevisionFetchHandler : HttpMessageHandler
    {
        public string? RequestUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUrl = request.RequestUri?.ToString();

            const string responseJson =
                "{\"id\":645,\"rev\":4,\"fields\":{\"System.WorkItemType\":\"Task\",\"System.Title\":\"Test\",\"System.State\":\"Doing\",\"Custom.Gated\":\"signed\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json"),
            });
        }
    }
}
