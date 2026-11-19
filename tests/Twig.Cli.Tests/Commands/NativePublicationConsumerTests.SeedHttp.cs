using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Bench.Application;
using Bench.Infrastructure;
using Bench.Server;
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
    // Canvas NormalSeedTests.cs, blob 6c46022a2061f910ef68e0151ea057faef5ca5ea.
    [Theory]
    [InlineData("projection")]
    [InlineData("lifecycle")]
    [InlineData("pin")]
    [InlineData("origin")]
    [InlineData("unknown")]
    [InlineData("guards")]
    [InlineData("ownership")]
    public async Task NativeAdoSeedHttpKeepsExactAuthority(string scenario)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var ct = deadline.Token;
        var fixture = await CreateHumanHostAsync().WaitAsync(ct);
        var directory = Path.GetDirectoryName(fixture.Registry)!;
        var store = UserBenchStore.ForRegistry(fixture.Registry);
        var definition = (await store.ReadAsync(ct)).Value.Benches.Single();
        (await store.SaveAsync(new(definition.Id, definition.Name, [], connections: definition.Connections),
            definition.Revision, ct)).IsSuccess.ShouldBeTrue();
        var observation = (await ReadHumanHostAsync(fixture.Root, fixture.Stamp, true, ct)).Value;
        observation.Snapshot.Items.Length.ShouldBe(2, "Clearing membership does not clear the native item cache.");
        var type = observation.Schema!.Value.Types.Single(value => !value.Hidden);
        var state = type.States.Single(value => value.Category == StateCategory.Proposed).Name;
        var gateway = new NativeHttpGateway(this);
        var changes = new CatalogChanges();
        var panel = new NativePanel(store, gateway,
            new NativeSourceAdapter(Path.Combine(directory, "seed-http-evidence"), gateway, changes));
        var sessions = new ViewSessions(Path.Combine(directory, "seed-http-profiles"));
        var instance = (await store.ReadAsync(ct)).Value.InstanceId;
        var canvas = sessions.RegisterNativeCanvas(instance);
        var human = sessions.ConnectOwner(instance, canvas.ViewId, Guid.NewGuid());
        var owned = sessions.Content(human.ViewId, human.ContentCapability, true);
        await using var app = NativeBenchHost.Build(new(0, directory), panel, sessions, changes);
        try
        {
            await app.StartAsync(ct);
            using var client = new HttpClient
            {
                BaseAddress = new(app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                Timeout = TimeSpan.FromSeconds(10),
            };
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            async Task<(HttpStatusCode Status, JsonNode Body)> Send(string path, HttpMethod method,
                JsonNode? body = null, string? capability = null)
            {
                using var request = new HttpRequestMessage(method, $"/api/v1/views/{human.ViewId:D}" + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", capability ?? human.ContentCapability);
                if (body is not null)
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var response = await client.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                text.ShouldNotBeNullOrWhiteSpace(path);
                return (response.StatusCode, JsonNode.Parse(text)!);
            }
            async Task<JsonNode> Ok(string path, HttpMethod method, JsonNode? body = null)
            {
                var response = await Send(path, method, body);
                response.Status.ShouldBe(HttpStatusCode.OK, path + ": " + response.Body.ToJsonString());
                return response.Body;
            }
            static JsonObject Field(string reference, string value) => new()
            {
                ["reference"] = reference,
                ["value"] = new JsonObject { ["kind"] = "Text", ["value"] = value, ["dataType"] = null },
            };
            Task<JsonNode> View() => Ok($"/benches/{fixture.Bench:D}", HttpMethod.Get);
            async Task Report()
            {
                var snapshot = (await ReadHumanHostAsync(fixture.Root, fixture.Stamp, false, ct)).Value.Snapshot;
                _agentHttpOutput?.WriteLine($"SEED HTTP {scenario}: providerHttp={_transport.TotalRequests}, " +
                    $"admissions={_transport.ObservedPaths.Count(path => path.EndsWith("/_apis/connectionData", StringComparison.Ordinal))}, " +
                    $"captures={(await store.ReadEditorCapturesAsync(ct)).Value.Length}, " +
                    $"creates={_transport.PostCount}, patches={HumanPatchAttempts}, committed={_transport.CommittedWrites}, " +
                    $"readbacks={_transport.ReadbacksAfterCommit}, journal={snapshot.Journal.Length}, " +
                    $"receipts={snapshot.Journal.Sum(row => row.Receipts.Length)}, " +
                    $"prepared={gateway.PreparedEditors}, existing={gateway.ExistingEditors}");
            }
            void NoWrites()
            {
                _transport.PostCount.ShouldBe(0);
                HumanPatchAttempts.ShouldBe(0);
                _transport.CommittedWrites.ShouldBe(0);
            }

            await Ok("/context", HttpMethod.Put, new JsonObject { ["root"] = fixture.Root });
            var view = await View();
            var source = view["sources"]!.AsArray().ShouldHaveSingleItem()!;
            source["items"]!.AsArray().ShouldBeEmpty("An explicitly empty Bench has no cached remote members.");
            source["items"]!.AsArray().ShouldAllBe(item => !item!["benchMember"]!.GetValue<bool>());
            NoWrites();
            var sourceId = source["sourceId"]!.GetValue<string>();
            var create = new JsonObject
            {
                ["sourceId"] = sourceId, ["benchId"] = fixture.Bench.ToString("D"), ["type"] = type.Reference,
            };
            var seedPath = $"/benches/{fixture.Bench:D}/seeds";
            JsonObject OpenBody(string itemId) => new()
            {
                ["sourceId"] = sourceId, ["benchId"] = fixture.Bench.ToString("D"),
                ["itemId"] = itemId, ["planMode"] = false,
            };
            var emptyBench = (await store.ReadAsync(ct)).Value.Benches.Single();
            async Task UnchangedBench()
            {
                var current = (await store.ReadAsync(ct)).Value.Benches.Single();
                (current.Id, current.Name, current.Revision).ShouldBe((emptyBench.Id, emptyBench.Name, emptyBench.Revision));
                current.Selectors.ShouldBe(emptyBench.Selectors);
                current.Connections.ShouldBe(emptyBench.Connections);
            }

            if (scenario == "projection")
            {
                ItemKey key;
                using (var editor = (await OpenHumanHostAsync(fixture.Root, fixture.Bench, fixture.Stamp, ct)).Value)
                {
                    var form = (await editor.CreateSeedAsync(type.Reference, ct)).Value;
                    key = form.Item;
                    key.LocalId.Version.ShouldBe(7);
                    (await editor.StageAsync(form.Ticket,
                        FieldValues.Empty.Set("System.Title", FieldValue.String("Owned projection"))
                            .Set(type.StateField, FieldValue.String(state)), ct)).IsSuccess.ShouldBeTrue();
                    var nativeSeed = (await editor.ReadOwnedSeedsAsync(ct)).Value.ShouldHaveSingleItem();
                    nativeSeed.Ready.ShouldBeTrue();
                    nativeSeed.Draft.ShouldNotBeNull().Baseline.ShouldBeNull();
                    nativeSeed.Item.ShouldBe(key);
                }
                var retained = (await View())["sources"]![0]!["items"]!.AsArray()
                    .Single(item => item!["isLocal"]!.GetValue<bool>())!;
                var opened = await Ok("/editor", HttpMethod.Post, OpenBody(retained["id"]!.GetValue<string>()));
                opened["itemId"]!.GetValue<string>().ShouldBe(retained["id"]!.GetValue<string>());
                var projected = await Ok(seedPath, HttpMethod.Get);
                var seed = projected["seeds"]!.AsArray().ShouldHaveSingleItem()!;
                seed["localId"]!.GetValue<string>().ShouldBe(key.LocalId.ToString("D"));
                seed["ready"]!.GetValue<bool>().ShouldBeTrue();
                projected["source"]!["sourceId"]!.GetValue<string>().ShouldBe(sourceId);
                projected["source"]!["items"]!.AsArray().Single(item => item!["isLocal"]!.GetValue<bool>())!
                    ["benchMember"]!.GetValue<bool>().ShouldBeTrue();
                await UnchangedBench();
                NoWrites();
                await Report();
                return;
            }

            var beforeHttp = _transport.TotalRequests;
            var beforeCaptures = (await store.ReadEditorCapturesAsync(ct)).Value.Length;
            foreach (var path in new[] { seedPath, "/editor/seed", "/seeds/observe", "/seeds/delivery" })
                (await Send(path, path == seedPath ? HttpMethod.Get : HttpMethod.Post,
                    path == seedPath ? null : create, canvas.PresentationCapability)).Status.ShouldBe(HttpStatusCode.Forbidden);
            (await Send(seedPath, HttpMethod.Get, capability: new string('x', 64))).Status.ShouldBe(HttpStatusCode.Forbidden);
            client.DefaultRequestHeaders.Remove("X-Twig-Bench");
            (await Send("/editor/seed", HttpMethod.Post, create)).Status.ShouldBe(HttpStatusCode.Forbidden);
            client.DefaultRequestHeaders.Add("X-Twig-Bench", "1");
            _transport.TotalRequests.ShouldBe(beforeHttp, "SDK, owner and CSRF refusal precedes provider HTTP.");
            (await store.ReadEditorCapturesAsync(ct)).Value.Length.ShouldBe(beforeCaptures);
            NoWrites();
            var choices = await Ok(seedPath, HttpMethod.Get);
            choices["types"]!.AsArray().ShouldContain(value => value!["reference"]!.GetValue<string>() == type.Reference);
            var invalid = create.DeepClone();
            invalid["type"] = "Not.A.Connected.Type";
            (await Send("/editor/seed", HttpMethod.Post, invalid)).Status.ShouldBe(HttpStatusCode.BadRequest);
            var created = await Ok("/editor/seed", HttpMethod.Post, create);
            created["fields"]!.AsArray().ShouldAllBe(field => field!["value"]!["kind"]!.GetValue<string>() == "Null");
            var change = new JsonObject { ["ticket"] = created["ticket"]!.DeepClone(), ["fields"] = new JsonArray() };
            (await Send("/editor/save", HttpMethod.Post, change)).Status.ShouldBe(HttpStatusCode.BadRequest);
            change["fields"] = new JsonArray(Field("System.Title", "Explicit HTTP seed"), Field(type.StateField, state));
            await Ok("/editor/stage", HttpMethod.Post, change);
            NoWrites();
            var normalCapture = (await store.ReadEditorCapturesAsync(ct)).Value.ShouldHaveSingleItem();
            var seedKey = UserBenchStore.ParseIntent(normalCapture.IntentJson).Operations.ShouldHaveSingleItem().Item;
            seedKey.IsLocal.ShouldBeTrue();
            seedKey.LocalId.Version.ShouldBe(7);
            normalCapture.UserInstance.ShouldBe(instance);
            normalCapture.BenchId.ShouldBe(fixture.Bench);
            normalCapture.Origin.ShouldBe(fixture.Stamp);
            normalCapture.Root.ShouldBe(fixture.Root);
            normalCapture.StoreInstance.ShouldBe(fixture.Stamp.StoreInstance);
            normalCapture.PlanMode.ShouldBeFalse();
            normalCapture.ScopeId.ShouldBe(Guid.Empty);
            normalCapture.Ready.ShouldBeTrue();
            normalCapture.Settled.ShouldBeFalse();
            normalCapture.SeedProposal.ShouldBeNull();
            await UnchangedBench();

            ItemKey[] foreign = [];
            if (scenario == "ownership")
            {
                using var services = EditorServices(fixture.Root);
                using var origin = (await services.GetRequiredService<CoreWorkspaceFactory>().OpenAsync(ct)).Value;
                var cli = (await origin.Workspace.StageSeedAsync(type.Reference, FieldValues.Empty, ct)).Value;
                var other = (await store.SaveAsync(new(Guid.NewGuid(), "Other seed HTTP Bench", []), 0, ct)).Value;
                ItemKey otherSeed;
                using (var editor = (await OpenHumanHostAsync(fixture.Root, other.Id, fixture.Stamp, ct)).Value)
                    otherSeed = (await editor.CreateSeedAsync(type.Reference, ct)).Value.Item;
                var status = (await store.ReadPlanStatusAsync(fixture.Bench, ct)).Value;
                (await store.ChangePlanModeAsync(status.UserInstance, fixture.Bench, status.BenchRevision,
                    status.Revision, true, ct)).IsSuccess.ShouldBeTrue();
                ItemKey planSeed;
                using (var editor = (await OpenHumanHostAsync(fixture.Root, fixture.Bench, fixture.Stamp, ct)).Value)
                {
                    editor.StoreInstance.ShouldNotBe(fixture.Stamp.StoreInstance);
                    planSeed = (await editor.CreateSeedAsync(type.Reference, ct)).Value.Item;
                    (await editor.ReadPlanDraftsAsync(ct)).Value.ShouldHaveSingleItem().Key.ShouldBe(planSeed);
                }
                status = (await store.ReadPlanStatusAsync(fixture.Bench, ct)).Value;
                (await store.ChangePlanModeAsync(status.UserInstance, fixture.Bench, status.BenchRevision,
                    status.Revision, false, ct)).IsSuccess.ShouldBeTrue();
                foreign = [cli.Key, otherSeed, planSeed];
                foreign.Distinct().Count().ShouldBe(3);
                foreign.ShouldNotContain(seedKey);
                var planOwner = (await store.ReadPlanSeedOwnerAsync(planSeed.LocalId, ct)).Value;
                planOwner.BenchId.ShouldBe(fixture.Bench);
                planOwner.StoreInstance.ShouldNotBe(fixture.Stamp.StoreInstance);
                var captures = (await store.ReadEditorCapturesAsync(ct)).Value;
                captures.Single(capture => UserBenchStore.ParseIntent(capture.IntentJson).Operations.Single().Item == otherSeed)
                    .BenchId.ShouldBe(other.Id);
                using var normal = (await OpenHumanHostAsync(fixture.Root, fixture.Bench, fixture.Stamp, ct)).Value;
                (await normal.ReadOwnedSeedsAsync(ct)).Value.ShouldHaveSingleItem().Item.ShouldBe(seedKey);
                (await normal.SelectAsync(cli.Key, ct)).Error.Code.ShouldBe(ErrorCode.Conflict);
                (await normal.SelectAsync(otherSeed, ct)).Error.Code.ShouldBe(ErrorCode.Conflict);
                (await normal.SelectAsync(planSeed, ct)).Error.Code.ShouldBe(ErrorCode.Unsupported);
            }
            var seeds = await Ok(seedPath, HttpMethod.Get);
            var ownedSeed = seeds["seeds"]!.AsArray().ShouldHaveSingleItem()!;
            ownedSeed["localId"]!.GetValue<string>().ShouldBe(seedKey.LocalId.ToString("D"));
            ownedSeed["captureId"]!.GetValue<string>().ShouldBe(normalCapture.Id.ToString("D"));
            seeds["source"]!["sourceId"]!.GetValue<string>().ShouldBe(sourceId);
            var rows = seeds["source"]!["items"]!.AsArray();
            var row = rows.Single(item => item!["normalSeed"]!.GetValue<bool>())!;
            row["benchMember"]!.GetValue<bool>().ShouldBeTrue();
            var itemId = row["id"]!.GetValue<string>();
            if (scenario == "ownership")
            {
                var debts = rows.Where(item => item!["isLocal"]!.GetValue<bool>() && !item["normalSeed"]!.GetValue<bool>()).ToArray();
                debts.Length.ShouldBe(2, "CLI and other-Bench drafts are visible debt; the isolated Plan seed is not in this store.");
                foreach (var debt in debts)
                {
                    debt!["benchMember"]!.GetValue<bool>().ShouldBeFalse();
                    (await Send("/editor", HttpMethod.Post, OpenBody(debt["id"]!.GetValue<string>())))
                        .Status.ShouldBe(HttpStatusCode.NotFound);
                }
            }
            var reopened = await Ok("/editor", HttpMethod.Post, OpenBody(itemId));
            reopened["itemId"]!.GetValue<string>().ShouldBe(created["itemId"]!.GetValue<string>());
            change["ticket"] = reopened["ticket"]!.DeepClone();
            change["fields"] = new JsonArray();
            if (scenario == "guards")
            {
                var wrongSource = create.DeepClone();
                wrongSource["sourceId"] = Guid.NewGuid().ToString("D");
                (await Send("/editor/seed", HttpMethod.Post, wrongSource)).Status.ShouldBe(HttpStatusCode.Conflict);
                (await Send("/editor/save", HttpMethod.Post, change)).Status.ShouldBe(HttpStatusCode.Conflict,
                    "Even refused create navigation retires the previous editor ticket.");
                reopened = await Ok("/editor", HttpMethod.Post, OpenBody(itemId));
                var bench = (await store.ReadAsync(ct)).Value.Benches.Single();
                (await store.SaveAsync(new(bench.Id, "Changed HTTP seed Bench", bench.Selectors),
                    bench.Revision, ct)).IsSuccess.ShouldBeTrue();
                change["ticket"] = reopened["ticket"]!.DeepClone();
                (await Send("/editor/save", HttpMethod.Post, change)).Status.ShouldBe(HttpStatusCode.Conflict,
                    "A genuine native definition revision advance invalidates the field ticket.");
                reopened = await Ok("/editor", HttpMethod.Post, OpenBody(itemId));
                HumanActorDenied = true;
                change["ticket"] = reopened["ticket"]!.DeepClone();
                var denied = await Send("/editor/save", HttpMethod.Post, change);
                (denied.Status is HttpStatusCode.Forbidden or HttpStatusCode.Conflict).ShouldBeTrue();
                denied.Body["code"]!.GetValue<string>().ShouldBeOneOf("SourceChanged", "PolicyViolation");
                (await Send(seedPath, HttpMethod.Get)).Status.ShouldNotBe(HttpStatusCode.OK);
                NoWrites();
                (await store.ReadEditorCapturesAsync(ct)).Value.ShouldAllBe(capture => !capture.Settled);
                await Report();
                return;
            }

            if (scenario == "origin") _transport.FailCurrentReadsAfterCommit = true;
            else if (scenario == "pin") _transport.AfterCommit = () => SeedPinFailureAsync(fixture.Registry, true);
            else if (scenario == "unknown")
                _transport.AfterCommit = () => throw new IOException("Lost synthetic creation response after commit");
            var saved = await Ok("/editor/save", HttpMethod.Post, change);
            var publication = saved["seedPublication"]!;
            var expectedStatus = scenario == "unknown" ? "Protected"
                : scenario is "origin" or "pin" ? "VerifiedDeliveryPending" : "Complete";
            publication["status"]!.GetValue<string>().ShouldBe(expectedStatus, saved.ToJsonString());
            publication["localId"]!.GetValue<string>().ShouldBe(seedKey.LocalId.ToString("D"));
            publication["captureId"]!.GetValue<string>().ShouldBe(normalCapture.Id.ToString("D"));
            _transport.PostCount.ShouldBe(1);
            HumanPatchAttempts.ShouldBe(0);
            _transport.CommittedWrites.ShouldBe(1, "The synthetic provider really committed, even when its response was lost.");
            _transport.FailCurrentReadsAfterCommit = false;
            _transport.AfterCommit = null;
            if (scenario == "pin") await SeedPinFailureAsync(fixture.Registry, false);

            var before = (await ReadHumanHostAsync(fixture.Root, fixture.Stamp, false, ct)).Value.Snapshot;
            var proposal = before.Proposals.ShouldHaveSingleItem();
            proposal.Digest.ShouldBe(publication["digest"]!.GetValue<string>());
            var operation = proposal.Operations.ShouldHaveSingleItem();
            operation.Kind.ShouldBe(OpKind.Create);
            operation.Item.ShouldBe(seedKey);
            var journal = before.Journal.ShouldHaveSingleItem();
            journal.Digest.ShouldBe(proposal.Digest);
            journal.Status.ShouldBe(scenario == "unknown" ? OpStatus.Indeterminate : OpStatus.Verified);
            var captured = (await store.ReadEditorCapturesAsync(ct)).Value.Single(capture => capture.Id == normalCapture.Id);
            UserBenchStore.ParseIntent(captured.SeedProposal!).Digest.ShouldBe(proposal.Digest);
            captured.Settled.ShouldBe(expectedStatus == "Complete");
            if (scenario == "unknown")
            {
                before.Mappings.ShouldBeEmpty();
                before.Drafts.ShouldHaveSingleItem().Key.ShouldBe(seedKey);
                journal.Receipts.ShouldNotBeEmpty();
                journal.Receipts.ShouldAllBe(receipt => receipt.Status != OpStatus.Verified);
            }
            else
            {
                _transport.ReadbacksAfterCommit.ShouldBeGreaterThanOrEqualTo(2);
                before.Drafts.ShouldNotContain(draft => draft.Key == seedKey);
                var mapping = before.Mappings.ShouldHaveSingleItem();
                mapping.Local.ShouldBe(seedKey);
                mapping.ProposalDigest.ShouldBe(proposal.Digest);
                journal.Receipts.Length.ShouldBe(2);
                var applied = journal.Receipts.Single(receipt => receipt.Status == OpStatus.Applied);
                var verified = journal.Receipts.Single(receipt => receipt.Status == OpStatus.Verified);
                (applied.Digest, applied.Attempt, applied.PublishedKey).ShouldBe((proposal.Digest, journal.Attempt, mapping.Remote));
                applied.Revision.ShouldNotBeNull();
                applied.Error.ShouldBeNull();
                (verified.Digest, verified.Operation, verified.Attempt, verified.Revision, verified.PublishedKey)
                    .ShouldBe((applied.Digest, applied.Operation, applied.Attempt, applied.Revision, applied.PublishedKey));
                verified.Error.ShouldBeNull();
            }

            if (scenario is "pin" or "origin" or "unknown")
            {
                var bench = (await store.ReadAsync(ct)).Value.Benches.Single();
                bench.Selectors.ShouldBeEmpty("Pending or unknown publication has no permanent pin.");
                if (scenario == "pin")
                {
                    var extra = new Selector(SelectorKind.Pin, ItemKey.Remote(fixture.Stamp.Connection.Provider,
                        fixture.Stamp.Connection.ResourceScope, "44"), connection: fixture.Stamp.Connection);
                    (await store.SaveAsync(new(bench.Id, bench.Name, bench.Selectors.Add(extra)), bench.Revision, ct))
                        .IsSuccess.ShouldBeTrue();
                }
                var pending = (await Ok(seedPath, HttpMethod.Get))["seeds"]!.AsArray().ShouldHaveSingleItem()!;
                pending["publication"]!["status"]!.GetValue<string>().ShouldBe(expectedStatus);
                pending["localId"]!.GetValue<string>().ShouldBe(seedKey.LocalId.ToString("D"));
                pending["captureId"]!.GetValue<string>().ShouldBe(captured.Id.ToString("D"));
                pending["hasDraft"]!.GetValue<bool>().ShouldBe(scenario == "unknown");
                var reference = new JsonObject
                {
                    ["sourceId"] = sourceId, ["benchId"] = fixture.Bench.ToString("D"),
                    ["captureId"] = captured.Id.ToString("D"),
                };
                (await Ok("/seeds/observe", HttpMethod.Post, reference))["status"]!.GetValue<string>().ShouldBe(expectedStatus);
                reference["digest"] = new string('0', 64);
                (await Send("/seeds/delivery", HttpMethod.Post, reference)).Status.ShouldBe(HttpStatusCode.Conflict);
                reference["digest"] = proposal.Digest;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    var delivered = await Ok("/seeds/delivery", HttpMethod.Post, reference);
                    delivered["status"]!.GetValue<string>().ShouldBe(scenario == "unknown" ? "Protected" : "Complete");
                    delivered["captureId"]!.GetValue<string>().ShouldBe(captured.Id.ToString("D"));
                    delivered["digest"]!.GetValue<string>().ShouldBe(proposal.Digest);
                }
            }
            var final = (await ReadHumanHostAsync(fixture.Root, fixture.Stamp, false, ct)).Value.Snapshot;
            final.Proposals.ShouldHaveSingleItem().Digest.ShouldBe(proposal.Digest);
            var finalJournal = final.Journal.ShouldHaveSingleItem();
            finalJournal.Attempt.ShouldBe(journal.Attempt);
            finalJournal.Status.ShouldBe(journal.Status);
            finalJournal.Receipts.Length.ShouldBe(journal.Receipts.Length);
            foreach (var (actual, original) in finalJournal.Receipts.Zip(journal.Receipts))
            {
                (actual.Digest, actual.Operation, actual.Attempt, actual.Status, actual.Revision, actual.PublishedKey)
                    .ShouldBe((original.Digest, original.Operation, original.Attempt, original.Status, original.Revision, original.PublishedKey));
                actual.Evidence.ShouldBe(original.Evidence);
                (actual.Error?.Code, actual.Error?.Message).ShouldBe((original.Error?.Code, original.Error?.Message));
                if (original.Error is not null) actual.Error.ShouldNotBeNull().Evidence.ShouldBe(original.Error.Evidence);
            }
            final.Mappings.ShouldBe(before.Mappings);
            var finalCapture = (await store.ReadEditorCapturesAsync(ct)).Value.Single(capture => capture.Id == captured.Id);
            finalCapture.SeedProposal.ShouldBe(captured.SeedProposal);
            finalCapture.Settled.ShouldBe(scenario != "unknown");
            var selectors = (await store.ReadAsync(ct)).Value.Benches.Single(bench => bench.Id == fixture.Bench).Selectors;
            selectors.ShouldNotContain(selector => selector.Item != null && selector.Item.IsLocal);
            if (scenario == "unknown")
            {
                final.Drafts.ShouldHaveSingleItem().Key.ShouldBe(seedKey);
                selectors.ShouldBeEmpty();
            }
            else
            {
                var remote = final.Mappings.Single().Remote;
                selectors.Count(selector => selector.Item == remote && selector.Connection == fixture.Stamp.Connection).ShouldBe(1);
                final.Items.Single(item => item.Key == remote).Fields["System.Title"].Text.ShouldBe("Explicit HTTP seed");
                var finalSource = (await View())["sources"]![0]!;
                var finalRows = finalSource["items"]!.AsArray();
                if (scenario == "pin")
                {
                    finalSource["errorCode"]!.GetValue<string>().ShouldBe("Unavailable");
                    finalRows.ShouldBeEmpty("Uncached unrelated pin 44 withholds the projection, not the verified delivery.");
                    selectors.ShouldContain(selector => selector.Item != null && selector.Item.NativeId == "44");
                }
                else
                {
                    finalSource["errorCode"].ShouldBeNull();
                    finalRows.Single(item => item!["locator"]!.GetValue<string>() == remote.NativeId)!
                        ["benchMember"]!.GetValue<bool>().ShouldBeTrue();
                }
                finalRows.ShouldNotContain(item => item!["id"]!.GetValue<string>() == itemId);
            }
            if (scenario == "ownership")
            {
                final.Drafts.Select(draft => draft.Key).OrderBy(key => key.LocalId)
                    .ShouldBe(foreign.Take(2).OrderBy(key => key.LocalId));
                using var editor = (await OpenHumanHostAsync(fixture.Root, fixture.Bench, fixture.Stamp, ct)).Value;
                (await editor.ReadOwnedSeedsAsync(ct)).Value.ShouldBeEmpty();
                (await store.ReadPlanSeedOwnerAsync(foreign[2].LocalId, ct)).IsSuccess.ShouldBeTrue();
            }
            _transport.PostCount.ShouldBe(1, "Observation and delivery must never replay creation.");
            HumanPatchAttempts.ShouldBe(0);
            _transport.CommittedWrites.ShouldBe(1);
            await Report();
        }
        finally
        {
            _transport.FailCurrentReadsAfterCommit = false;
            _transport.AfterCommit = null;
            sessions.Revoke(owned);
            await app.StopAsync(CancellationToken.None);
        }
    }
}
