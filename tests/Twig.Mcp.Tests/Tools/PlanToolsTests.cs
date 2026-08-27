using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using NSubstitute;
using Shouldly;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Mcp.Services;
using Twig.Mcp.Services.Batch;
using Twig.Mcp.Tools;
using Xunit;

namespace Twig.Mcp.Tests.Tools;

/// <summary>
/// Focused unit tests for <see cref="PlanTools"/>. Each tool is proven to route through
/// <see cref="IPlanLifecycleService"/> and <see cref="IPendingChangeReader"/> and to enforce
/// the input contract stated in the plan surface contract — file present, apply's strict
/// confirmed+digest pair, seed's negative-only alias — without reaching either downstream
/// service on rejection.
/// </summary>
public sealed class PlanToolsTests
{
    private static readonly Connection TestConnection = new("testorg", "testproject");
    private const string ValidDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string OtherDigest = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

    // ── twig_plan_validate ──────────────────────────────────────────

    [Fact]
    public async Task Validate_MissingFile_ReturnsInvalidInput()
    {
        var (sut, lifecycle, _) = BuildSut();

        var result = await sut.PlanValidate(file: "");

        result.IsError.ShouldBe(true);
        GetError(result).Code.ShouldBe(McpErrorCode.InvalidInput);
        await lifecycle.DidNotReceiveWithAnyArgs()
            .ValidateAsync(default!, default);
    }

    [Fact]
    public async Task Validate_HappyPath_RoutesFileThroughLifecycle()
    {
        var (sut, lifecycle, _) = BuildSut();
        lifecycle.ValidateAsync("plan.json", Arg.Any<CancellationToken>()).Returns(
            new PlanValidationResult { Issues = [], Digest = ValidDigest });

        var result = await sut.PlanValidate("plan.json");

        result.IsError.ShouldBeNull();
        var data = ParseData(result);
        data.GetProperty("isValid").GetBoolean().ShouldBeTrue();
        data.GetProperty("digest").GetString().ShouldBe(ValidDigest);
        data.GetProperty("issues").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Validate_InvalidPlan_ReportsIssuesVerbatim()
    {
        var (sut, lifecycle, _) = BuildSut();
        lifecycle.ValidateAsync("plan.json", Arg.Any<CancellationToken>()).Returns(
            new PlanValidationResult
            {
                Issues =
                [
                    new PlanValidationIssue
                    {
                        Code = PlanValidationCodes.WrongType,
                        Path = "/operations/0/kind",
                        Message = "Expected string.",
                    },
                ],
            });

        var data = ParseData(await sut.PlanValidate("plan.json"));
        data.GetProperty("isValid").GetBoolean().ShouldBeFalse();
        data.GetProperty("digest").ValueKind.ShouldBe(JsonValueKind.Null);
        var issue = data.GetProperty("issues")[0];
        issue.GetProperty("code").GetString().ShouldBe(PlanValidationCodes.WrongType);
        issue.GetProperty("path").GetString().ShouldBe("/operations/0/kind");
        issue.GetProperty("message").GetString().ShouldBe("Expected string.");
    }

    // ── twig_plan_preview ───────────────────────────────────────────

    [Fact]
    public async Task Preview_MissingFile_ReturnsInvalidInput()
    {
        var (sut, lifecycle, _) = BuildSut();

        var result = await sut.PlanPreview(file: "");

        result.IsError.ShouldBe(true);
        await lifecycle.DidNotReceiveWithAnyArgs().PreviewAsync(default!, default);
    }

    [Fact]
    public async Task Preview_EmitsDigest_CanApply_Pending_And_Operations()
    {
        var (sut, lifecycle, _) = BuildSut();
        var pending = new PendingChangeDetail(
            42, -1, "field", "System.Title", null, "old", "new",
            new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero), SeedRemap: null);
        lifecycle.PreviewAsync("plan.json", Arg.Any<CancellationToken>()).Returns(
            new PlanPreviewResult
            {
                Digest = ValidDigest,
                Operations = [],
                Issues = [],
                Workspace = new PlanWorkspace { Organization = "testorg", Project = "testproject" },
                PendingChanges = [pending],
                CanApply = false,
            });

        var data = ParseData(await sut.PlanPreview("plan.json"));
        data.GetProperty("digest").GetString().ShouldBe(ValidDigest);
        data.GetProperty("canApply").GetBoolean().ShouldBeFalse();
        data.GetProperty("workspace").GetProperty("organization").GetString().ShouldBe("testorg");
        data.GetProperty("pendingChanges").GetArrayLength().ShouldBe(1);
        var row = data.GetProperty("pendingChanges")[0];
        row.GetProperty("pendingChangeId").GetInt64().ShouldBe(42);
        row.GetProperty("workItemId").GetInt32().ShouldBe(-1);
        row.GetProperty("kind").GetString().ShouldBe("field");
        row.GetProperty("field").GetString().ShouldBe("System.Title");
        row.GetProperty("oldValue").GetString().ShouldBe("old");
        row.GetProperty("newValue").GetString().ShouldBe("new");
        row.GetProperty("seedRemap").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    // ── twig_plan_apply ─────────────────────────────────────────────

    [Fact]
    public async Task Apply_ConfirmedFalse_ReturnsConfirmationRequired_WithoutCallingLifecycle()
    {
        var (sut, lifecycle, _) = BuildSut();

        var result = await sut.PlanApply("plan.json", confirmed: false, confirmedDigest: ValidDigest);

        result.IsError.ShouldBe(true);
        GetError(result).Code.ShouldBe(McpErrorCode.ConfirmationRequired);
        await lifecycle.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);
    }

