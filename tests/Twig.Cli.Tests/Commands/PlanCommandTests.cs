using NSubstitute;
using Shouldly;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;
using Xunit;

namespace Twig.Cli.Tests.Commands;

/// <summary>
/// The CLI adapter for <c>twig plan …</c> only decides three things: exit code, output
/// shape, and argument surface. Everything else is <see cref="IPlanLifecycleService"/>.
/// These tests assert those three, using a substituted lifecycle so no ADO, SQLite, or
/// parser ever runs — the adapter's whole job is the mapping between the domain record
/// and the two output shapes.
/// </summary>
public sealed class PlanCommandTests
{
    private readonly IPlanLifecycleService _lifecycle = Substitute.For<IPlanLifecycleService>();
    private readonly OutputFormatterFactory _formatterFactory =
        new(new HumanOutputFormatter());
    private readonly ISessionSteeringModeProvider _steering = new UnresolvedSessionSteeringModeProvider();

    private PlanCommand CreateCommand(StringWriter stdout, StringWriter stderr)
        => new(_lifecycle, _formatterFactory, _steering, TimeProvider.System, new RendererFactory(), stdout, stderr);

    // ── usage: missing arguments are exit 2 (never routed to lifecycle) ──

    [Fact]
    public async Task Validate_MissingFile_ExitsUsageWithoutCallingLifecycle()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cmd = CreateCommand(stdout, stderr);

        var exit = await cmd.ValidateAsync(file: null, outputFormat: "human", ct: default);

        exit.ShouldBe(2);
        stderr.ToString().ShouldContain("--file");
        await _lifecycle.DidNotReceive().ValidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_MissingFile_ExitsUsageWithoutCallingLifecycle()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cmd = CreateCommand(stdout, stderr);

        var exit = await cmd.PreviewAsync(file: "  ", outputFormat: "human", ct: default);

        exit.ShouldBe(2);
        await _lifecycle.DidNotReceive().PreviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_MissingFile_ExitsUsageWithoutCallingLifecycle()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cmd = CreateCommand(stdout, stderr);

        var exit = await cmd.ApplyAsync(file: null, confirmedDigest: "deadbeef", authorizerIdentity: "Test Authorizer", rationale: null, outputFormat: "human", ct: default);

        exit.ShouldBe(2);
        await _lifecycle.DidNotReceive().ApplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ProposalAuthorization?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_MissingConfirm_IsUsageError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cmd = CreateCommand(stdout, stderr);

        var exit = await cmd.ApplyAsync(file: "plan.json", confirmedDigest: null, authorizerIdentity: "Test Authorizer", rationale: null, outputFormat: "human", ct: default);

        exit.ShouldBe(2);
        stderr.ToString().ShouldContain("--confirm");
        await _lifecycle.DidNotReceive().ApplyAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ProposalAuthorization?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Status_MissingFile_IsUsageError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cmd = CreateCommand(stdout, stderr);

        var exit = await cmd.StatusAsync(file: null, outputFormat: "human", ct: default);

        exit.ShouldBe(2);
        await _lifecycle.DidNotReceive().StatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Seed_MissingId_IsUsageError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var cmd = CreateCommand(stdout, stderr);

        var exit = await cmd.DescribeSeedAsync(id: null, outputFormat: "human", ct: default);

        exit.ShouldBe(2);
        await _lifecycle.DidNotReceive().DescribeSeedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // ── validate: exit code follows result.IsValid ──

