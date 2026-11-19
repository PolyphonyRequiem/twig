using Bench.Infrastructure;
using Shouldly;
using Twig.Core;
using Xunit;

namespace Twig.Daemon.Tests.Native;

public sealed class NativeMetadataRefreshTests
{
    [Fact]
    public async Task RefreshIsDueAtFifteenMinutesAndSuccessfulObservationSurvivesRestart()
    {
        var start = DateTimeOffset.UtcNow;
        await using var fixture = await MetadataFixture.Start(start);
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(15).AddTicks(-1), fixture.Token)).Value.ShouldBeFalse();
        fixture.Gateway.SchemaCalls.ShouldBe(1);
        fixture.Gateway.Type = "TwigDelivery.Epic";
        fixture.Gateway.ObservedAt = start.AddMinutes(15);
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(15), fixture.Token)).Value.ShouldBeTrue();
        var restarted = new NativeSourceAdapter(fixture.Cache, fixture.Gateway, fixture.Changes);
        var observed = (await restarted.ReadAsync(fixture.Source, fixture.Token)).Value;
        observed.Binding.ShouldBe(fixture.Source.Binding);
        observed.Schema.Types.Single().Reference.ShouldBe("TwigDelivery.Epic");
        observed.SchemaObservedAt.ShouldBe(start.AddMinutes(15));
        observed.Snapshot.Items.Single().Key.ShouldBe(fixture.Gateway.Item.Key);
        (await restarted.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(16), fixture.Token)).Value.ShouldBeFalse();
        fixture.Gateway.SchemaCalls.ShouldBe(2);
        fixture.Gateway.SchemaExpectations.ShouldAllBe(value => value == fixture.Gateway.Stamp);
    }

    [Fact]
    public async Task TemporaryFailureRetainsDefinitionsAndTimestampAndSpacesRetriesWithoutAdmittingUnknownSources()
    {
        var start = DateTimeOffset.UtcNow;
        await using var fixture = await MetadataFixture.Start(start);
        var bytes = await File.ReadAllBytesAsync(fixture.CachePath, fixture.Token);
        fixture.Gateway.Failure = new(ErrorCode.Unavailable, "Fixture provider temporarily unavailable.");
        var failed = await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(15), fixture.Token);
        failed.IsSuccess.ShouldBeFalse();
        failed.Error.Code.ShouldBe(ErrorCode.Unavailable);
        (await File.ReadAllBytesAsync(fixture.CachePath, fixture.Token)).ShouldBe(bytes);
        var retained = (await fixture.Adapter.ReadAsync(fixture.Source, fixture.Token)).Value;
        retained.SchemaObservedAt.ShouldBe(start);
        retained.Schema.Types.Single().Reference.ShouldBe("Hyperbright.Epic");
        retained.Snapshot.Items.Single().Key.ShouldBe(fixture.Gateway.Item.Key);
        fixture.Gateway.Failure = null;
        fixture.Gateway.Type = "TwigDelivery.Epic";
        fixture.Gateway.ObservedAt = start.AddMinutes(30);
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(30).AddTicks(-1), fixture.Token)).Value.ShouldBeFalse();
        fixture.Gateway.SchemaCalls.ShouldBe(2);
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(30), fixture.Token)).Value.ShouldBeTrue();
        var unknownDirectory = Path.Combine(fixture.Root, "unadmitted");
        var unknown = new NativeSourceAdapter(unknownDirectory, fixture.Gateway, fixture.Changes);
        (await unknown.RefreshMetadataAsync(fixture.Workspace, start.AddHours(1), fixture.Token)).Value.ShouldBeFalse();
        Directory.Exists(unknownDirectory).ShouldBeFalse();
        Directory.CreateDirectory(unknownDirectory);
        File.Copy(fixture.CachePath, Path.Combine(unknownDirectory, Path.GetFileName(fixture.CachePath)));
        (await unknown.RefreshMetadataAsync(fixture.Workspace, start.AddHours(1), fixture.Token))
            .Error.Code.ShouldBe(ErrorCode.StorageUnavailable);
        File.Exists(Path.Combine(unknownDirectory, Path.GetFileName(fixture.CheckPath))).ShouldBeFalse();
        fixture.Gateway.SchemaCalls.ShouldBe(3);
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace with { StoreInstance = Guid.NewGuid() },
            start.AddHours(1), fixture.Token)).Error.Code.ShouldBe(ErrorCode.Conflict);
        fixture.Gateway.SchemaCalls.ShouldBe(3);
        fixture.Gateway.SchemaExpectations.ShouldAllBe(value => value == fixture.Gateway.Stamp);
    }

    [Theory]
    [InlineData(ErrorCode.PolicyViolation, "")]
    [InlineData(ErrorCode.Unavailable, "http:401")]
    [InlineData(ErrorCode.Unavailable, "http:403")]
    [InlineData(ErrorCode.Conflict, "")]
    public async Task DeniedOrChangedSourceCannotBeReauthorizedByLaterScheduledSuccess(ErrorCode code, string evidence)
    {
        var start = DateTimeOffset.UtcNow;
        await using var fixture = await MetadataFixture.Start(start);
        var bytes = await File.ReadAllBytesAsync(fixture.CachePath, fixture.Token);
        fixture.Gateway.Failure = new(code, "Fixture refusal.") { Evidence = evidence.Length == 0 ? [] : [evidence] };
        (await fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(15), fixture.Token)).Error.Code.ShouldBe(code);
        (await File.ReadAllTextAsync(fixture.CheckPath, fixture.Token)).ShouldContain("\npending\n");
        fixture.Gateway.Failure = null;
        var restarted = new NativeSourceAdapter(fixture.Cache, fixture.Gateway, fixture.Changes);
        (await restarted.RefreshMetadataAsync(fixture.Workspace, start.AddHours(1), fixture.Token)).Value.ShouldBeFalse();
        (await restarted.ReadAsync(fixture.Source, fixture.Token)).Error.Code.ShouldBe(ErrorCode.PolicyViolation);
        (await File.ReadAllBytesAsync(fixture.CachePath, fixture.Token)).ShouldBe(bytes);
        fixture.Gateway.SchemaCalls.ShouldBe(2);
        fixture.Gateway.Type = "TwigDelivery.Epic";
        fixture.Gateway.ObservedAt = start.AddHours(1);
        (await restarted.RecheckAsync(fixture.Source, fixture.Token)).IsSuccess.ShouldBeTrue();
        (await restarted.ReadAsync(fixture.Source, fixture.Token)).Value.Schema.Types.Single().Reference.ShouldBe("TwigDelivery.Epic");
        (await File.ReadAllTextAsync(fixture.CheckPath, fixture.Token)).ShouldContain("\nallowed\n");
        fixture.Gateway.SchemaCalls.ShouldBe(3);
    }

    [Fact]
    public async Task CancellationLeavesDurablePendingCheckAndCannotBeReauthorizedByScheduledWork()
    {
        var start = DateTimeOffset.UtcNow;
        await using var fixture = await MetadataFixture.Start(start);
        var bytes = await File.ReadAllBytesAsync(fixture.CachePath, fixture.Token);
        fixture.Gateway.DiscoveryBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(fixture.Token);
        var refresh = fixture.Adapter.RefreshMetadataAsync(fixture.Workspace, start.AddMinutes(15), cancel.Token);
        try { await fixture.Gateway.DiscoveryEntered.Task.WaitAsync(fixture.Token); }
        finally { cancel.Cancel(); }
        await Should.ThrowAsync<OperationCanceledException>(() => refresh);
        (await File.ReadAllTextAsync(fixture.CheckPath, fixture.Token)).ShouldContain("\npending\n");
        fixture.Gateway.DiscoveryBarrier = null;
        var restarted = new NativeSourceAdapter(fixture.Cache, fixture.Gateway, fixture.Changes);
        (await restarted.RefreshMetadataAsync(fixture.Workspace, start.AddHours(1), fixture.Token)).Value.ShouldBeFalse();
        (await restarted.ReadAsync(fixture.Source, fixture.Token)).Error.Code.ShouldBe(ErrorCode.PolicyViolation);
        (await File.ReadAllBytesAsync(fixture.CachePath, fixture.Token)).ShouldBe(bytes);
        fixture.Gateway.SchemaCalls.ShouldBe(2);
        (await restarted.RecheckAsync(fixture.Source, fixture.Token)).IsSuccess.ShouldBeTrue();
        (await restarted.ReadAsync(fixture.Source, fixture.Token)).IsSuccess.ShouldBeTrue();
        fixture.Gateway.SchemaCalls.ShouldBe(3);
    }
}
