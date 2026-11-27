using System.Text.Json;
using Bench.Domain;
using Bench.Infrastructure;
using Twig.Server;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Server.Tests;

public sealed class EditorInputPersistenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "twig-private-inputs-" + Guid.NewGuid().ToString("N"));
    private readonly Clock clock = new();
    private readonly Guid store = Guid.NewGuid(), profile = Guid.NewGuid(), bench = Guid.NewGuid();
    private readonly NativePanelContext source;
    private readonly ViewSessions sessions;
    private readonly ViewSession session;

    public EditorInputPersistenceTests()
    {
        var stamp = new NativeSourceStamp(new(new("ado"), "collection", "destination", "identity"),
            Guid.NewGuid(), "principal", "authority");
        source = new(root, stamp, new(Guid.NewGuid(), "fixture", root, NativeSourceAdapter.Binding(stamp), true, clock.Now));
        sessions = new(root, clock);
        session = Attach(sessions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-")]
    [InlineData("12.")]
    public void IncompleteValuesDurablyRestoreAcrossNewServerAndViewWithoutRetainingTicket(string value)
    {
        var capture = Capture();
        var write = Guid.NewGuid();
        var saved = sessions.WriteInputs(session, capture, 0, write, Values(value));
        saved.Revision.ShouldBe(1);
        var restarted = new ViewSessions(root, clock);
        var next = Capture() with { Form = capture.Form with { Ticket = Guid.NewGuid() } };
        var restored = restarted.ReadInputs(Attach(restarted), next);
        restored.Status.ShouldBe("Restored");
        restored.WriteId.ShouldBe(write);
        restored.Values!.Fields.Single().Value.Value.GetString().ShouldBe(value);
        Directory.GetFiles(root, "*.inputs").ShouldHaveSingleItem();
        File.ReadAllText(Directory.GetFiles(root, "*.inputs").Single()).ShouldNotContain(capture.Form.Ticket.ToString());
    }

    [Theory]
    [InlineData("source")]
    [InlineData("root")]
    [InlineData("authority")]
    [InlineData("bench")]
    [InlineData("item")]
    [InlineData("store")]
    [InlineData("plan")]
    [InlineData("scope")]
    [InlineData("mode")]
    [InlineData("endpoint")]
    public void StableIdentitySeparatesEveryNativeBufferDimension(string dimension)
    {
        var capture = Capture();
        sessions.WriteInputs(session, capture, 0, Guid.NewGuid(), Values("-"));
        var identity = capture.Identity;
        identity = dimension switch
        {
            "source" => identity with { SourceId = Guid.NewGuid() },
            "root" => identity with { Root = Path.Combine(root, "other") },
            "authority" => identity with { SourceBinding = "different" },
            "bench" => identity with { BenchId = Guid.NewGuid() },
            "item" => identity with { ItemId = "other" },
            "store" => identity with { StoreInstance = Guid.NewGuid() },
            "plan" => identity with { PlanMode = true, PlanId = Guid.NewGuid() },
            "scope" => identity with { ScopeId = Guid.NewGuid() },
            "mode" => identity with { Mode = "normal-seed" },
            _ => identity with { EndpointId = "explicit-other" },
        };
        var different = capture with { Identity = identity };
        different.Key.ShouldNotBe(capture.Key);
        if (dimension is "source" or "root" or "authority" or "bench")
            Should.Throw<HttpFailure>(() => sessions.ReadInputs(session, different)).Code.ShouldBe("Conflict");
        else sessions.ReadInputs(session, different).Status.ShouldBe("Empty");
        sessions.ReadInputs(session, capture).Values.ShouldNotBeNull();
    }

    [Fact]
    public void ProfileRepositoryOwnerExpiryAndSignoutWithholdRawValues()
    {
        var capture = Capture();
        sessions.WriteInputs(session, capture, 0, Guid.NewGuid(), Values("private"));
        sessions.ReadInputs(Attach(sessions, Guid.NewGuid()), capture).Status.ShouldBe("Empty");
        session.RepositoryRoot = Path.Combine(root, "repository");
        Should.Throw<HttpFailure>(() => sessions.ReadInputs(session, capture)).Code.ShouldBe("Conflict");
        sessions.ReadInputs(session, capture with { Identity = capture.Identity with { RepositoryRoot = session.RepositoryRoot } })
            .Status.ShouldBe("Empty");
        session.RepositoryRoot = null;
        session.OwnerAuthenticated = false;
        Should.Throw<HttpFailure>(() => sessions.ReadInputs(session, capture)).Status.ShouldBe(403);
        session.OwnerAuthenticated = true;
        clock.Now += TimeSpan.FromHours(25);
        Should.Throw<HttpFailure>(() => sessions.ReadInputs(session, capture)).Status.ShouldBe(403);
        var renewed = Attach(sessions);
        sessions.Revoke(renewed);
        Should.Throw<HttpFailure>(() => sessions.ReadInputs(renewed, capture)).Status.ShouldBe(403);
    }

    [Fact]
    public void NativeBaselineConflictWithholdsValuesAndRequiresExplicitConditionalDiscard()
    {
        var capture = Capture();
        sessions.WriteInputs(session, capture, 0, Guid.NewGuid(), Values("-"));
        var changed = capture with { Baseline = "changed-native-baseline" };
        var conflict = sessions.ReadInputs(session, changed);
        conflict.Status.ShouldBe("Conflict");
        conflict.Values.ShouldBeNull();
        conflict.Revision.ShouldBe(1);
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, changed, 1, Guid.NewGuid(), Values("new")))
            .Code.ShouldBe("InputBaselineConflict");
        sessions.WriteInputs(session, changed, 1, Guid.NewGuid(), null).Status.ShouldBe("Discarded");
        sessions.WriteInputs(session, changed, 2, Guid.NewGuid(), Values("new")).Status.ShouldBe("Restored");
    }

    [Fact]
    public void LostResponseIsObservedByWriteIdentityAndStaleOrReplayedPutsCannotClobber()
    {
        var capture = Capture();
        var original = Guid.NewGuid();
        _ = sessions.WriteInputs(session, capture, 0, original, Values("original"));
        var observation = new ViewSessions(root, clock).ReadInputs(Attach(new ViewSessions(root, clock)), capture);
        observation.WriteId.ShouldBe(original);
        observation.Revision.ShouldBe(1);
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, capture, 0, original, Values("original")))
            .Code.ShouldBe("InputRevisionConflict");
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, capture, 1, original, Values("replay")))
            .Code.ShouldBe("InputRevisionConflict");
        sessions.WriteInputs(session, capture, 1, Guid.NewGuid(), Values("newer"));
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, capture, 1, Guid.NewGuid(), Values("late")))
            .Code.ShouldBe("InputRevisionConflict");
        sessions.ReadInputs(session, capture).Values!.Fields[0].Value.Value.GetString().ShouldBe("newer");
    }

    [Fact]
    public async Task IndependentServerWritersCannotOverwriteTheSameAcknowledgement()
    {
        var capture = Capture();
        var peer = new ViewSessions(root, clock);
        var peerSession = Attach(peer);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = new[] { (sessions, session), (peer, peerSession) }.Select(async writer =>
        {
            await start.Task;
            var writeId = Guid.NewGuid();
            try
            {
                var saved = writer.Item1.WriteInputs(writer.Item2, capture, 0, writeId, Values(writeId.ToString("D")));
                return (WriteId: writeId, Saved: saved);
            }
            catch (HttpFailure failure) when (failure.Code is "InputRevisionConflict" or "ViewStateBusy")
            {
                return (WriteId: writeId, Saved: (EditorInputRecoveryDto?)null);
            }
        }).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(writers);
        var winner = results.Single(result => result.Saved is not null);
        results.Count(result => result.Saved is null).ShouldBe(1);
        var observed = peer.ReadInputs(peerSession, capture);
        observed.Revision.ShouldBe(1);
        observed.WriteId.ShouldBe(winner.WriteId);
        observed.Values!.Fields[0].Value.Value.GetString().ShouldBe(winner.WriteId.ToString("D"));
    }

    [Fact]
    public void IndependentInitialCapturesAndDeleteRecreateUseOnlyTheirOwnVersion()
    {
        var first = Capture();
        var second = first with { Identity = first.Identity with { ItemId = "second" } };
        var observed = sessions.ReadInputs(session, second);
        sessions.WriteInputs(session, first, 0, Guid.NewGuid(), Values("first"));
        var saved = sessions.WriteInputs(session, second, observed.Revision, Guid.NewGuid(), Values("second"));
        saved.Revision.ShouldBe(1);
        var deleted = sessions.WriteInputs(session, second, saved.Revision, Guid.NewGuid(), null);
        sessions.WriteInputs(session, second, deleted.Revision, Guid.NewGuid(), Values("replacement"));
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, second, saved.Revision, Guid.NewGuid(), Values("late")))
            .Code.ShouldBe("InputRevisionConflict");
        sessions.ReadInputs(session, second).Values!.Fields.Single().Value.Value.GetString().ShouldBe("replacement");
    }

    [Fact]
    public void ExactSubmissionAndRevisionSpecificConsumptionPreserveNewerInputsAndOtherBuffers()
    {
        var capture = Capture();
        var other = capture with { Identity = capture.Identity with { ItemId = "other" } };
        sessions.WriteInputs(session, capture, 0, Guid.NewGuid(), Values("old"));
        var otherRevision = sessions.WriteInputs(session, other, sessions.ReadInputs(session, other).Revision, Guid.NewGuid(), Values("other")).Revision;
        var reference = new EditorInputReference(capture.Key, 1);
        Should.Throw<HttpFailure>(() => sessions.RequireInputs(session, capture, reference, Values("substitution")))
            .Code.ShouldBe("InputRevisionConflict");
        sessions.RequireInputs(session, capture, reference, Values("old"));
        var newer = sessions.WriteInputs(session, capture, 1, Guid.NewGuid(), Values("newer"));
        sessions.ConsumeInputs(session, capture, reference);
        sessions.ReadInputs(session, capture).Values!.Fields[0].Value.Value.GetString().ShouldBe("newer");
        sessions.ConsumeInputs(session, capture, reference with { Revision = newer.Revision });
        sessions.ReadInputs(session, capture).Status.ShouldBe("Consumed");
        sessions.ReadInputs(session, other).Values.ShouldNotBeNull();
        sessions.WriteInputs(session, other, otherRevision, Guid.NewGuid(), null).Status.ShouldBe("Discarded");
        sessions.ReadInputs(session, capture).Status.ShouldBe("Consumed");
    }

    [Fact]
    public void RelationshipAndLocalInputsCannotCrossModeOrEndpoint()
    {
        var scalar = Capture();
        var pair = new RelationshipFormDto(scalar.Form, new("item", "1", "fixture", "1", []),
            new("endpoint", "2", "fixture", "1", []), [], [new("endpoint", "association", true, false)], [], []);
        var relationship = scalar with { Identity = scalar.Identity with { Mode = "relationships", EndpointId = "endpoint" }, Pair = pair };
        var links = new EditorInputValues([], pair.Choices);
        sessions.WriteInputs(session, relationship, 0, Guid.NewGuid(), links);
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, scalar, 0, Guid.NewGuid(), links)).Status.ShouldBe(400);
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, relationship, 1, Guid.NewGuid(),
            new([], [new("wrong", "association", true, false)]))).Status.ShouldBe(400);
        var local = relationship with { Identity = relationship.Identity with { Mode = "local-relationship" }, Pair = pair with { LocalOnly = true } };
        sessions.WriteInputs(session, local, 0, Guid.NewGuid(), new([], Adding: null));
        sessions.WriteInputs(session, local, 1, Guid.NewGuid(), new([], Adding: true));
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, local, 2, Guid.NewGuid(), Values("scalar"))).Status.ShouldBe(400);
        sessions.ReadInputs(session, relationship).Values!.Links.ShouldHaveSingleItem();
    }

    [Fact]
    public void InvalidShapesAndOversizedInputsCannotBeAcknowledged()
    {
        var capture = Capture();
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, capture, 0, Guid.NewGuid(),
            new([new("number", new("Integer", JsonSerializer.SerializeToElement(4, TransportJson.Default.Int32), null))]))).Status.ShouldBe(400);
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, capture, 0, Guid.NewGuid(), Values(new string('x', 65537))))
            .Status.ShouldBe(400);
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, capture, 0, Guid.NewGuid(),
            new([Values("x").Fields[0], Values("y").Fields[0]]))).Status.ShouldBe(400);
        sessions.ReadInputs(session, capture).Status.ShouldBe("Empty");
    }

    [Fact]
    public void SerializedBufferAndProfileByteBoundsNeverEvictAcknowledgedInputs()
    {
        var original = Capture();
        var definitions = Enumerable.Range(0, 5).Select(index => original.Form.Fields[0] with { Reference = "number-" + index }).ToArray();
        var capture = original with { Form = original.Form with { Fields = definitions } };
        var fields = definitions.Select(field => new FieldDto(field.Reference, Values(new string('x', 60000)).Fields[0].Value)).ToArray();
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, capture, 0, Guid.NewGuid(), new(fields)))
            .Code.ShouldBe("InvalidInput");
        var values = new EditorInputValues(fields[..4]);
        for (var index = 0; index < 4; index++)
        {
            var next = capture with { Identity = capture.Identity with { ItemId = "large-" + index } };
            sessions.WriteInputs(session, next, sessions.ReadInputs(session, next).Revision, Guid.NewGuid(), values);
        }
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, capture, sessions.ReadInputs(session, capture).Revision,
            Guid.NewGuid(), values)).Code.ShouldBe("InputLimit");
        sessions.ReadInputs(session, capture).Status.ShouldBe("Empty");
        var retained = sessions.ReadInputs(session, capture with { Identity = capture.Identity with { ItemId = "large-0" } });
        retained.Values!.Fields.Length.ShouldBe(4);
        retained.Values.Fields.ShouldAllBe(field => field.Value.Value.GetString()!.Length == 60000);
    }

    [Fact]
    public void FullBuffersAreBoundedButDiscardReclaimsCapacityWithoutAcceptingDelayedInitialWrites()
    {
        var first = Capture();
        sessions.WriteInputs(session, first, 0, Guid.NewGuid(), Values("first"));
        for (var index = 1; index < 64; index++)
        {
            var capture = first with { Identity = first.Identity with { ItemId = "item-" + index } };
            sessions.WriteInputs(session, capture, sessions.ReadInputs(session, capture).Revision, Guid.NewGuid(), Values("retained"));
        }
        var next = first with { Identity = first.Identity with { ItemId = "new-buffer" } };
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, next, sessions.ReadInputs(session, next).Revision,
            Guid.NewGuid(), Values("overflow"))).Code.ShouldBe("InputLimit");
        sessions.WriteInputs(session, first, 1, Guid.NewGuid(), null);
        sessions.WriteInputs(session, next, sessions.ReadInputs(session, next).Revision, Guid.NewGuid(), Values("new"));
        Should.Throw<HttpFailure>(() => sessions.WriteInputs(session, first, 0, Guid.NewGuid(), Values("late initial")))
            .Code.ShouldBe("InputRevisionConflict");
        sessions.ReadInputs(session, next).Values!.Fields[0].Value.Value.GetString().ShouldBe("new");
    }

    private ViewSession Attach(ViewSessions owner, Guid? selectedProfile = null)
    {
        var enrolled = owner.ConnectOwner(store, null, selectedProfile ?? profile);
        var attached = owner.Content(enrolled.ViewId, enrolled.ContentCapability, true);
        attached.DisplayContexts[source.Stamp.Connection] = source;
        attached.CurrentBenchId = bench;
        return attached;
    }

    private EditorInputCapture Capture()
    {
        var value = new FieldValueDto("Integer", JsonSerializer.SerializeToElement("1", TransportJson.Default.String), null);
        return new(new(source.Source.Id, root, CatalogFingerprint.Binding(source.Source.Binding), null, bench, "item", "scalar",
                source.Stamp.StoreInstance, false, Guid.Empty, Guid.Empty, null), "native-baseline",
            new(Guid.NewGuid(), "authority", "fixture", "state",
                [new("number", "Integer", "Number", false, false, false, [], value)], [], []), null);
    }

    private static EditorInputValues Values(string value)
        => new([new("number", new("Integer", JsonSerializer.SerializeToElement(value, TransportJson.Default.String), null))]);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class Clock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
