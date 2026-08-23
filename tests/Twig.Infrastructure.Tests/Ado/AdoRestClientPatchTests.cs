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
    public async Task PatchAsync_ClearingAssignment_SendsRemoveFieldOperation()
    {
        var handler = new PatchTrackingHandler();
        var client = CreateClient(handler);

        await client.PatchAsync(
            id: 645,
            changes: [new FieldChange("System.AssignedTo", "Daniel Green", null)],
            expectedRevision: 4);

        using var document = JsonDocument.Parse(handler.LastRequestBody!);
        var operation = document.RootElement[0];

        operation.GetProperty("op").GetString().ShouldBe("remove");
        operation.GetProperty("path").GetString().ShouldBe("/fields/System.AssignedTo");
        operation.TryGetProperty("value", out _).ShouldBeFalse();
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
}
