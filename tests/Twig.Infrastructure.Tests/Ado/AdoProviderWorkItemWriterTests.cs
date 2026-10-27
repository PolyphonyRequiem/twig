using System.Reflection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Twig.Domain.Aggregates;
using Twig.Domain.Capabilities;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Ado;
using Twig.Infrastructure.Ado.Exceptions;
using Xunit;

namespace Twig.Infrastructure.Tests.Ado;

/// <summary>
/// Feature #1178 AC (a): the provider mutation port carries an opaque revision precondition,
/// and the ADO adapter refuses a missing, foreign or non-canonical one before any transport
/// call while passing a canonical one through to ADO unchanged.
/// </summary>
public sealed class AdoProviderWorkItemWriterTests
{
    private static readonly IReadOnlyList<FieldChange> Changes =
        [new FieldChange("System.State", OldValue: null, NewValue: "Doing")];

    private readonly IAdoWorkItemService _ado = Substitute.For<IAdoWorkItemService>();
    private readonly IRevisionBoundAdoWorkItemService _revisionBound = Substitute.For<IRevisionBoundAdoWorkItemService>();
    private readonly AdoProviderWorkItemWriter _writer;

    public AdoProviderWorkItemWriterTests() => _writer = new AdoProviderWorkItemWriter(_ado, _revisionBound);

    private static ProviderRevisionPrecondition Ado(string token)
        => new(ProviderCapabilityCatalog.AdoProviderKind, new ProviderRevisionToken(token));

    // ── refusals: nothing reaches ADO ───────────────────────────────────────

