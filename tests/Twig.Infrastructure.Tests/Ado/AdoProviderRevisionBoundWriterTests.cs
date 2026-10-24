using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
using Twig.Domain.Services.Navigation;
using Twig.Domain.Services.Seed;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Feature #1178 AC (b): proposal relationship and delete operations dispatch through the
/// provider-neutral <see cref="IProviderRevisionBoundWriter"/> port, and publish-seed through
/// <see cref="IProviderSeedPublisher"/>. The ADO adapter maps the closed proposal relation
/// vocabulary to ADO link types, refuses a missing, foreign or non-canonical precondition
/// before transport, and otherwise reaches ADO byte-identically.
/// </summary>
public sealed class AdoProviderRevisionBoundWriterTests
{
    private const string OrgUrl = "https://dev.azure.com/testorg";
    private const string Project = "testproject";

    private readonly IRevisionBoundAdoWorkItemService _ado = Substitute.For<IRevisionBoundAdoWorkItemService>();
    private readonly AdoProviderRevisionBoundWriter _writer;

    public AdoProviderRevisionBoundWriterTests() => _writer = new AdoProviderRevisionBoundWriter(_ado);

    private static ProviderRevisionPrecondition Ado(string token)
        => new(ProviderCapabilityCatalog.AdoProviderKind, new ProviderRevisionToken(token));

    public enum Kind { Add, Remove, Delete }

    private async Task Invoke(Kind kind, ProviderRevisionPrecondition precondition)
    {
        switch (kind)
        {
            case Kind.Add:
                await _writer.AddRelationshipAsync(42, ProposalRelation.Related, 99, precondition, CancellationToken.None);
                break;
            case Kind.Remove:
                await _writer.RemoveRelationshipAsync(42, ProposalRelation.Related, 99, precondition, CancellationToken.None);
                break;
            default:
                await _writer.DeleteAsync(42, precondition, CancellationToken.None);
                break;
        }
    }

    // ── refusals: nothing reaches ADO ───────────────────────────────────────

