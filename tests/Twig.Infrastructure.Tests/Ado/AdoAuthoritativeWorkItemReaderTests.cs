using NSubstitute;
using Shouldly;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Feature #1177: the ADO adapter for authoritative revision-bound snapshot reads. The opaque
/// identity and revision token must decode to exactly one ADO item and revision, belong to this
/// workspace's organisation, and the server's answer must be the one asked for.
/// </summary>
public sealed class AdoAuthoritativeWorkItemReaderTests
{
    private const string Scope = "11111111-2222-3333-4444-555555555555";
    private readonly IRevisionBoundAdoWorkItemService _revisionBound = Substitute.For<IRevisionBoundAdoWorkItemService>();

    private static ExternalWorkItemIdentity Identity(string nativeId = "42", string scope = Scope, string kind = "ado")
        => new(kind, scope, nativeId);

    private AdoAuthoritativeWorkItemReader Reader(string? scope = Scope)
        => new(_revisionBound, new ScopedProvider(scope));

    private void ServerAnswers(int id, int revision)
        => _revisionBound.FetchAtRevisionAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new WorkItemSnapshot { Id = id, Revision = revision, Title = "Fixture" });

    [Fact]
    public async Task MatchingSnapshot_IsReturnedWithTheRequestedIdentityAndToken()
    {
        ServerAnswers(42, 7);
        var identity = Identity();
        var token = new ProviderRevisionToken("7");

        var result = await Reader().ReadAtRevisionAsync(identity, token);

        result.Identity.ShouldBe(identity);
        result.Revision.ShouldBe(token);
        result.Item.Id.ShouldBe(42);
        result.Item.Revision.ShouldBe(7);
        await _revisionBound.Received(1).FetchAtRevisionAsync(42, 7, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(42, 8)]
    [InlineData(43, 7)]
    public async Task ServerAnswerForADifferentItemOrRevision_IsRefused(int answeredId, int answeredRevision)
    {
        ServerAnswers(answeredId, answeredRevision);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Reader().ReadAtRevisionAsync(Identity(), new ProviderRevisionToken("7")));

        ex.Message.ShouldContain("mismatched snapshot");
    }

    [Fact]
    public async Task UnresolvedScope_IsRefusedBeforeAnyFetch()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Reader(scope: null).ReadAtRevisionAsync(Identity(), new ProviderRevisionToken("7")));

        ex.Message.ShouldContain("unscoped");
        await _revisionBound.DidNotReceiveWithAnyArgs().FetchAtRevisionAsync(default, default, default);
    }

    [Theory]
    [InlineData("99999999-2222-3333-4444-555555555555")]
    [InlineData("11111111-2222-3333-4444-555555555555x")]
    [InlineData("11111111-2222-3333-4444-55555555555A")]
    public async Task IdentityFromAnotherOrganisation_IsRefusedBeforeAnyFetch(string otherScope)
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => Reader().ReadAtRevisionAsync(Identity(scope: otherScope), new ProviderRevisionToken("7")));

        ex.Message.ShouldContain("resource scope");
        await _revisionBound.DidNotReceiveWithAnyArgs().FetchAtRevisionAsync(default, default, default);
    }

    [Fact]
    public async Task IdentityOwnedByAnotherProvider_IsRefusedBeforeAnyFetch()
    {
        await Should.ThrowAsync<ArgumentException>(
            () => Reader().ReadAtRevisionAsync(Identity(kind: "github"), new ProviderRevisionToken("7")));

        await _revisionBound.DidNotReceiveWithAnyArgs().FetchAtRevisionAsync(default, default, default);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("1.0")]
    [InlineData("1,000")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    public async Task NonCanonicalNativeIdOrToken_IsRefusedBeforeAnyFetch(string spelling)
    {
        await Should.ThrowAsync<ArgumentException>(
            () => Reader().ReadAtRevisionAsync(Identity(nativeId: spelling), new ProviderRevisionToken("7")));
        await Should.ThrowAsync<ArgumentException>(
            () => Reader().ReadAtRevisionAsync(Identity(), new ProviderRevisionToken(spelling)));

        await _revisionBound.DidNotReceiveWithAnyArgs().FetchAtRevisionAsync(default, default, default);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(int.MaxValue)]
    public void Tokens_RoundTripCanonically(int value)
    {
        AdoProviderTokens.TryDecodePositive(AdoProviderTokens.EncodeNativeId(value), out var id).ShouldBeTrue();
        id.ShouldBe(value);
        AdoProviderTokens.TryDecodePositive(AdoProviderTokens.EncodeRevision(value).Value, out var rev).ShouldBeTrue();
        rev.ShouldBe(value);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Tokens_RefuseToEncodeNonPositiveValues(int value)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => AdoProviderTokens.EncodeNativeId(value));
        Should.Throw<ArgumentOutOfRangeException>(() => AdoProviderTokens.EncodeRevision(value));
    }

    private sealed class ScopedProvider(string? scope) : IWorkItemProvider
    {
        public ProviderCapabilityDescriptor Capabilities => ProviderCapabilityCatalog.Ado;

        public Task<string?> ResolveResourceScopeAsync(CancellationToken ct = default) => Task.FromResult(scope);
    }
}