    [Fact]
    public async Task Patch_NullPrecondition_IsRefusedAsMissing_WithNoAdoCall()
    {
        var ex = await Should.ThrowAsync<ProviderRevisionPreconditionRefusedException>(
            () => _writer.PatchAsync(42, Changes, null!, CancellationToken.None));

        ex.Reason.ShouldBe(ProviderRevisionPreconditionRefusal.Missing);
        ex.Message.ShouldStartWith("revision-precondition-refused:");
        ex.Message.ShouldEndWith("nothing was sent.");
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData(ProviderCapabilityCatalog.GitHubProviderKind)]
    [InlineData("ADO")]
    [InlineData("Ado")]
    [InlineData("ado ")]
    public async Task Patch_ForeignProviderKind_IsRefused_WithNoAdoCall(string kind)
    {
        var foreign = new ProviderRevisionPrecondition(kind, new ProviderRevisionToken("7"));

        var ex = await Should.ThrowAsync<ProviderRevisionPreconditionRefusedException>(
            () => _writer.PatchAsync(42, Changes, foreign, CancellationToken.None));

        ex.Reason.ShouldBe(ProviderRevisionPreconditionRefusal.ForeignProvider);
        ex.AdapterKind.ShouldBe(ProviderCapabilityCatalog.AdoProviderKind);
        ex.Message.ShouldStartWith("revision-precondition-refused:");
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("05")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("1,000")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("W/\"etag\"")]
    [InlineData("99999999999")]
    public async Task Patch_NonCanonicalToken_IsRefusedAsMalformed_WithNoAdoCall_AndNoEcho(string token)
    {
        var ex = await Should.ThrowAsync<ProviderRevisionPreconditionRefusedException>(
            () => _writer.PatchAsync(42, Changes, Ado(token), CancellationToken.None));

        ex.Reason.ShouldBe(ProviderRevisionPreconditionRefusal.MalformedToken);
        ex.Message.ShouldStartWith("revision-precondition-refused:");
        ex.Message.ShouldNotContain(token.Trim());
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    // ── canonical pass-through ──────────────────────────────────────────────

    [Fact]
    public async Task Patch_CanonicalToken_ReachesAdoUnchanged_AndEncodesTheWrittenRevision()
    {
        _ado.PatchAsync(42, Changes, 7, Arg.Any<CancellationToken>()).Returns(8);
        using var cts = new CancellationTokenSource();

        var written = await _writer.PatchAsync(42, Changes, Ado("7"), cts.Token);

        written.ShouldBe(new ProviderRevisionToken("8"));
        var call = _ado.ReceivedCalls().ShouldHaveSingleItem();
        call.GetMethodInfo().Name.ShouldBe(nameof(IAdoWorkItemService.PatchAsync));
        var args = call.GetArguments();
        args[0].ShouldBe(42);
        args[1].ShouldBeSameAs(Changes);
        args[2].ShouldBe(7);
        args[3].ShouldBe(cts.Token);
    }

    [Fact]
    public async Task Patch_AdoConflict_PropagatesUnchanged()
    {
        _ado.PatchAsync(42, Changes, 7, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(9, "conflict"));

        var ex = await Should.ThrowAsync<AdoConflictException>(
            () => _writer.PatchAsync(42, Changes, Ado("7"), CancellationToken.None));

        ex.ServerRevision.ShouldBe(9);
    }

    [Fact]
    public async Task Patch_AdoReturnsNoRevision_IsPostTransportFormatError_NotARefusal()
    {
        // The write was sent, so this must not look like a determinate pre-transport refusal.
        _ado.PatchAsync(42, Changes, 7, Arg.Any<CancellationToken>()).Returns(0);

        var ex = await Should.ThrowAsync<FormatException>(
            () => _writer.PatchAsync(42, Changes, Ado("7"), CancellationToken.None));

        ex.ShouldNotBeOfType<ProviderRevisionPreconditionRefusedException>();
        _ado.ReceivedCalls().ShouldHaveSingleItem();
    }

    public static TheoryData<string, string?, string?, ProviderRevisionPreconditionRefusal> StrictRefusals()
    {
        var cases = new TheoryData<string, string?, string?, ProviderRevisionPreconditionRefusal>();
        foreach (var operation in new[] { "add", "remove", "delete" })
        {
            cases.Add(operation, null, null, ProviderRevisionPreconditionRefusal.Missing);
            cases.Add(operation, "github", "7", ProviderRevisionPreconditionRefusal.ForeignProvider);
            cases.Add(operation, "ado", "07", ProviderRevisionPreconditionRefusal.MalformedToken);
            cases.Add(operation, "ado", "0", ProviderRevisionPreconditionRefusal.MalformedToken);
        }
        return cases;
    }

    [Theory]
    [MemberData(nameof(StrictRefusals))]
    public async Task StrictMutation_InvalidPrecondition_RefusesBeforeTransport(
        string operation, string? provider, string? token, ProviderRevisionPreconditionRefusal reason)
    {
        var expected = provider is null ? null
            : new ProviderRevisionPrecondition(provider, new ProviderRevisionToken(token!));

        var ex = await Should.ThrowAsync<ProviderRevisionPreconditionRefusedException>(
            () => StrictMutationAsync(operation, expected!, CancellationToken.None));

        ex.Reason.ShouldBe(reason);
        _ado.ReceivedCalls().ShouldBeEmpty();
        _revisionBound.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task StrictLink_CanonicalPrecondition_PreservesArgumentsAndWrittenRevision(string operation)
    {
        _revisionBound.AddLinkAtRevisionAsync(42, "relation", 43, 7, Arg.Any<CancellationToken>()).Returns(8);
        _revisionBound.RemoveLinkAtRevisionAsync(42, "relation", 43, 7, Arg.Any<CancellationToken>()).Returns(8);
        _revisionBound.ClearReceivedCalls();
        using var cts = new CancellationTokenSource();

        var written = operation == "add"
            ? await _writer.AddLinkAtRevisionAsync(42, "relation", 43, Ado("7"), cts.Token)
            : await _writer.RemoveLinkAtRevisionAsync(42, "relation", 43, Ado("7"), cts.Token);

        written.Value.ShouldBe("8");
        var call = _revisionBound.ReceivedCalls().ShouldHaveSingleItem();
        call.GetMethodInfo().Name.ShouldBe(operation == "add"
            ? nameof(IRevisionBoundAdoWorkItemService.AddLinkAtRevisionAsync)
            : nameof(IRevisionBoundAdoWorkItemService.RemoveLinkAtRevisionAsync));
        call.GetArguments().ShouldBe(new object[] { 42, "relation", 43, 7, cts.Token });
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task StrictDelete_CanonicalPrecondition_PreservesArguments()
    {
        using var cts = new CancellationTokenSource();

        await _writer.DeleteAtRevisionAsync(42, Ado("7"), cts.Token);

        await _revisionBound.Received(1).DeleteAtRevisionAsync(42, 7, cts.Token);
        _revisionBound.ReceivedCalls().ShouldHaveSingleItem();
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("delete")]
    public async Task StrictMutation_Conflict_PropagatesWithoutRetry(string operation)
    {
        var conflict = new AdoConflictException(8, "conflict");
        _revisionBound.AddLinkAtRevisionAsync(42, "relation", 43, 7, Arg.Any<CancellationToken>())
            .ThrowsAsync(conflict);
        _revisionBound.RemoveLinkAtRevisionAsync(42, "relation", 43, 7, Arg.Any<CancellationToken>())
            .ThrowsAsync(conflict);
        _revisionBound.DeleteAtRevisionAsync(42, 7, Arg.Any<CancellationToken>()).ThrowsAsync(conflict);
        _revisionBound.ClearReceivedCalls();

        var ex = await Should.ThrowAsync<AdoConflictException>(
            () => StrictMutationAsync(operation, Ado("7"), CancellationToken.None));

        ex.ShouldBeSameAs(conflict);
        _revisionBound.ReceivedCalls().ShouldHaveSingleItem();
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    public async Task StrictLink_MissingWrittenRevision_IsIndeterminateNotPretransportRefusal(string operation)
    {
        _revisionBound.AddLinkAtRevisionAsync(42, "relation", 43, 7, Arg.Any<CancellationToken>()).Returns(0);
        _revisionBound.RemoveLinkAtRevisionAsync(42, "relation", 43, 7, Arg.Any<CancellationToken>()).Returns(0);
        _revisionBound.ClearReceivedCalls();

        await Should.ThrowAsync<FormatException>(
            () => StrictMutationAsync(operation, Ado("7"), CancellationToken.None));

        _revisionBound.ReceivedCalls().ShouldHaveSingleItem();
    }

    private Task StrictMutationAsync(string operation, ProviderRevisionPrecondition expected, CancellationToken ct)
        => operation switch
        {
            "add" => _writer.AddLinkAtRevisionAsync(42, "relation", 43, expected, ct),
            "remove" => _writer.RemoveLinkAtRevisionAsync(42, "relation", 43, expected, ct),
            "delete" => _writer.DeleteAtRevisionAsync(42, expected, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };

    // ── codec ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Codec_FromCacheRevision_NotPositive_IsRefusedAsMissing(int revision)
    {
        var ex = Should.Throw<ProviderRevisionPreconditionRefusedException>(
            () => AdoProviderRevisionCodec.Instance.FromCacheRevision(revision));

        ex.Reason.ShouldBe(ProviderRevisionPreconditionRefusal.Missing);
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(7, "7")]
    [InlineData(int.MaxValue, "2147483647")]
    public void Codec_CanonicalRevision_RoundTrips(int revision, string token)
    {
        var codec = AdoProviderRevisionCodec.Instance;

        var precondition = codec.FromCacheRevision(revision);

        precondition.ProviderKind.ShouldBe(ProviderCapabilityCatalog.AdoProviderKind);
        precondition.Expected.Value.ShouldBe(token);
        codec.ToCacheRevision(precondition.Expected).ShouldBe(revision);
        codec.DecodePrecondition(precondition).ShouldBe(revision);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("07")]
    [InlineData("abc")]
    public void Codec_ToCacheRevision_NonCanonical_IsFormatError(string token)
        => Should.Throw<FormatException>(
            () => AdoProviderRevisionCodec.Instance.ToCacheRevision(new ProviderRevisionToken(token)));

    // ── conflict-retry over the port: revision 0 never reaches ADO ──────────

    [Fact]
    public async Task ConflictRetry_OverThePort_RevisionZero_IsRefused_WithNoPatchFetchOrRetry()
    {
        var ex = await Should.ThrowAsync<ProviderRevisionPreconditionRefusedException>(
            () => ConflictRetryHelper.PatchWithRetryAsync(
                (IProviderWorkItemWriter)_writer, 42, Changes, 0, CancellationToken.None));

        ex.Reason.ShouldBe(ProviderRevisionPreconditionRefusal.Missing);
        await _ado.DidNotReceiveWithAnyArgs().PatchAsync(default, default!, default, default);
        await _ado.DidNotReceiveWithAnyArgs().FetchAsync(default, default);
        _ado.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task ConflictRetry_OverThePort_CanonicalRevision_PassesThroughAndRetriesOnce()
    {
        _ado.PatchAsync(42, Changes, 3, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AdoConflictException(4, "conflict"));
        var fresh = new WorkItem { Id = 42, Title = "T" };
        fresh.MarkSynced(4);
        _ado.FetchAsync(42, Arg.Any<CancellationToken>()).Returns(fresh);
        _ado.PatchAsync(42, Changes, 4, Arg.Any<CancellationToken>()).Returns(5);

        var newRevision = await ConflictRetryHelper.PatchWithRetryAsync(
            (IProviderWorkItemWriter)_writer, 42, Changes, 3, CancellationToken.None);

        newRevision.ShouldBe(5);
        await _ado.Received(1).PatchAsync(42, Changes, 3, Arg.Any<CancellationToken>());
        await _ado.Received(1).PatchAsync(42, Changes, 4, Arg.Any<CancellationToken>());
    }

    // ── port shape ──────────────────────────────────────────────────────────

    [Fact]
    public void Port_CarriesNoIntegerRevision_AndReturnsAnOpaqueToken()
    {
        var methods = typeof(IProviderWorkItemWriter).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        methods.SelectMany(m => m.GetParameters())
            .Where(p => p.Name!.Contains("revision", StringComparison.OrdinalIgnoreCase))
            .ShouldAllBe(p => p.ParameterType == typeof(ProviderRevisionPrecondition));

        var patch = typeof(IProviderWorkItemWriter).GetMethod(nameof(IProviderWorkItemWriter.PatchAsync))!;
        patch.ReturnType.ShouldBe(typeof(Task<ProviderRevisionToken>));
        patch.GetParameters()[2].ParameterType.ShouldBe(typeof(ProviderRevisionPrecondition));
    }

    [Fact]
    public void AdoClient_IsNotItselfThePort()
        => typeof(IProviderWorkItemWriter).IsAssignableFrom(typeof(IAdoWorkItemService)).ShouldBeFalse();
}