    [Fact]
    public async Task Apply_MissingDigest_ReturnsInvalidInput()
    {
        var (sut, lifecycle, _) = BuildSut();

        var result = await sut.PlanApply("plan.json", confirmed: true, confirmedDigest: "");

        result.IsError.ShouldBe(true);
        GetError(result).Code.ShouldBe(McpErrorCode.InvalidInput);
        await lifecycle.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("not-a-digest")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcde")]      // 63 chars
    [InlineData("0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef")]     // uppercase
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdefX")]    // 65 chars
    public async Task Apply_MalformedDigest_ReturnsInvalidInput(string digest)
    {
        var (sut, lifecycle, _) = BuildSut();

        var result = await sut.PlanApply("plan.json", confirmed: true, confirmedDigest: digest);

        result.IsError.ShouldBe(true);
        GetError(result).Code.ShouldBe(McpErrorCode.InvalidInput);
        await lifecycle.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, default);
    }

    [Fact]
    public async Task Apply_HappyPath_ForwardsExactDigestAndReturnsResult()
    {
        var (sut, lifecycle, _) = BuildSut();
        lifecycle.ApplyAsync("plan.json", ValidDigest, Arg.Any<CancellationToken>())
            .Returns(new PlanApplyResult
            {
                Digest = ValidDigest,
                Operations = [SampleVerifiedOp()],
                Failed = false,
            });

        var result = await sut.PlanApply("plan.json", confirmed: true, confirmedDigest: ValidDigest);

        result.IsError.ShouldBeNull();
        var data = ParseData(result);
        data.GetProperty("digest").GetString().ShouldBe(ValidDigest);
        data.GetProperty("failed").GetBoolean().ShouldBeFalse();
        data.GetProperty("operations").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task Apply_VerifiedWithWarning_EmitsWarningWithoutMarkingTheCallAnError()
    {
        // AB#754/755: a normalization warning is additive detail on a landed operation. It
        // must reach the caller AND must not flip failed/IsError — an agent that treats a
        // warning as a failure would re-drive a mutation that already succeeded.
        var (sut, lifecycle, _) = BuildSut();
        lifecycle.ApplyAsync("plan.json", ValidDigest, Arg.Any<CancellationToken>())
            .Returns(new PlanApplyResult
            {
                Digest = ValidDigest,
                Operations =
                [
                    SampleVerifiedOp() with
                    {
                        Warning = "ADO normalized server-generated field(s) after apply: "
                            + "Microsoft.VSTS.Common.ClosedDate.",
                    },
                ],
                Failed = false,
            });

        var result = await sut.PlanApply("plan.json", confirmed: true, confirmedDigest: ValidDigest);

        result.IsError.ShouldBeNull();
        var data = ParseData(result);
        data.GetProperty("failed").GetBoolean().ShouldBeFalse();
        var op = data.GetProperty("operations")[0];
        op.GetProperty("state").GetString().ShouldBe(nameof(PlanOperationState.Verified));
        op.GetProperty("warning").GetString()!.ShouldContain("ClosedDate");
        op.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Apply_FailureResult_EmitsFullJournalProjectionWithFailedTrue()
    {
        var (sut, lifecycle, _) = BuildSut();
        var appliedAt = new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero);
        var startedAt = appliedAt.AddSeconds(-1);
        lifecycle.ApplyAsync("plan.json", ValidDigest, Arg.Any<CancellationToken>())
            .Returns(new PlanApplyResult
            {
                Digest = ValidDigest,
                Operations =
                [
                    new PlanJournalOperation
                    {
                        Ordinal = 0,
                        OpId = "op-verified",
                        Kind = PlanOperationKind.Batch,
                        State = PlanOperationState.Verified,
                        RequestJson = "{}",
                        StartedAt = startedAt,
                        AppliedAt = appliedAt,
                        VerifiedAt = appliedAt.AddSeconds(1),
                        ResultJson = "{\"revision\":2}",
                    },
                    new PlanJournalOperation
                    {
                        Ordinal = 1,
                        OpId = "op-failed",
                        Kind = PlanOperationKind.Batch,
                        State = PlanOperationState.Failed,
                        RequestJson = "{}",
                        StartedAt = startedAt,
                        AppliedAt = appliedAt,
                        Error = "412 precondition failed",
                    },
                ],
                Failed = true,
                Error = "One or more operations failed.",
            });

        var result = await sut.PlanApply("plan.json", confirmed: true, confirmedDigest: ValidDigest);

        // 🔴 Failure MUST carry the full journal payload (digest, failed:true, error,
        //   per-operation state/timings/errors) AND set the transport-level IsError
        //   flag so a sequential BatchExecutionEngine stops the enclosing batch.
        //   Direct clients still read the same shape as a successful apply and can
        //   drive recovery without a second round trip.
        result.IsError.ShouldBe(true);
        var data = ParseData(result);
        data.GetProperty("digest").GetString().ShouldBe(ValidDigest);
        data.GetProperty("failed").GetBoolean().ShouldBeTrue();
        data.GetProperty("error").GetString().ShouldBe("One or more operations failed.");

        var ops = data.GetProperty("operations");
        ops.GetArrayLength().ShouldBe(2);

        var verified = ops[0];
        verified.GetProperty("ordinal").GetInt32().ShouldBe(0);
        verified.GetProperty("opId").GetString().ShouldBe("op-verified");
        verified.GetProperty("state").GetString().ShouldBe(nameof(PlanOperationState.Verified));
        verified.GetProperty("startedAt").GetString().ShouldBe(
            startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        verified.GetProperty("appliedAt").GetString().ShouldBe(
            appliedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        verified.GetProperty("verifiedAt").GetString().ShouldBe(
            appliedAt.AddSeconds(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        verified.GetProperty("result").GetString().ShouldBe("{\"revision\":2}");
        verified.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
        // AB#754/755: the key is ALWAYS emitted so a caller can read it without probing;
        // null means no normalization was observed.
        verified.GetProperty("warning").ValueKind.ShouldBe(JsonValueKind.Null);

        var failed = ops[1];
        failed.GetProperty("ordinal").GetInt32().ShouldBe(1);
        failed.GetProperty("opId").GetString().ShouldBe("op-failed");
        failed.GetProperty("state").GetString().ShouldBe(nameof(PlanOperationState.Failed));
        failed.GetProperty("startedAt").GetString().ShouldBe(
            startedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        failed.GetProperty("appliedAt").GetString().ShouldBe(
            appliedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        failed.GetProperty("verifiedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        failed.GetProperty("result").ValueKind.ShouldBe(JsonValueKind.Null);
        failed.GetProperty("error").GetString().ShouldBe("412 precondition failed");
    }

    [Fact]
    public async Task Apply_FailureResult_InSequentialBatch_StopsNextStepButRetainsPayload()
    {
        // 🔴 A failed apply must (a) stop a sequential BatchExecutionEngine from
        //   dispatching the next step, and (b) preserve the full journal JSON so the
        //   caller can recover without a second status round-trip. The engine reads
        //   only CallToolResult.IsError to gate fail-fast and captures the text
        //   content as StepResult.Error — proving both properties in one wire test.
        var (sut, lifecycle, _) = BuildSut();
        lifecycle.ApplyAsync("plan.json", ValidDigest, Arg.Any<CancellationToken>())
            .Returns(new PlanApplyResult
            {
                Digest = ValidDigest,
                Operations = [SampleVerifiedOp()],
                Failed = true,
                Error = "One or more operations failed.",
            });

        var dispatcher = new PlanToolsBatchDispatcher(sut);
        var engine = new BatchExecutionEngine(dispatcher);

        var graph = new BatchGraph(
            new SequenceNode(
            [
                new StepNode(0, "twig_plan_apply", new Dictionary<string, object?>
                {
                    ["file"] = "plan.json",
                    ["confirmed"] = true,
                    ["confirmedDigest"] = ValidDigest,
                }),
                new StepNode(1, "twig_plan_status", new Dictionary<string, object?>
                {
                    ["file"] = "plan.json",
                }),
            ]),
            TotalStepCount: 2);

        var result = await engine.ExecuteAsync(graph, TimeSpan.FromSeconds(30), null, CancellationToken.None);

        result.Steps.Count.ShouldBe(2);
        result.Steps[0].Status.ShouldBe(StepStatus.Failed);
        result.Steps[1].Status.ShouldBe(StepStatus.Skipped);
        result.Steps[1].Error!.ShouldContain("prior step failure");

        // Payload is preserved verbatim: the failed step's Error carries the full
        // envelope, with digest, failed:true, error, and operations.
        using var doc = JsonDocument.Parse(result.Steps[0].Error!);
        var data = doc.RootElement.GetProperty("data");
        data.GetProperty("digest").GetString().ShouldBe(ValidDigest);
        data.GetProperty("failed").GetBoolean().ShouldBeTrue();
        data.GetProperty("error").GetString().ShouldBe("One or more operations failed.");
        data.GetProperty("operations").GetArrayLength().ShouldBe(1);

        // Second step never reached the lifecycle — StatusAsync was not called.
        await lifecycle.DidNotReceiveWithAnyArgs().StatusAsync(default!, default);
    }

    /// <summary>
    /// Test dispatcher that routes a single tool name to a real <see cref="PlanTools"/>
    /// instance. Kept intentionally minimal — only the two tools used by the
    /// sequential-batch fail-fast test are wired.
    /// </summary>
    private sealed class PlanToolsBatchDispatcher(PlanTools tools) : IToolDispatcher
    {
        public async Task<CallToolResult> DispatchAsync(
            string toolName,
            IReadOnlyDictionary<string, object?> args,
            string? workspaceOverride,
            CancellationToken ct) => toolName switch
        {
            "twig_plan_apply" => await tools.PlanApply(
                (string)args["file"]!,
                (bool)args["confirmed"]!,
                (string)args["confirmedDigest"]!,
                workspace: workspaceOverride,
                ct: ct),
            "twig_plan_status" => await tools.PlanStatus(
                (string)args["file"]!,
                workspace: workspaceOverride,
                ct: ct),
            _ => throw new InvalidOperationException($"Unexpected tool {toolName}"),
        };
    }

    // ── twig_plan_status ────────────────────────────────────────────

    [Fact]
    public async Task Status_NullResult_EmitsNullStatus()
    {
        var (sut, lifecycle, _) = BuildSut();
        lifecycle.StatusAsync("plan.json", Arg.Any<CancellationToken>()).Returns((PlanStatusResult?)null);

        var data = ParseData(await sut.PlanStatus("plan.json"));
        data.GetProperty("status").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Status_ExistingJournal_EmitsFoundDigestStateAndOperations()
    {
        var (sut, lifecycle, _) = BuildSut();
        lifecycle.StatusAsync("plan.json", Arg.Any<CancellationToken>()).Returns(new PlanStatusResult
        {
            Found = true,
            Digest = ValidDigest,
            State = PlanOperationState.Applying,
            Operations = [SampleVerifiedOp()],
        });

        var data = ParseData(await sut.PlanStatus("plan.json"));
        var status = data.GetProperty("status");
        status.GetProperty("found").GetBoolean().ShouldBeTrue();
        status.GetProperty("digest").GetString().ShouldBe(ValidDigest);
        status.GetProperty("state").GetString().ShouldBe(nameof(PlanOperationState.Applying));
        status.GetProperty("issues").GetArrayLength().ShouldBe(0);
        status.GetProperty("operations").GetArrayLength().ShouldBe(1);
        status.GetProperty("error").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Status_InputError_EmitsFoundFalseWithIssuesAndNoState()
    {
        // 🔴 The lifecycle contract distinguishes "file broken" (Found=false with Issues)
        //   from "valid file, no journal" (null) — the projection MUST preserve both, so
        //   a caller can tell "please fix your plan" from "you have not previewed yet".
        var (sut, lifecycle, _) = BuildSut();
        lifecycle.StatusAsync("plan.json", Arg.Any<CancellationToken>()).Returns(new PlanStatusResult
        {
            Found = false,
            Digest = null,
            State = null,
            Issues =
            [
                new PlanValidationIssue
                {
                    Code = PlanValidationCodes.WrongType,
                    Path = "/operations/0/kind",
                    Message = "Expected string.",
                },
            ],
        });

        var data = ParseData(await sut.PlanStatus("plan.json"));
        var status = data.GetProperty("status");
        status.GetProperty("found").GetBoolean().ShouldBeFalse();
        status.GetProperty("digest").ValueKind.ShouldBe(JsonValueKind.Null);
        status.GetProperty("state").ValueKind.ShouldBe(JsonValueKind.Null);
        status.GetProperty("operations").GetArrayLength().ShouldBe(0);
        var issue = status.GetProperty("issues")[0];
        issue.GetProperty("code").GetString().ShouldBe(PlanValidationCodes.WrongType);
        issue.GetProperty("path").GetString().ShouldBe("/operations/0/kind");
        issue.GetProperty("message").GetString().ShouldBe("Expected string.");
    }

    [Fact]
    public async Task Status_ExistingJournalWithError_SurfacesJournalError()
    {
        var (sut, lifecycle, _) = BuildSut();
        lifecycle.StatusAsync("plan.json", Arg.Any<CancellationToken>()).Returns(new PlanStatusResult
        {
            Found = true,
            Digest = ValidDigest,
            State = PlanOperationState.Failed,
            Operations = [SampleVerifiedOp()],
            Error = "One or more operations failed.",
        });

        var data = ParseData(await sut.PlanStatus("plan.json"));
        var status = data.GetProperty("status");
        status.GetProperty("found").GetBoolean().ShouldBeTrue();
        status.GetProperty("state").GetString().ShouldBe(nameof(PlanOperationState.Failed));
        status.GetProperty("error").GetString().ShouldBe("One or more operations failed.");
    }

    // ── twig_plan_seed ──────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(42)]
    public async Task Seed_NonNegativeId_ReturnsInvalidInputWithoutCallingLifecycle(int id)
    {
        var (sut, lifecycle, _) = BuildSut();

        var result = await sut.PlanSeed(id);

        result.IsError.ShouldBe(true);
        GetError(result).Code.ShouldBe(McpErrorCode.InvalidInput);
        await lifecycle.DidNotReceiveWithAnyArgs().DescribeSeedAsync(default, default);
    }

    [Fact]
    public async Task Seed_UnknownAlias_EmitsNullDescriptor()
    {
        var (sut, lifecycle, _) = BuildSut();
        lifecycle.DescribeSeedAsync(-3, Arg.Any<CancellationToken>()).Returns((PlanSeedDescriptor?)null);

        var data = ParseData(await sut.PlanSeed(-3));
        data.GetProperty("descriptor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Seed_KnownAlias_EmitsIdentityFingerprintTitleAndType()
    {
        var (sut, lifecycle, _) = BuildSut();
        var identity = StagedIdentity.New();
        StagedAlias.TryFrom(-7, out var alias).ShouldBeTrue();
        lifecycle.DescribeSeedAsync(-7, Arg.Any<CancellationToken>()).Returns(new PlanSeedDescriptor
        {
            Identity = identity,
            Alias = alias,
            Fingerprint = ValidDigest,
            Title = "Investigate flaky test",
            Type = "Task",
        });

        var data = ParseData(await sut.PlanSeed(-7));
        var descriptor = data.GetProperty("descriptor");
        descriptor.GetProperty("stagedIdentity").GetString().ShouldBe(identity.ToString());
        descriptor.GetProperty("stagedAlias").GetInt32().ShouldBe(-7);
        descriptor.GetProperty("fingerprint").GetString().ShouldBe(ValidDigest);
        descriptor.GetProperty("title").GetString().ShouldBe("Investigate flaky test");
        descriptor.GetProperty("type").GetString().ShouldBe("Task");
    }

    // ── twig_pending ────────────────────────────────────────────────

    [Fact]
    public async Task Pending_UsesPendingChangeReader_And_EmitsRawOpaqueStrings()
    {
        var (sut, _, pendingReader) = BuildSut();
        var opaque = "<html><b>&raw &lt;bytes&gt;</b></html>";
        pendingReader.GetAllChangesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new PendingChangeDetail(
                1, -5, "note", null, opaque, null, opaque,
                new DateTimeOffset(2026, 8, 22, 12, 34, 56, TimeSpan.Zero),
                new SeedRemapIdentity(StagedIdentity.FromGuid(Guid.Parse("00000000-0000-7000-8000-000000000000")),
                    Alias(-5), PublishedWorkItemId: null)),
        ]);

        var data = ParseData(await sut.Pending());
        data.GetProperty("pendingChanges").GetArrayLength().ShouldBe(1);
        var row = data.GetProperty("pendingChanges")[0];
        row.GetProperty("pendingChangeId").GetInt64().ShouldBe(1);
        row.GetProperty("kind").GetString().ShouldBe("note");
        row.GetProperty("field").ValueKind.ShouldBe(JsonValueKind.Null);
        row.GetProperty("note").GetString().ShouldBe(opaque);
        row.GetProperty("newValue").GetString().ShouldBe(opaque);
        var remap = row.GetProperty("seedRemap");
        remap.GetProperty("stagedIdentity").GetString().ShouldBe("00000000-0000-7000-8000-000000000000");
        remap.GetProperty("stagedAlias").GetInt32().ShouldBe(-5);
        remap.GetProperty("publishedWorkItemId").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Pending_DoesNotRenderOrCoalesce_MultipleRowsSameField()
    {
        var (sut, _, pendingReader) = BuildSut();
        var t = DateTimeOffset.UtcNow;
        pendingReader.GetAllChangesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new PendingChangeDetail(1, 100, "field", "System.Title", null, "A", "B", t, null),
            new PendingChangeDetail(2, 100, "field", "System.Title", null, "B", "C", t, null),
        ]);

        var rows = ParseData(await sut.Pending()).GetProperty("pendingChanges");
        rows.GetArrayLength().ShouldBe(2);
        rows[0].GetProperty("pendingChangeId").GetInt64().ShouldBe(1);
        rows[1].GetProperty("pendingChangeId").GetInt64().ShouldBe(2);
    }

    [Fact]
    public async Task Pending_HtmlUnsafeRowStrings_AreScriptEscapedOnWire_ButDecodeExact()
    {
        // 🔴 Pending rows are opaque bytes — a title like "<script>alert('x')</script>" MUST
        //   still be safe to embed in an HTML page. The outer envelope uses
        //   UnsafeRelaxedJsonEscaping (to keep non-ASCII human text readable), so opaque
        //   pending values are pinned to the HTML-safe encoder inside that outer writer.
        //   The escaping only lives on the wire — a normal JSON parse round-trips the
        //   exact original bytes.
        var (sut, _, pendingReader) = BuildSut();
        var unsafeText = "<script>alert('x')</script>&\"amp;\"";
        pendingReader.GetAllChangesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new PendingChangeDetail(
                7, -1, "note", "System.Title", unsafeText, null, unsafeText,
                new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero), null),
        ]);

        var result = await sut.Pending();
        var raw = ((TextContentBlock)result.Content[0]).Text!;

        // Wire form MUST NOT contain the raw HTML metacharacters — every unsafe byte
        // is escaped to its \u00XX form.
        raw.ShouldNotContain("<script>");
        raw.ShouldNotContain("</script>");
        raw.ShouldContain("\\u003C");   // '<'
        raw.ShouldContain("\\u003E");   // '>'
        raw.ShouldContain("\\u0026");   // '&'
        raw.ShouldContain("\\u0027");   // '\''

        // But a normal parse decodes to the exact original string.
        using var doc = JsonDocument.Parse(raw);
        var row = doc.RootElement.GetProperty("data").GetProperty("pendingChanges")[0];
        row.GetProperty("field").GetString().ShouldBe("System.Title");
        row.GetProperty("note").GetString().ShouldBe(unsafeText);
        row.GetProperty("newValue").GetString().ShouldBe(unsafeText);
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static PlanJournalOperation SampleVerifiedOp() =>
        new()
        {
            Ordinal = 0,
            OpId = "op-1",
            Kind = PlanOperationKind.Batch,
            State = PlanOperationState.Verified,
            RequestJson = "{}",
            VerifiedAt = DateTimeOffset.UtcNow,
            ResultJson = "{\"revision\":2}",
        };

    private static StagedAlias Alias(int value)
    {
        StagedAlias.TryFrom(value, out var alias).ShouldBeTrue();
        return alias;
    }

    private static JsonElement ParseData(CallToolResult result)
    {
        var text = ((TextContentBlock)result.Content[0]).Text!;
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        return root.TryGetProperty("data", out var d) ? d.Clone() : root.Clone();
    }

    private static (string Code, string Message) GetError(CallToolResult result)
    {
        var text = ((TextContentBlock)result.Content[0]).Text!;
        using var doc = JsonDocument.Parse(text);
        var err = doc.RootElement.GetProperty("error");
        return (err.GetProperty("code").GetString()!, err.GetProperty("message").GetString()!);
    }

    private static (PlanTools Sut, IPlanLifecycleService Lifecycle, IPendingChangeReader Reader) BuildSut()
    {
        var lifecycle = Substitute.For<IPlanLifecycleService>();
        var reader = Substitute.For<IPendingChangeReader>();

        var services = new ServiceCollection();
        services.AddSingleton(lifecycle);
        services.AddSingleton(reader);
        services.AddSingleton(new TwigConfiguration());
        services.AddSingleton(TwigPaths.ForContext(
            Path.GetTempPath(), TestConnection.Org, TestConnection.Project));
        services.AddSingleton(Substitute.For<IContextStore>());
        services.AddSingleton(Substitute.For<IWorkItemRepository>());

        var provider = services.BuildServiceProvider();
        var scope = new ConnectionScope(TestConnection, provider);

        var registry = Substitute.For<IConnectionRegistry>();
        registry.Workspaces.Returns([TestConnection]);
        registry.IsSingleWorkspace.Returns(true);

        var factory = Substitute.For<IConnectionScopeFactory>();
        factory.GetOrCreate(Arg.Any<Connection>()).Returns(scope);

        var resolver = new ConnectionResolver(registry, factory);
        var sut = new PlanTools(resolver);
        return (sut, lifecycle, reader);
    }
}