    [Fact]
    public async Task Validate_ValidPlan_ExitsZero_AndJsonCarriesDigest()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .ValidateAsync("plan.json", Arg.Any<CancellationToken>())
            .Returns(new PlanValidationResult
            {
                Issues = Array.Empty<PlanValidationIssue>(),
                CanonicalJson = "{}",
                Digest = "abc123",
                Plan = null,
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.ValidateAsync("plan.json", outputFormat: "json", ct: default);

        exit.ShouldBe(0);
        stdout.ToString().ShouldContain("\"valid\": true");
        stdout.ToString().ShouldContain("abc123");
    }

    [Fact]
    public async Task Validate_InvalidPlan_ExitsOne_AndJsonListsIssueCode()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .ValidateAsync("plan.json", Arg.Any<CancellationToken>())
            .Returns(new PlanValidationResult
            {
                Issues =
                [
                    new PlanValidationIssue
                    {
                        Code = PlanValidationCodes.MissingProperty,
                        Path = "/operations/0",
                        Message = "workItemId is required",
                    },
                ],
                Digest = null,
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.ValidateAsync("plan.json", outputFormat: "json", ct: default);

        exit.ShouldBe(1);
        stdout.ToString().ShouldContain(PlanValidationCodes.MissingProperty);
    }

    // ── preview: pendingChanges and canApply appear in the machine surface ──

    [Fact]
    public async Task Preview_ValidWithoutPending_ExitsZero_AndJsonCanApplyTrue()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .PreviewAsync("plan.json", Arg.Any<CancellationToken>())
            .Returns(new PlanPreviewResult
            {
                Digest = "digest-a",
                Operations = Array.Empty<PlanOperationDefinition>(),
                Issues = Array.Empty<PlanValidationIssue>(),
                PendingChanges = Array.Empty<PendingChangeDetail>(),
                CanApply = true,
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.PreviewAsync("plan.json", outputFormat: "json", ct: default);

        exit.ShouldBe(0);
        var body = stdout.ToString();
        body.ShouldContain("\"canApply\": true");
        body.ShouldContain("\"pendingChanges\"");
        body.ShouldContain("digest-a");
    }

    [Fact]
    public async Task Preview_WithPending_CanApplyFalse_AndRawValuesPreserved()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .PreviewAsync("plan.json", Arg.Any<CancellationToken>())
            .Returns(new PlanPreviewResult
            {
                Digest = "digest-b",
                Operations = Array.Empty<PlanOperationDefinition>(),
                Issues = Array.Empty<PlanValidationIssue>(),
                PendingChanges =
                [
                    new PendingChangeDetail(
                        PendingChangeId: 17,
                        WorkItemId: 42,
                        Kind: "batch",
                        Field: "System.State",
                        Note: null,
                        OldValue: "New",
                        NewValue: "  Active  ",
                        StagedAt: DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
                        SeedRemap: null),
                ],
                CanApply = false,
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.PreviewAsync("plan.json", outputFormat: "json", ct: default);

        exit.ShouldBe(0);
        var body = stdout.ToString();
        body.ShouldContain("\"canApply\": false");
        // Raw pending value strings must survive verbatim — no trim, no normalize.
        body.ShouldContain("  Active  ");
        body.ShouldContain("System.State");
    }

    [Fact]
    public async Task Preview_InvalidPlan_ExitsOne()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .PreviewAsync("plan.json", Arg.Any<CancellationToken>())
            .Returns(new PlanPreviewResult
            {
                Digest = null,
                Operations = Array.Empty<PlanOperationDefinition>(),
                Issues =
                [
                    new PlanValidationIssue { Code = PlanValidationCodes.JsonInvalid, Path = "", Message = "bad json" },
                ],
                PendingChanges = Array.Empty<PendingChangeDetail>(),
                CanApply = false,
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.PreviewAsync("plan.json", outputFormat: "human", ct: default);

        exit.ShouldBe(1);
    }

    // ── apply: failure state -> exit 1 ──

    [Fact]
    public async Task Apply_Success_ExitsZero_AndJsonReportsVerifiedState()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .ApplyAsync("plan.json", "abc", Arg.Any<ProposalAuthorization?>(), Arg.Any<CancellationToken>())
            .Returns(new PlanApplyResult
            {
                Digest = "abc",
                Failed = false,
                Operations =
                [
                    new PlanJournalOperation
                    {
                        Ordinal = 0,
                        OpId = "op-1",
                        Kind = PlanOperationKind.Batch,
                        State = PlanOperationState.Verified,
                        RequestJson = "{}",
                    },
                ],
                Error = null,
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.ApplyAsync("plan.json", "abc", authorizerIdentity: "Test Authorizer", rationale: null, outputFormat: "json", ct: default);

        exit.ShouldBe(0);
        stdout.ToString().ShouldContain("Verified");
        stdout.ToString().ShouldContain("op-1");
    }

    [Fact]
    public async Task Apply_VerifiedWithWarning_RendersWarningLineAndStillExitsZero()
    {
        // AB#754/755: the warning is rendered on its own line and the command still succeeds.
        // A Verified operation must not read as failed, and the exit code must not move —
        // a CI step keyed on exit status would otherwise break on a harmless normalization.
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .ApplyAsync("plan.json", "abc", Arg.Any<ProposalAuthorization?>(), Arg.Any<CancellationToken>())
            .Returns(new PlanApplyResult
            {
                Digest = "abc",
                Failed = false,
                Operations =
                [
                    new PlanJournalOperation
                    {
                        Ordinal = 0,
                        OpId = "op-1",
                        Kind = PlanOperationKind.Batch,
                        State = PlanOperationState.Verified,
                        RequestJson = "{}",
                        Warning = "ADO canonicalized HTML field(s) after apply: System.Description.",
                    },
                ],
                Error = null,
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.ApplyAsync("plan.json", "abc", authorizerIdentity: "Test Authorizer", rationale: null, outputFormat: "human", ct: default);

        exit.ShouldBe(0);
        var text = stdout.ToString();
        text.ShouldContain("Verified");
        text.ShouldContain("warning:");
        text.ShouldContain("System.Description");
        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_Failure_ExitsOne_AndSurfacesError()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .ApplyAsync("plan.json", "abc", Arg.Any<ProposalAuthorization?>(), Arg.Any<CancellationToken>())
            .Returns(new PlanApplyResult
            {
                Digest = "abc",
                Failed = true,
                Operations =
                [
                    new PlanJournalOperation
                    {
                        Ordinal = 0,
                        OpId = "op-1",
                        Kind = PlanOperationKind.Batch,
                        State = PlanOperationState.Failed,
                        RequestJson = "{}",
                        Error = "412",
                    },
                ],
                Error = "digest mismatch",
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.ApplyAsync("plan.json", "abc", authorizerIdentity: "Test Authorizer", rationale: null, outputFormat: "json", ct: default);

        exit.ShouldBe(1);
        stdout.ToString().ShouldContain("digest mismatch");
    }

    // ── status ──

    [Fact]
    public async Task Status_ValidDigestNoJournal_ExitsOne_AsNotFound()
    {
        // Contract: StatusAsync returns null iff the file parsed cleanly, workspace matched,
        // and a digest was produced but no journal was ever imported for that digest.
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .StatusAsync("plan.json", Arg.Any<CancellationToken>())
            .Returns((PlanStatusResult?)null);

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.StatusAsync("plan.json", outputFormat: "human", ct: default);

        exit.ShouldBe(1);
        stderr.ToString().ShouldContain("No journal");
    }

    [Fact]
    public async Task Status_InputError_ExitsTwo_AndReportsIssuesToStderr()
    {
        // Contract: a lifecycle input error (path outside workspace, unreadable file,
        // invalid JSON, workspace mismatch) arrives non-null with Issues populated and
        // Found=false. The adapter surfaces it as exit 2 so callers can distinguish
        // "this file is not a valid plan" from "you never previewed this plan".
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .StatusAsync("plan.json", Arg.Any<CancellationToken>())
            .Returns(new PlanStatusResult
            {
                Digest = null,
                State = null,
                Found = false,
                Operations = Array.Empty<PlanJournalOperation>(),
                Issues =
                [
                    new PlanValidationIssue
                    {
                        Code = PlanValidationCodes.JsonInvalid,
                        Path = "",
                        Message = "unterminated string",
                    },
                ],
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.StatusAsync("plan.json", outputFormat: "json", ct: default);

        exit.ShouldBe(2);
        // The input-error branch renders to stderr — not the not-found channel and not stdout.
        var errBody = stderr.ToString();
        errBody.ShouldContain(PlanValidationCodes.JsonInvalid);
        errBody.ShouldNotContain("No journal");
    }

    [Fact]
    public async Task Status_WithJournal_ExitsZero_AndEmitsState()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .StatusAsync("plan.json", Arg.Any<CancellationToken>())
            .Returns(new PlanStatusResult
            {
                Digest = "abc",
                State = PlanOperationState.Applying,
                Found = true,
                Operations = Array.Empty<PlanJournalOperation>(),
                Issues = Array.Empty<PlanValidationIssue>(),
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.StatusAsync("plan.json", outputFormat: "json", ct: default);

        exit.ShouldBe(0);
        stdout.ToString().ShouldContain("Applying");
        stdout.ToString().ShouldContain("abc");
    }

    [Fact]
    public async Task Status_JsonProjection_IncludesResultJsonAlongsideError()
    {
        // The machine projection MUST expose the per-operation success payload so CLI JSON
        // consumers see the published seed id / new revision the durable journal already
        // holds. Emitted as a raw JSON string named "resultJson" (RenderValue is a closed
        // union with no raw-JSON node; the explicit name signals nested JSON, no double-
        // encoding intent).
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .StatusAsync("plan.json", Arg.Any<CancellationToken>())
            .Returns(new PlanStatusResult
            {
                Digest = "abc",
                State = PlanOperationState.Verified,
                Found = true,
                Issues = Array.Empty<PlanValidationIssue>(),
                Operations =
                [
                    new PlanJournalOperation
                    {
                        Ordinal = 0,
                        OpId = "op-seed",
                        Kind = PlanOperationKind.PublishSeed,
                        State = PlanOperationState.Verified,
                        RequestJson = "{}",
                        ResultJson = "{\"newId\":4242,\"revision\":3}",
                    },
                ],
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.StatusAsync("plan.json", outputFormat: "json", ct: default);

        exit.ShouldBe(0);
        using var outerJson = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        var operation = outerJson.RootElement.GetProperty("operations")[0];
        var resultJson = operation.GetProperty("resultJson");
        resultJson.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);

        using var result = System.Text.Json.JsonDocument.Parse(resultJson.GetString()!);
        result.RootElement.GetProperty("newId").GetInt32().ShouldBe(4242);
        result.RootElement.GetProperty("revision").GetInt32().ShouldBe(3);
        operation.GetProperty("error").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Apply_JsonProjection_IncludesResultJsonForVerifiedOperation()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .ApplyAsync("plan.json", "abc", Arg.Any<ProposalAuthorization?>(), Arg.Any<CancellationToken>())
            .Returns(new PlanApplyResult
            {
                Digest = "abc",
                Failed = false,
                Operations =
                [
                    new PlanJournalOperation
                    {
                        Ordinal = 0,
                        OpId = "op-batch",
                        Kind = PlanOperationKind.Batch,
                        State = PlanOperationState.Verified,
                        RequestJson = "{}",
                        ResultJson = "{\"revision\":9}",
                    },
                ],
                Error = null,
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.ApplyAsync("plan.json", "abc", authorizerIdentity: "Test Authorizer", rationale: null, outputFormat: "json", ct: default);

        exit.ShouldBe(0);
        using var outerJson = System.Text.Json.JsonDocument.Parse(stdout.ToString());
        var operation = outerJson.RootElement.GetProperty("operations")[0];
        var resultJson = operation.GetProperty("resultJson");
        resultJson.ValueKind.ShouldBe(System.Text.Json.JsonValueKind.String);

        using var result = System.Text.Json.JsonDocument.Parse(resultJson.GetString()!);
        result.RootElement.GetProperty("revision").GetInt32().ShouldBe(9);
        // ResultJson lives beside a null error, not replacing it.
        operation.GetProperty("error").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
    }

    // ── seed descriptor ──

    [Fact]
    public async Task Seed_Unknown_ExitsOne()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        _lifecycle
            .DescribeSeedAsync(-42, Arg.Any<CancellationToken>())
            .Returns((PlanSeedDescriptor?)null);

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.DescribeSeedAsync(-42, outputFormat: "human", ct: default);

        exit.ShouldBe(1);
    }

    [Fact]
    public async Task Seed_Known_ExitsZero_AndEmitsIdentityAndFingerprint()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var identity = StagedIdentity.New();
        StagedAlias.TryFrom(-7, out var alias).ShouldBeTrue();

        _lifecycle
            .DescribeSeedAsync(-7, Arg.Any<CancellationToken>())
            .Returns(new PlanSeedDescriptor
            {
                Identity = identity,
                Alias = alias,
                Fingerprint = "fp-xyz",
                Title = "Some seed",
                Type = "Task",
            });

        var cmd = CreateCommand(stdout, stderr);
        var exit = await cmd.DescribeSeedAsync(-7, outputFormat: "json", ct: default);

        exit.ShouldBe(0);
        var body = stdout.ToString();
        body.ShouldContain("fp-xyz");
        body.ShouldContain(identity.ToString());
    }
}