    [Theory]
    [InlineData(Kind.Add)]
    [InlineData(Kind.Remove)]
    [InlineData(Kind.Delete)]
    public async Task NullPrecondition_IsRefusedAsMissing_WithNoAdoCall(Kind kind)
    {
        var ex = await Should.ThrowAsync<ProviderRevisionPreconditionRefusedException>(() => Invoke(kind, null!));

        ex.Reason.ShouldBe(ProviderRevisionPreconditionRefusal.Missing);
        ex.Message.ShouldStartWith("revision-precondition-refused:");
        ex.Message.ShouldEndWith("nothing was sent.");
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Kind.Add, ProviderCapabilityCatalog.GitHubProviderKind)]
    [InlineData(Kind.Add, "ADO")]
    [InlineData(Kind.Remove, ProviderCapabilityCatalog.GitHubProviderKind)]
    [InlineData(Kind.Remove, "Ado")]
    [InlineData(Kind.Delete, ProviderCapabilityCatalog.GitHubProviderKind)]
    [InlineData(Kind.Delete, "ado ")]
    public async Task ForeignProviderKind_IsRefused_WithNoAdoCall(Kind kind, string providerKind)
    {
        var foreign = new ProviderRevisionPrecondition(providerKind, new ProviderRevisionToken("7"));

        var ex = await Should.ThrowAsync<ProviderRevisionPreconditionRefusedException>(() => Invoke(kind, foreign));

        ex.Reason.ShouldBe(ProviderRevisionPreconditionRefusal.ForeignProvider);
        ex.AdapterKind.ShouldBe(ProviderCapabilityCatalog.AdoProviderKind);
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(Kind.Add, "0")]
    [InlineData(Kind.Add, "05")]
    [InlineData(Kind.Add, "abc")]
    [InlineData(Kind.Remove, "-1")]
    [InlineData(Kind.Remove, "99999999999")]
    [InlineData(Kind.Remove, "W/\"etag\"")]
    [InlineData(Kind.Delete, "0")]
    [InlineData(Kind.Delete, "+5")]
    [InlineData(Kind.Delete, "1e3")]
    public async Task NonCanonicalToken_IsRefusedAsMalformed_WithNoAdoCall_AndNoEcho(Kind kind, string token)
    {
        var ex = await Should.ThrowAsync<ProviderRevisionPreconditionRefusedException>(() => Invoke(kind, Ado(token)));

        ex.Reason.ShouldBe(ProviderRevisionPreconditionRefusal.MalformedToken);
        ex.Message.ShouldNotContain(token.Trim());
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    // ── canonical pass-through: ADO receives exactly what it received before ─

    public static TheoryData<string, string> RelationToAdoLinkType() => new()
    {
        { "parent", "System.LinkTypes.Hierarchy-Reverse" },
        { "child", "System.LinkTypes.Hierarchy-Forward" },
        { "related", "System.LinkTypes.Related" },
        { "predecessor", "System.LinkTypes.Dependency-Reverse" },
        { "successor", "System.LinkTypes.Dependency-Forward" },
    };

    [Theory]
    [MemberData(nameof(RelationToAdoLinkType))]
    public async Task AddRelationship_CanonicalToken_ReachesAdoWithTheMappedLinkType(string relation, string adoLinkType)
    {
        ProposalRelation.TryParse(relation, out var parsed).ShouldBeTrue();
        _ado.AddLinkAtRevisionAsync(42, adoLinkType, 99, 7, Arg.Any<CancellationToken>()).Returns(8);
        using var cts = new CancellationTokenSource();

        var written = await _writer.AddRelationshipAsync(42, parsed, 99, Ado("7"), cts.Token);

        written.ShouldBe(new ProviderRevisionToken("8"));
        var call = _ado.ReceivedCalls().ShouldHaveSingleItem();
        call.GetMethodInfo().Name.ShouldBe(nameof(IRevisionBoundAdoWorkItemService.AddLinkAtRevisionAsync));
        call.GetArguments().ShouldBe([42, adoLinkType, 99, 7, cts.Token]);
    }

    [Theory]
    [MemberData(nameof(RelationToAdoLinkType))]
    public async Task RemoveRelationship_CanonicalToken_ReachesAdoWithTheMappedLinkType(string relation, string adoLinkType)
    {
        ProposalRelation.TryParse(relation, out var parsed).ShouldBeTrue();
        _ado.RemoveLinkAtRevisionAsync(42, adoLinkType, 99, 7, Arg.Any<CancellationToken>()).Returns(8);
        using var cts = new CancellationTokenSource();

        var written = await _writer.RemoveRelationshipAsync(42, parsed, 99, Ado("7"), cts.Token);

        written.ShouldBe(new ProviderRevisionToken("8"));
        var call = _ado.ReceivedCalls().ShouldHaveSingleItem();
        call.GetMethodInfo().Name.ShouldBe(nameof(IRevisionBoundAdoWorkItemService.RemoveLinkAtRevisionAsync));
        call.GetArguments().ShouldBe([42, adoLinkType, 99, 7, cts.Token]);
    }

    [Fact]
    public async Task Delete_CanonicalToken_ReachesAdoUnchanged()
    {
        using var cts = new CancellationTokenSource();

        await _writer.DeleteAsync(42, Ado("17"), cts.Token);

        var call = _ado.ReceivedCalls().ShouldHaveSingleItem();
        call.GetMethodInfo().Name.ShouldBe(nameof(IRevisionBoundAdoWorkItemService.DeleteAtRevisionAsync));
        call.GetArguments().ShouldBe([42, 17, cts.Token]);
    }

    [Fact]
    public async Task AdoConflict_PropagatesUnchanged()
    {
        _ado.AddLinkAtRevisionAsync(42, "System.LinkTypes.Related", 99, 7, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(9, "conflict"));

        var ex = await Should.ThrowAsync<AdoConflictException>(
            () => _writer.AddRelationshipAsync(42, ProposalRelation.Related, 99, Ado("7"), CancellationToken.None));

        ex.ServerRevision.ShouldBe(9);
    }

    [Fact]
    public async Task AdoRelationNotFound_PropagatesUnchanged()
    {
        var original = new AdoRelationNotFoundException(42, "System.LinkTypes.Related", 99, 7);
        _ado.RemoveLinkAtRevisionAsync(42, "System.LinkTypes.Related", 99, 7, Arg.Any<CancellationToken>())
            .ThrowsAsync(original);

        var ex = await Should.ThrowAsync<AdoRelationNotFoundException>(
            () => _writer.RemoveRelationshipAsync(42, ProposalRelation.Related, 99, Ado("7"), CancellationToken.None));

        ex.ShouldBeSameAs(original);
    }

    [Fact]
    public async Task AdoReturnsNoRevision_IsPostTransportFormatError_NotARefusal()
    {
        _ado.AddLinkAtRevisionAsync(42, "System.LinkTypes.Related", 99, 7, Arg.Any<CancellationToken>()).Returns(0);

        var ex = await Should.ThrowAsync<FormatException>(
            () => _writer.AddRelationshipAsync(42, ProposalRelation.Related, 99, Ado("7"), CancellationToken.None));

        ex.ShouldNotBeOfType<ProviderRevisionPreconditionRefusedException>();
        _ado.ReceivedCalls().ShouldHaveSingleItem();
    }

    // ── closed vocabulary ───────────────────────────────────────────────────

    [Fact]
    public void ProposalRelation_IsExactlyTheSetAdoCanMap()
    {
        ProposalRelation.All.Select(r => r.Name).OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(LinkTypeMapper.SupportedTypes.Select(n => n.ToLowerInvariant()).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("parent")]
    [InlineData("PARENT")]
    [InlineData("Successor")]
    public void ProposalRelation_TryParse_IsCaseInsensitive_AndReturnsTheSingleton(string spelling)
    {
        ProposalRelation.TryParse(spelling, out var relation).ShouldBeTrue();
        ProposalRelation.All.ShouldContain(relation!);
        relation!.Name.ShouldBe(spelling.ToLowerInvariant());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" parent")]
    [InlineData("bogus")]
    [InlineData("System.LinkTypes.Related")]
    [InlineData("Hierarchy-Reverse")]
    public void ProposalRelation_TryParse_RefusesAnythingOutsideTheClosedSet(string? spelling)
    {
        ProposalRelation.TryParse(spelling, out var relation).ShouldBeFalse();
        relation.ShouldBeNull();
    }

    [Fact]
    public void ProposalRelation_HasNoPublicConstructor()
        => typeof(ProposalRelation).GetConstructors(BindingFlags.Public | BindingFlags.Instance).ShouldBeEmpty();

    // ── port shape ──────────────────────────────────────────────────────────

    [Fact]
    public void Port_CarriesOnlyOpaquePreconditions_AndNeutralRelations()
    {
        var methods = typeof(IProviderRevisionBoundWriter).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        methods.Length.ShouldBe(3);
        var parameters = methods.SelectMany(m => m.GetParameters()).ToArray();

        parameters.Where(p => p.Name!.Contains("revision", StringComparison.OrdinalIgnoreCase)
                || p.Name!.Contains("expected", StringComparison.OrdinalIgnoreCase))
            .ShouldAllBe(p => p.ParameterType == typeof(ProviderRevisionPrecondition));
        parameters.Where(p => p.Name!.Contains("relation", StringComparison.OrdinalIgnoreCase))
            .ShouldAllBe(p => p.ParameterType == typeof(ProposalRelation));
        parameters.ShouldNotContain(p => p.ParameterType == typeof(string));

        typeof(IProviderRevisionBoundWriter).GetMethod(nameof(IProviderRevisionBoundWriter.AddRelationshipAsync))!
            .ReturnType.ShouldBe(typeof(Task<ProviderRevisionToken>));
        typeof(IProviderRevisionBoundWriter).GetMethod(nameof(IProviderRevisionBoundWriter.RemoveRelationshipAsync))!
            .ReturnType.ShouldBe(typeof(Task<ProviderRevisionToken>));
    }

    [Fact]
    public void AdoClient_IsNotItselfThePort()
    {
        typeof(IProviderRevisionBoundWriter).IsAssignableFrom(typeof(IRevisionBoundAdoWorkItemService)).ShouldBeFalse();
        typeof(IProviderRevisionBoundWriter).IsAssignableFrom(typeof(AdoRestClient)).ShouldBeFalse();
    }

    [Fact]
    public void DomainPorts_NameNoAdoLinkTypes()
    {
        // The neutral surface must not carry provider spellings; only adapters may.
        foreach (var type in new[] { typeof(IProviderRevisionBoundWriter), typeof(IProviderSeedPublisher), typeof(ProposalRelation) })
        {
            var names = type.GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                .Select(m => m.Name);
            names.ShouldNotContain(n => n.Contains("LinkType", StringComparison.Ordinal));
            names.ShouldNotContain(n => n.Contains("Ado", StringComparison.Ordinal));
        }

        ProposalRelation.All.ShouldAllBe(r => !r.Name.Contains("System.", StringComparison.Ordinal));
    }

    // ── seed publisher: lazy, and dispatched through the port ──────────────

    [Fact]
    public async Task SeedPublisher_ResolvesTheOrchestratorOnlyWhenPublishing()
    {
        var resolutions = 0;
        var publisher = new AdoProviderSeedPublisher(() =>
        {
            resolutions++;
            throw new InvalidOperationException("orchestrator resolved");
        });

        resolutions.ShouldBe(0);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => publisher.PublishAsync(-3, CancellationToken.None));

        ex.Message.ShouldBe("orchestrator resolved");
        resolutions.ShouldBe(1);
    }

    [Fact]
    public void SeedPort_IsNotTheOrchestrator()
        => typeof(IProviderSeedPublisher).IsAssignableFrom(typeof(SeedPublishOrchestrator)).ShouldBeFalse();

    // ── real ADO client behind the adapter: request bytes unchanged ────────

    [Fact]
    public async Task OverRealClient_AddRelationship_SendsTheSameStrictCasRequest()
    {
        var handler = new RecordingHandler { ResponseRev = 8 };
        var writer = new AdoProviderRevisionBoundWriter(CreateClient(handler));

        var written = await writer.AddRelationshipAsync(1, ProposalRelation.Successor, 2, Ado("7"), CancellationToken.None);

        written.ShouldBe(new ProviderRevisionToken("8"));
        handler.Requests.ShouldHaveSingleItem().ShouldStartWith("PATCH ");
        handler.Requests[0].ShouldEndWith("/_apis/wit/workitems/1");
        handler.LastIfMatch.ShouldBe("7");
        using var doc = JsonDocument.Parse(handler.LastBody!);
        doc.RootElement.GetArrayLength().ShouldBe(2);
        doc.RootElement[0].GetProperty("op").GetString().ShouldBe("test");
        doc.RootElement[0].GetProperty("path").GetString().ShouldBe("/rev");
        doc.RootElement[0].GetProperty("value").GetInt32().ShouldBe(7);
        doc.RootElement[1].GetProperty("op").GetString().ShouldBe("add");
        doc.RootElement[1].GetProperty("path").GetString().ShouldBe("/relations/-");
        var value = doc.RootElement[1].GetProperty("value");
        value.GetProperty("rel").GetString().ShouldBe("System.LinkTypes.Dependency-Forward");
        value.GetProperty("url").GetString().ShouldBe($"{OrgUrl}/_apis/wit/workitems/2");
    }

    [Fact]
    public async Task OverRealClient_AddRelationship_412_IsAConflictWithoutRetry()
    {
        var handler = new RecordingHandler { StatusCode = HttpStatusCode.PreconditionFailed };
        var writer = new AdoProviderRevisionBoundWriter(CreateClient(handler));

        await Should.ThrowAsync<AdoConflictException>(
            () => writer.AddRelationshipAsync(1, ProposalRelation.Related, 2, Ado("5"), CancellationToken.None));

        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OverRealClient_Delete_SendsIfMatch_And404IsIdempotentSuccess()
    {
        var handler = new RecordingHandler { StatusCode = HttpStatusCode.NotFound };
        var writer = new AdoProviderRevisionBoundWriter(CreateClient(handler));

        await writer.DeleteAsync(999, Ado("3"), CancellationToken.None);

        handler.Requests.ShouldHaveSingleItem().ShouldStartWith("DELETE ");
        handler.Requests[0].ShouldContain("/workitems/999");
        handler.LastIfMatch.ShouldBe("3");
    }

    [Fact]
    public async Task OverRealClient_Delete_412_IsAConflict()
    {
        var handler = new RecordingHandler { StatusCode = HttpStatusCode.PreconditionFailed };
        var writer = new AdoProviderRevisionBoundWriter(CreateClient(handler));

        await Should.ThrowAsync<AdoConflictException>(
            () => writer.DeleteAsync(42, Ado("3"), CancellationToken.None));

        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(Kind.Add)]
    [InlineData(Kind.Remove)]
    [InlineData(Kind.Delete)]
    public async Task OverRealClient_Refusal_SendsNoRequest(Kind kind)
    {
        var handler = new RecordingHandler();
        var writer = new AdoProviderRevisionBoundWriter(CreateClient(handler));
        var foreign = new ProviderRevisionPrecondition(ProviderCapabilityCatalog.GitHubProviderKind, new ProviderRevisionToken("7"));

        async Task Act()
        {
            if (kind == Kind.Add)
                await writer.AddRelationshipAsync(1, ProposalRelation.Parent, 2, foreign, CancellationToken.None);
            else if (kind == Kind.Remove)
                await writer.RemoveRelationshipAsync(1, ProposalRelation.Parent, 2, foreign, CancellationToken.None);
            else
                await writer.DeleteAsync(1, foreign, CancellationToken.None);
        }

        await Should.ThrowAsync<ProviderRevisionPreconditionRefusedException>(Act);
        handler.Requests.ShouldBeEmpty();
    }

    private static AdoRestClient CreateClient(HttpMessageHandler handler)
        => new(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) },
            new FakeAuthProvider(), OrgUrl, Project, new WorkItemMapper());

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public string? LastIfMatch { get; private set; }
        public string? LastBody { get; private set; }
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public int ResponseRev { get; init; } = 2;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add($"{request.Method.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Headers.TryGetValues("If-Match", out var ifMatch))
                LastIfMatch = ifMatch.FirstOrDefault();
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);

            if (StatusCode == HttpStatusCode.OK)
            {
                var json = $"{{\"id\":1,\"rev\":{ResponseRev},\"fields\":{{\"System.WorkItemType\":\"Task\",\"System.Title\":\"Test\",\"System.State\":\"New\"}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            return new HttpResponseMessage(StatusCode) { Content = new StringContent(string.Empty, Encoding.UTF8, "application/json") };
        }
    }
}
