using System.Net;
using System.Text;
using Microsoft.Data.Sqlite;
using NSubstitute;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Infrastructure.Tests.Services;

public sealed class NativeAdoSetupDiscoveryTests : IDisposable
{
    private const string ProjectId = "11111111-1111-1111-1111-111111111111";
    private const string TeamId = "22222222-2222-2222-2222-222222222222";
    private const string OtherId = "33333333-3333-3333-3333-333333333333";
    private readonly string home = Path.Combine(Path.GetTempPath(), "twig-discovery-" + Guid.NewGuid().ToString("N"));
    private readonly ITokenRefresher refresher = Substitute.For<ITokenRefresher>();
    private ConnectionBindingService? bindings;
    private NativeAadEnrollment enrollment = null!;

    private async Task<NativeConnectionSetup> Setup(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
    {
        var jwt = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"aud":"499b84ac-1321-427f-aa17-267ca6975798","tid":"tenant","oid":"principal","iss":"issuer","exp":4102444800}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
        refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((jwt, null, false));
        bindings = new(home, refresher);
        enrollment = ConnectionBindingService.Enrollment(await bindings.RegisterAadIdentityAsync("chosen", new()
        {
            RefreshToken = "fixture-refresh", ClientId = "fixture-client", TenantId = "tenant",
            ObjectId = "principal", AuthorityHost = "login.microsoftonline.com",
        }));
        (await bindings.ListBindingsAsync("fixture", ProjectId)).ShouldBeEmpty();
        Directory.Exists(Path.Combine(home, "sources")).ShouldBeFalse();
        return new(_ => bindings, transport: new(() => new Handler(async (request, ct) =>
        {
            request.Method.ShouldBe(HttpMethod.Get);
            request.Headers.Authorization!.Scheme.ShouldBe("Bearer");
            request.Headers.Authorization.Parameter.ShouldBe(jwt);
            return await send(request, ct);
        })));
    }

