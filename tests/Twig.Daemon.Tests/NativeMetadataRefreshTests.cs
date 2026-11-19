using Bench.Infrastructure;
using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Daemon.Tests;

public sealed class NativeMetadataRefreshTests
{
    [Fact]
    public async Task RefreshIsDueAtFifteenMinutesAndSuccessfulObservationSurvivesRestart()
    {
        var start = DateTimeOffset.UtcNow;
        await using var fixture = await MetadataFixture.Start(start);
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(15).AddTicks(-1), default)).Value.ShouldBeFalse();
        fixture.Gateway.SchemaCalls.ShouldBe(1);
        fixture.Gateway.Type = "TwigDelivery.Epic";
        fixture.Gateway.ObservedAt = start.AddMinutes(15);
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(15), default)).Value.ShouldBeTrue();
        var restarted = new NativeSourceAdapter(fixture.Cache, fixture.Gateway, fixture.Changes);
        (await restarted.ReadAsync(fixture.Source, default)).Value.Schema.Types.Single().Reference.ShouldBe("TwigDelivery.Epic");
        (await restarted.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(16), default)).Value.ShouldBeFalse();
        fixture.Gateway.SchemaCalls.ShouldBe(2);
    }

    [Fact]
    public async Task TemporaryFailureRetainsDefinitionsAndTimestampAndSpacesRetriesWithoutAdmittingUnknownSources()
    {
        var start = DateTimeOffset.UtcNow;
        await using var fixture = await MetadataFixture.Start(start);
        fixture.Gateway.Failure = new(ErrorCode.Unavailable, "Fixture provider temporarily unavailable.") { Evidence = ["http:503"] };
        var failed = await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(15), default);
        failed.IsSuccess.ShouldBeFalse();
        failed.Error.Code.ShouldBe(ErrorCode.Unavailable);
        var retained = (await fixture.Adapter.ReadAsync(fixture.Source, default)).Value;
        retained.SchemaObservedAt.ShouldBe(start);
        retained.Schema.Types.Single().Reference.ShouldBe("Hyperbright.Epic");
        fixture.Gateway.Failure = null;
        fixture.Gateway.Type = "TwigDelivery.Epic";
        fixture.Gateway.ObservedAt = start.AddMinutes(30);
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(30).AddTicks(-1), default)).Value.ShouldBeFalse();
        fixture.Gateway.SchemaCalls.ShouldBe(2);
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(30), default)).Value.ShouldBeTrue();
        var unknown = new NativeSourceAdapter(Path.Combine(fixture.Root, "unadmitted"), fixture.Gateway, fixture.Changes);
        (await unknown.RefreshMetadataAsync(fixture.Workspace, start.AddHours(1), default)).Value.ShouldBeFalse();
        fixture.Gateway.SchemaCalls.ShouldBe(3);
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace with { StoreInstance = Guid.NewGuid() },
            start.AddHours(1), default)).Error.Code.ShouldBe(ErrorCode.Conflict);
        fixture.Gateway.SchemaCalls.ShouldBe(3);
    }

    [Theory]
    [InlineData(ErrorCode.PolicyViolation, "")]
    [InlineData(ErrorCode.Unavailable, "http:401")]
    [InlineData(ErrorCode.Unavailable, "http:403")]
    [InlineData(ErrorCode.Unavailable, "")]
    [InlineData(ErrorCode.Conflict, "")]
    public async Task DeniedOrChangedSourceCannotBeReauthorizedByLaterScheduledSuccess(ErrorCode code, string evidence)
    {
        var start = DateTimeOffset.UtcNow;
        await using var fixture = await MetadataFixture.Start(start);
        fixture.Gateway.Failure = new(code, "Fixture refusal.") { Evidence = evidence.Length == 0 ? [] : [evidence] };
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(15), default)).IsSuccess.ShouldBeFalse();
        fixture.Gateway.Failure = null;
        var restarted = new NativeSourceAdapter(fixture.Cache, fixture.Gateway, fixture.Changes);
        (await restarted.RefreshMetadataAsync(fixture.Workspace, start.AddHours(1), default)).Value.ShouldBeFalse();
        (await restarted.ReadAsync(fixture.Source, default)).IsSuccess.ShouldBeFalse();
        fixture.Gateway.SchemaCalls.ShouldBe(2);
        (await restarted.RecheckAsync(fixture.Source, default)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task CancellationLeavesDurablePendingCheckAndCannotBeReauthorizedByScheduledWork()
    {
        var start = DateTimeOffset.UtcNow;
        await using var fixture = await MetadataFixture.Start(start);
        fixture.Gateway.DiscoveryBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        using var cancel = new CancellationTokenSource();
        var refresh = fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(15), cancel.Token);
        await fixture.Gateway.DiscoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancel.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() => refresh);
        fixture.Gateway.DiscoveryBarrier = null;
        var restarted = new NativeSourceAdapter(fixture.Cache, fixture.Gateway, fixture.Changes);
        (await restarted.RefreshMetadataAsync(fixture.Workspace, start.AddHours(1), default)).Value.ShouldBeFalse();
        (await restarted.ReadAsync(fixture.Source, default)).IsSuccess.ShouldBeFalse();
        fixture.Gateway.SchemaCalls.ShouldBe(2);
    }
}
