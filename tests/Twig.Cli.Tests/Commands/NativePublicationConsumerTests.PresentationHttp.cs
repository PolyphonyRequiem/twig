using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Infrastructure;
using Twig.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Twig.Core;
using Twig.Infrastructure.Services;
using Xunit;

namespace Twig.Cli.Tests.Commands;

public sealed partial class NativePublicationConsumerTests
{
    [Fact]
    public async Task NativePresentationHttpPersistsWithoutAcquisitionAndPreservesContentPriority()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var ct = deadline.Token;
        var fixture = await CreateHumanHostAsync();
        var directory = Path.GetDirectoryName(fixture.Registry)!;
        var gateway = new NativeHttpGateway(this);
        var changes = new CatalogChanges();
        var store = UserBenchStore.ForRegistry(fixture.Registry);
        var panel = new NativePanel(store, gateway,
            new NativeSourceAdapter(Path.Combine(directory, "presentation-evidence"), gateway, changes));
        var sessions = new ViewSessions(Path.Combine(directory, "presentation-profiles"));
        var instance = (await store.ReadAsync(ct)).Value.InstanceId;
        var profile = Guid.NewGuid();
        var canvas = sessions.RegisterNativeCanvas(instance);
        var human = sessions.ConnectOwner(instance, canvas.ViewId, profile);
        await using var app = NativeBenchHost.Build(new(0, directory), panel, sessions, changes);
        await app.StartAsync(ct);
        try
        {
            using var client = new HttpClient
            {
                BaseAddress = new(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
            };
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            async Task<(HttpStatusCode Status, JsonNode Body)> Send(string path, HttpMethod method,
                JsonNode? body = null, string? token = null)
            {
                using var request = new HttpRequestMessage(method, $"/api/v1/views/{human.ViewId:D}" + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? human.ContentCapability);
                if (body is not null)
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, ct);
                return (response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!);
            }
            async Task<int> Captures()
            {
                var captures = await UserBenchStore.ForRegistry(fixture.Registry).ReadEditorCapturesAsync(ct);
                captures.IsSuccess.ShouldBeTrue(captures.IsSuccess ? "" : captures.Error.Message);
                return captures.Value.Length;
            }
            var admitted = await Send("/context", HttpMethod.Put, new JsonObject { ["root"] = fixture.Root });
            admitted.Status.ShouldBe(HttpStatusCode.OK, admitted.Body.ToJsonString());
            (await Send("/selection", HttpMethod.Put, new JsonObject { ["expectedBenchId"] = null, ["benchId"] = fixture.Bench })).Status.ShouldBe(HttpStatusCode.OK);
            var view = await Send($"/benches/{fixture.Bench:D}", HttpMethod.Get);
            view.Status.ShouldBe(HttpStatusCode.OK, view.Body.ToJsonString());
            view.Body["sources"]![0]!["items"]!.AsArray().Count.ShouldBe(2);
            _transport.TotalRequests.ShouldBeGreaterThan(0);

            var session = sessions.Content(human.ViewId, human.ContentCapability, true);
            var state = sessions.ReadState(session);
            sessions.SaveState(session, new(state.Revision, state.Choices with
            {
                ViewingRoot = fixture.Root, AddRoot = fixture.Root, DisplayRoots = [fixture.Root],
                BenchId = fixture.Bench, TeamId = Guid.NewGuid().ToString("D"),
            }));
            (await Send("/layout", HttpMethod.Put, new JsonObject { ["layout"] = "tree" }, canvas.PresentationCapability))
                .Status.ShouldBe(HttpStatusCode.Forbidden, "SDK controls require explicit human attachment.");
            var attached = await Send("/attachment", HttpMethod.Post, new JsonObject());
            attached.Status.ShouldBe(HttpStatusCode.OK, attached.Body.ToJsonString());
            state = sessions.ReadState(session);
            var before = await Send("/view-state", HttpMethod.Get);
            before.Status.ShouldBe(HttpStatusCode.OK);
            var expectedChoices = before.Body["choices"]!.DeepClone();
            expectedChoices["layout"] = "tree";
            var beforeOnline = gateway.OnlineReads;
            var beforeHttp = _transport.TotalRequests;
            var beforeCaptures = await Captures();
            using var events = changes.Subscribe(out var reader);
            gateway.HoldOnline();
            try
            {
                JsonObject Body(long revision, string layout) => new()
                {
                    ["expectedRevision"] = revision, ["benchId"] = fixture.Bench.ToString("D"),
                    ["layout"] = layout, ["groupSource"] = null, ["groupField"] = null,
                };
                var changed = await Send("/session", HttpMethod.Put, Body(state.Revision, "tree"))
                    .WaitAsync(TimeSpan.FromSeconds(2), ct);
                changed.Status.ShouldBe(HttpStatusCode.OK, changed.Body.ToJsonString());
                reader.TryRead(out var notification).ShouldBeTrue();
                notification!.PresentationOnly.ShouldBeTrue("Human layout publishes presentation only.");
                var after = sessions.ReadState(session);
                var persisted = await Send("/view-state", HttpMethod.Get);
                persisted.Status.ShouldBe(HttpStatusCode.OK);
                JsonNode.DeepEquals(persisted.Body["choices"], expectedChoices).ShouldBeTrue(
                    "Layout preserves Bench, roots, team and every unrelated persisted choice.");

                var other = sessions.ConnectOwner(instance, null, profile);
                var second = sessions.Content(other.ViewId, other.ContentCapability, true);
                var current = sessions.ReadState(second);
                sessions.SaveState(second, new(current.Revision, current.Choices with { Mode = "queries" }));
                var stale = await Send("/session", HttpMethod.Put, Body(after.Revision, "list"));
                stale.Status.ShouldBe(HttpStatusCode.Conflict, "A genuine other-view revision advance remains CAS-protected.");
                var fresh = sessions.ReadState(session);
                var reconciled = await Send("/session", HttpMethod.Put, Body(fresh.Revision, "list"));
                reconciled.Status.ShouldBe(HttpStatusCode.OK, reconciled.Body.ToJsonString());
                sessions.ReadState(session).Choices.Mode.ShouldBe("queries");
                sessions.ReadState(session).Choices.BenchId.ShouldBe(fixture.Bench,
                    "Disjoint other-view choices survive explicit reconciliation.");
                (await Send("/layout", HttpMethod.Put, new JsonObject { ["layout"] = "tree" }, canvas.PresentationCapability))
                    .Status.ShouldBe(HttpStatusCode.OK, "Attached SDK keeps its acknowledgement-only layout path.");
                sessions.ReadState(session).Choices.Layout.ShouldBe("tree",
                    "SDK layout reaches the same persisted presentation.");

                gateway.OnlineReads.ShouldBe(beforeOnline, "Presentation must not acquire fresh online evidence.");
                _transport.TotalRequests.ShouldBe(beforeHttp, "Presentation must not acquire provider/schema/membership HTTP.");
                (await Captures()).ShouldBe(beforeCaptures, "Presentation must not create native captures.");
                HumanPatchAttempts.ShouldBe(0, "Presentation never publishes.");
                while (reader.TryRead(out _)) { }
                changes.Publish(5);
                changes.PublishPresentation();
                reader.TryRead(out var content).ShouldBeTrue();
                content!.PresentationOnly.ShouldBeFalse("Presentation cannot evict a pending content invalidation.");
                content.Resync.ShouldBeTrue("Overflow preserves content invalidation through an all-resource resync.");
                content.AgentViewId.ShouldBeNull();
                reader.TryRead(out _).ShouldBeFalse();
                changes.PublishPresentation();
                changes.Publish(6);
                reader.TryRead(out content).ShouldBeTrue();
                content!.PresentationOnly.ShouldBeFalse("Content takes priority over a queued presentation event.");
                content.Resync.ShouldBeTrue("Overflow preserves content invalidation through an all-resource resync.");
                content.AgentViewId.ShouldBeNull();
                reader.TryRead(out _).ShouldBeFalse();
            }
            finally { gateway.ReleaseOnline(); }
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    private sealed class NativeHttpGateway(NativePublicationConsumerTests fixture) : INativeSourceGateway
    {
        internal int LocalReads { get; private set; }
        internal int SchemaReads { get; private set; }
        internal int OnlineReads { get; private set; }
        internal int PreparedEditors { get; private set; }
        internal int ExistingEditors { get; private set; }
        internal int PlanReviews { get; private set; }
        internal int OpenReviews { get; private set; }
        internal bool RefuseEditor { get; set; }
        internal bool SourceUnavailable { get; set; }
        internal int ProfileReads { get; private set; }
        internal ImmutableArray<string> ProfileIdentities { get; private set; } = [];
        internal Action<string>? Trace { get; set; }
        private TaskCompletionSource? onlineGate;

        internal void HoldOnline() => onlineGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void ReleaseOnline() { onlineGate?.TrySetResult(); onlineGate = null; }

        public Task<Result<NativeSourceObservation>> ReadAsync(string root, NativeSourceStamp? expected,
            bool observeSchema, CancellationToken ct)
        {
            if (SourceUnavailable)
                return Task.FromResult(Result<NativeSourceObservation>.Fail(ErrorCode.StorageUnavailable, "Test source is offline."));
            if (observeSchema) SchemaReads++; else LocalReads++;
            var trace = Trace;
            trace?.Invoke(observeSchema ? "schema read started" : "local read started");
            var pending = fixture.ReadHumanHostAsync(root, expected, observeSchema, ct);
            return trace is null ? pending : Observe(pending, trace, observeSchema);

            static async Task<Result<NativeSourceObservation>> Observe(Task<Result<NativeSourceObservation>> pending,
                Action<string> trace, bool schema)
            {
                try { return await pending.ConfigureAwait(false); }
                finally { trace(schema ? "schema read completed" : "local read completed"); }
            }
        }

        public async Task<Result<NativeSourceObservation>> ReadOnlineAsync(string root, NativeSourceStamp expected,
            CancellationToken ct)
        {
            OnlineReads++;
            Trace?.Invoke("online gateway entered");
            if (onlineGate is { } held) await held.Task.WaitAsync(ct);
            return await fixture.ReadHumanOnlineAsync(root, expected, ct);
        }

        public Task<Result<ImmutableArray<WorkItem>>> ObserveItemsAsync(string root, NativeSourceStamp expected,
            ImmutableArray<ItemKey> keys, CancellationToken ct)
            => NativeSourceReader.ObserveItemsAsync(root, expected, keys, fixture.ConfigureHumanEditor, ct);

        public Task<Result<NativeItemEditor>> OpenEditorAsync(string root, Guid bench,
            NativeSourceStamp expected, CancellationToken ct)
        {
            PreparedEditors++;
            return RefuseEditor
                ? Task.FromResult(Result<NativeItemEditor>.Fail(ErrorCode.Unavailable, "Synthetic native editor refusal."))
                : fixture.OpenHumanHostAsync(root, bench, expected, ct);
        }

        public Task<Result<NativeItemEditor>> OpenExistingEditorAsync(string root, Guid bench,
            NativeSourceStamp expected, CancellationToken ct)
        {
            ExistingEditors++;
            return NativeItemEditor.OpenAsync(root, bench, expected, fixture.ConfigureHumanEditor, ct, prepareScope: false);
        }

        public Task<Result<NativePlanReview>> ReviewPlanAsync(string root, Guid bench,
            ImmutableArray<NativePlanSource> sources, CancellationToken ct)
        {
            PlanReviews++;
            return fixture.ReviewHumanPlanAsync(root, bench, sources, ct);
        }

        public Task<Result<NativePlanReview>> OpenPlanReviewAsync(string root, string digest, CancellationToken ct)
        {
            OpenReviews++;
            return fixture.OpenHumanPlanReviewAsync(root, digest, ct);
        }

        public async Task<Result<NativeSourceObservation>> ReadIdentityProfilesAsync(string root,
            NativeSourceStamp expected, ImmutableArray<string> identities, CancellationToken ct)
        {
            ProfileReads++;
            ProfileIdentities = identities;
            var observed = await ReadOnlineAsync(root, expected, ct);
            return observed.IsSuccess ? Result<NativeSourceObservation>.Ok(observed.Value with
            {
                IdentityProfiles = identities.Select(identity => new IdentityProfile(identity,
                    "Synthetic Full Name", null, "Synthetic photo unavailable")).ToImmutableArray(),
            }) : observed;
        }
    }
}