    [Fact]
    public async Task NamedIdentityDiscoversOrganizationsAndPagesWithoutAnyBindingOrWorkspace()
    {
        var calls = new List<string>();
        var setup = await Setup((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json(request.RequestUri.AbsolutePath.EndsWith("/me", StringComparison.Ordinal)
                ? $$"""{"id":"{{OtherId}}"}"""
                : $$"""{"value":[{"accountId":"{{ProjectId}}","accountName":"first"},{"accountId":"{{TeamId}}","accountName":"second"}]}"""));
        });
        var request = new NativeAdoDiscoveryRequest(new(enrollment, "organizations"), Limit: 1);
        var first = await setup.DiscoverAdoAsync(request);
        first.IsSuccess.ShouldBeTrue();
        first.Value.Scope.ShouldBe(request.Scope);
        first.Value.Complete.ShouldBeFalse();
        first.Value.Choices.ShouldBe([new NativeAdoSetupChoice(ProjectId, "first", "first")]);
        var second = await setup.DiscoverAdoAsync(request with { Continuation = first.Value.Continuation });
        second.IsSuccess.ShouldBeTrue();
        second.Value.Choices.Single().Name.ShouldBe("second");
        second.Value.Complete.ShouldBeTrue();
        second.Value.Continuation.ShouldBeNull();
        calls.Count.ShouldBe(4);
        calls.ShouldAllBe(address => address.StartsWith("https://app.vssps.visualstudio.com/", StringComparison.Ordinal));
        calls[1].ShouldContain("memberId=" + OtherId);
        (await bindings!.ListBindingsAsync("fixture", ProjectId)).ShouldBeEmpty();
        Directory.Exists(Path.Combine(home, "sources")).ShouldBeFalse();
        Directory.Exists(Path.Combine(home, ".twig")).ShouldBeFalse();
    }

    [Fact]
    public async Task ProjectServerContinuationAndTeamOffsetAreNotSilentlyTruncated()
    {
        var calls = new List<string>();
        var setup = await Setup((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            calls.Add(url);
            return Task.FromResult(url.Contains("/teams", StringComparison.Ordinal)
                ? Json(url.Contains("$skip=0", StringComparison.Ordinal)
                    ? $$"""{"value":[{"id":"{{TeamId}}","name":"Team"}]}""" : """{"value":[]}""")
                : url.Contains("continuationToken=", StringComparison.Ordinal)
                    ? Json($$"""{"value":[{"id":"{{OtherId}}","name":"Next project"}]}""")
                    : url.Contains("$skip=2", StringComparison.Ordinal) ? Json("""{"value":[]}""")
                    : Json($$"""{"value":[{"id":"{{ProjectId}}","name":"Project"}]}""", "opaque +/="));
        });
        var projects = new NativeAdoDiscoveryRequest(new(enrollment, "projects", "fixture"), Limit: 1);
        var first = (await setup.DiscoverAdoAsync(projects)).Value;
        first.Complete.ShouldBeFalse();
        var middle = (await setup.DiscoverAdoAsync(projects with { Continuation = first.Continuation })).Value;
        middle.Complete.ShouldBeFalse();
        var last = (await setup.DiscoverAdoAsync(projects with { Continuation = middle.Continuation })).Value;
        last.Complete.ShouldBeTrue();
        last.Choices.ShouldBeEmpty();
        calls[1].ShouldContain("continuationToken=opaque%20%2B%2F%3D");
        calls[2].ShouldContain("$skip=2");
        var teams = new NativeAdoDiscoveryRequest(new(enrollment, "teams", "fixture", ProjectId), Limit: 1);
        var team = (await setup.DiscoverAdoAsync(teams)).Value;
        team.Choices.Single().Id.ShouldBe(TeamId);
        team.Complete.ShouldBeFalse();
        (await setup.DiscoverAdoAsync(teams with { Continuation = team.Continuation })).Value.Complete.ShouldBeTrue();
        calls[4].ShouldContain("/_apis/projects/" + ProjectId + "/teams");
        calls[4].ShouldContain("$skip=1");
    }

    [Theory]
    [InlineData(403, ErrorCode.PolicyViolation, "http:403")]
    [InlineData(401, ErrorCode.Unavailable, "http:401")]
    [InlineData(203, ErrorCode.Unavailable, "http:203")]
    [InlineData(404, ErrorCode.NotFound, "http:404")]
    [InlineData(503, ErrorCode.Unavailable, "http:503")]
    public async Task ProviderRefusalCannotBecomeAnEmptySuccessfulListing(int status, ErrorCode code, string evidence)
    {
        var calls = 0;
        var setup = await Setup((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("private provider error") });
        });
        var result = await setup.DiscoverAdoAsync(new(new(enrollment, "projects", "fixture")));
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(code);
        result.Error.Evidence.ShouldContain(evidence);
        result.Error.Message.ShouldNotContain("private");
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task ChangedEnrollmentAndCrossScopeContinuationsRefuseBeforeWorkHttp()
    {
        var calls = 0;
        var setup = await Setup((_, _) =>
        {
            calls++;
            return Task.FromResult(Json($$"""{"value":[{"id":"{{ProjectId}}","name":"Project"}]}""", "next"));
        });
        var request = new NativeAdoDiscoveryRequest(new(enrollment, "projects", "fixture"));
        var page = (await setup.DiscoverAdoAsync(request)).Value;
        calls.ShouldBe(1);
        foreach (var changed in new[]
        {
            request with { Scope = request.Scope with { Organization = "different" }, Continuation = page.Continuation },
            request with { Scope = request.Scope with { Enrollment = enrollment with { ObjectId = "different" } } },
            request with { Scope = request.Scope with { Enrollment = enrollment with { Alias = "ambient" } } },
            request with { Limit = 1, Continuation = page.Continuation },
            request with { Continuation = page.Continuation + "tampered" },
        })
            (await setup.DiscoverAdoAsync(changed)).IsSuccess.ShouldBeFalse();
        var restarted = new NativeConnectionSetup(_ => throw new Xunit.Sdk.XunitException("A foreign cursor must fail before credentials"));
        (await restarted.DiscoverAdoAsync(request with { Continuation = page.Continuation })).Error.Code.ShouldBe(ErrorCode.Conflict);
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task CancellationCannotReleaseLateSuccessfulChoices()
    {
        using var cancel = new CancellationTokenSource();
        var setup = await Setup((_, _) =>
        {
            cancel.Cancel();
            return Task.FromResult(Json($$"""{"value":[{"id":"{{ProjectId}}","name":"Project"}]}"""));
        });
        var result = await setup.DiscoverAdoAsync(new(new(enrollment, "projects", "fixture")), cancel.Token);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Evidence.ShouldContain("discovery:cancelled");
    }

    [Fact]
    public async Task EnrollmentChangedDuringHttpWithholdsTheSuccessfulPage()
    {
        var setup = await Setup(async (_, _) =>
        {
            await bindings!.RegisterAadIdentityAsync("chosen", new()
            {
                RefreshToken = "fixture-refresh", ClientId = "fixture-client", TenantId = "tenant",
                ObjectId = "principal", AuthorityHost = "login.microsoftonline.com", UserPrincipalName = "changed",
            });
            return Json($$"""{"value":[{"id":"{{ProjectId}}","name":"Project"}]}""");
        });
        var result = await setup.DiscoverAdoAsync(new(new(enrollment, "projects", "fixture")));
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.Conflict);
    }

    [Fact]
    public async Task WrongPrincipalTokenIsRefusedBeforeAnyProviderHttp()
    {
        var calls = 0;
        var setup = await Setup((_, _) =>
        {
            calls++;
            return Task.FromResult(Json("""{"value":[]}"""));
        });
        var wrong = "header." + Convert.ToBase64String(Encoding.UTF8.GetBytes(
            """{"aud":"499b84ac-1321-427f-aa17-267ca6975798","tid":"tenant","oid":"different","iss":"issuer","exp":4102444800}"""))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".signature";
        refresher.TryRefreshAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((wrong, null, false));
        var result = await setup.DiscoverAdoAsync(new(new(enrollment, "projects", "fixture")));
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.PolicyViolation);
        calls.ShouldBe(0);
    }

    [Fact]
    public async Task NativeDeadlineCancelsAnUnresponsiveProviderWithoutRetry()
    {
        var calls = 0;
        var setup = await Setup(async (_, ct) =>
        {
            calls++;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Json("""{"value":[]}""");
        });
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var result = await setup.DiscoverAdoAsync(new(new(enrollment, "projects", "fixture")));
        result.IsSuccess.ShouldBeFalse();
        result.Error.Evidence.ShouldContain("discovery:timeout");
        elapsed.Elapsed.ShouldBeGreaterThan(TimeSpan.FromSeconds(19));
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        calls.ShouldBe(1);
    }

    [Fact]
    public async Task MalformedOversizedAndRepeatedContinuationResponsesFailWithoutPartialChoices()
    {
        var response = Json("""{"value":[]}""", "same");
        var setup = await Setup((_, _) => Task.FromResult(response));
        var request = new NativeAdoDiscoveryRequest(new(enrollment, "projects", "fixture"));
        var first = (await setup.DiscoverAdoAsync(request)).Value;
        response = Json("""{"value":[]}""", "same");
        (await setup.DiscoverAdoAsync(request with { Continuation = first.Continuation })).Error.Code.ShouldBe(ErrorCode.Partial);
        response = Json(new string(' ', 1024 * 1024 + 1));
        (await setup.DiscoverAdoAsync(request)).Error.Code.ShouldBe(ErrorCode.Partial);
        response = Json("""{"value":[{"name":"missing ID"}]}""");
        (await setup.DiscoverAdoAsync(request)).Error.Code.ShouldBe(ErrorCode.Unavailable);
        response = Json("""{"value":[null]}""");
        (await setup.DiscoverAdoAsync(request)).Error.Code.ShouldBe(ErrorCode.Unavailable);
        response = Json("""{"value":[],"count":5}""");
        (await setup.DiscoverAdoAsync(request)).Error.Code.ShouldBe(ErrorCode.Partial);
        response = Json("""{"value":[]}""");
        var empty = (await setup.DiscoverAdoAsync(request)).Value;
        empty.Complete.ShouldBeTrue();
        empty.Choices.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("projects", "https://evil.test", null, 50)]
    [InlineData("organizations", "fixture", null, 50)]
    [InlineData("teams", "fixture", "project-name", 50)]
    [InlineData("projects", "fixture", null, 101)]
    public async Task InvalidTargetsAreRefusedBeforeResolvingAnyCredential(string kind, string? organization, string? project, int limit)
    {
        var setup = new NativeConnectionSetup(_ => throw new Xunit.Sdk.XunitException("Must not resolve credentials"));
        var identity = new NativeAadEnrollment("named", "identity", "tenant", "object", "issuer", "authority", null);
        var result = await setup.DiscoverAdoAsync(new(new(identity, kind, organization, project), limit));
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(ErrorCode.InvalidInput);
    }

    private static HttpResponseMessage Json(string body, string? continuation = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (continuation is not null) response.Headers.Add("x-ms-continuationtoken", continuation);
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
    public void Dispose()
    {
        bindings?.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(home)) Directory.Delete(home, recursive: true);
    }
}
