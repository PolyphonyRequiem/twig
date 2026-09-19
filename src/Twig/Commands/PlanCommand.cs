using System.Diagnostics.CodeAnalysis;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Formatters;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// CLI adapter for <c>twig proposal validate|preview|apply|status|seed</c> (formerly
/// <c>twig plan …</c>, which remains a retained deprecated alias). Every handler
/// delegates to <see cref="IPlanLifecycleService"/> — the shared surface owns file
/// resolution, workspace enforcement, journal transitions, and ADO calls. This adapter
/// only projects the returned records into human/json/minimal output and picks the exit
/// code.
/// </summary>
/// <remarks>
/// Exit contract (per §CLI, Twig.Plan design):
/// <list type="bullet">
///   <item>0 — success (valid plan, preview succeeded, apply completed with no failed
///   operations, status/seed found).</item>
///   <item>1 — invalid plan (issues raised), apply failure, or "not found" (status of a
///   never-previewed file, seed descriptor for an unknown id).</item>
///   <item>2 — usage error (missing <c>--file</c>, missing <c>--confirm</c> on apply,
///   missing <c>--id</c> on seed).</item>
/// </list>
/// The pending-change snapshot returned by preview is passed to
/// <see cref="PendingChangeRenderer"/> so the plan and pending surfaces agree on field
/// names — no other renderer in the tree talks about pending values.
/// </remarks>
public sealed class PlanCommand(
    IPlanLifecycleService lifecycle,
    OutputFormatterFactory formatterFactory,
    ISessionSteeringModeProvider steering,
    TimeProvider clock,
    RendererFactory? rendererFactory = null,
    TextWriter? stdout = null,
    TextWriter? stderr = null)
{
    private readonly RendererFactory _rendererFactory = rendererFactory ?? new RendererFactory();
    private readonly TextWriter _stdout = stdout ?? Console.Out;
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    /// <summary>Validate a plan v1 file. No ADO mutation. Exit 0 valid, 1 invalid, 2 usage.</summary>
    public async Task<int> ValidateAsync(string? file, string outputFormat, CancellationToken ct)
    {
        if (!TryRequireFile(file, out var resolved, out var usageError))
        {
            WriteUsage(usageError, outputFormat);
            return 2;
        }

        var result = await lifecycle.ValidateAsync(resolved, ct);
        RenderValidate(result, outputFormat);
        return result.IsValid ? 0 : 1;
    }

    /// <summary>
    /// Preview a plan: parse, canonicalize, import journal, snapshot pending changes.
    /// Exit 0 on success (even when <c>CanApply=false</c> because pending rows exist),
    /// 1 when the file is invalid, 2 for usage errors.
    /// </summary>
    public Task<int> PreviewAsync(string? file, string outputFormat, CancellationToken ct)
        => PreviewAsync(file, outputFormat, false, false, ct);

    internal Func<bool> IsReviewTerminal { get; init; } = () => !Console.IsInputRedirected && !Console.IsOutputRedirected;
    internal TextReader ReviewInput { get; init; } = Console.In;

    /// <summary>Preview with explicit density and an optional review-only line loop. Never applies.</summary>
    public async Task<int> PreviewAsync(string? file, string outputFormat, bool full, bool interactive, CancellationToken ct)
    {
        if (interactive && (!string.Equals(outputFormat, "human", StringComparison.OrdinalIgnoreCase) || !IsReviewTerminal()))
        {
            WriteUsage("--interactive requires human output and terminal input/output; use --full for noninteractive details.", outputFormat);
            return 2;
        }
        if (!TryRequireFile(file, out var resolved, out var usageError))
        {
            WriteUsage(usageError, outputFormat);
            return 2;
        }

        var result = await lifecycle.PreviewAsync(resolved, ct);
        RenderPreview(result, outputFormat, full);
        if (interactive && result.ReviewModel is { } model && ChangeProposalReviewRenderer.IsSupported(model.ModelVersion))
        {
            while (true)
            {
                _stdout.Write("Review only — Details / Back / Cancel: ");
                _stdout.Flush();
                var answer = await ReviewInput.ReadLineAsync(ct);
                if (answer is null || answer.Trim().Equals("cancel", StringComparison.OrdinalIgnoreCase) || answer.Trim().Equals("c", StringComparison.OrdinalIgnoreCase))
                {
                    _stdout.WriteLine("Review closed. Not applied.");
                    break;
                }
                var option = answer.Trim().ToLowerInvariant();
                if (option is "details" or "d") RenderPreview(result, outputFormat, true);
                else if (option is "back" or "b") RenderPreview(result, outputFormat, false);
                else _stdout.WriteLine("Choose Details, Back, or Cancel. No authorization or apply occurs here.");
            }
        }
        return result.Issues.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Apply a proposal. Exit 0 when every operation reached
    /// <see cref="PlanOperationState.Verified"/>; 1 when any operation failed, the
    /// digest did not match, or the authorization gate refused; 2 when <c>--confirm</c> is
    /// missing.
    /// </summary>
    /// <remarks>
    /// A missing <c>--authorize</c> is deliberately <b>not</b> a usage error. It is an
    /// authorization refusal, reported by the gate with the reason the session actually had —
    /// which differs between a human-steered and an AFK session. Turning it into exit 2 would
    /// tell the user they mistyped a command when what really happened is that nobody signed
    /// the proposal off.
    /// </remarks>
    public async Task<int> ApplyAsync(
        string? file,
        string? confirmedDigest,
        string? authorizerIdentity,
        string? rationale,
        string outputFormat,
        CancellationToken ct)
    {
        if (!TryRequireFile(file, out var resolved, out var usageError))
        {
            WriteUsage(usageError, outputFormat);
            return 2;
        }
        if (string.IsNullOrWhiteSpace(confirmedDigest))
        {
            WriteUsage("proposal apply requires --confirm <digest>.", outputFormat);
            return 2;
        }

        // The mode is read from the session seam, never chosen by a flag: which authorization
        // path applies is a property of how the session is steered, not something the caller
        // may assert about itself. The digest the human confirmed IS the digest they signed
        // off, so the two are bound to the same value here by construction.
        //
        // 🔴 The IDENTITY, unlike the mode, IS caller-asserted, and this is the site Spec #729
        // §Authorization records as known non-compliant with its authorizer-separation
        // invariant. --authorize is a free string checked only for non-blankness, while the
        // production steering provider resolves Unresolved and therefore requires mode Human.
        // So any caller — including an agent process whose own mutation is being gated — can
        // mint a passing "human" authorization naming a human who never saw the digest, and
        // the resulting audit row is byte-identical to a genuine sign-off. Observed live on
        // 2026-08-29. Closing this needs a session/authorization contract that can demonstrate
        // separation; Spec #729 deliberately defers that mechanism, so the gap is named here
        // rather than papered over with a check this layer cannot actually perform.
        var authorization = string.IsNullOrWhiteSpace(authorizerIdentity)
            ? null
            : new ProposalAuthorization
            {
                Digest = confirmedDigest!,
                Mode = ProposalAuthorizationGate.RequiredMode(steering.Resolve()),
                AuthorizerIdentity = authorizerIdentity!,
                Rationale = string.IsNullOrWhiteSpace(rationale) ? null : rationale,
                AuthorizedAt = clock.GetUtcNow(),
            };

        var result = await lifecycle.ApplyAsync(resolved, confirmedDigest!, authorization, ct);
        RenderApply(result, outputFormat);
        return result.Failed ? 1 : 0;
    }

    /// <summary>
    /// Show journal state for a plan file. Exit 0 when a journal exists, 1 when the file
    /// parsed cleanly but no journal has ever been imported for its digest, 2 for usage
    /// errors (missing <c>--file</c>), lifecycle input errors (path outside workspace,
    /// unreadable file, invalid JSON, workspace mismatch), or a replaced source file.
    /// </summary>
    /// <remarks>
    /// The peer contract (<see cref="IPlanLifecycleService.StatusAsync"/>) reserves the
    /// <c>null</c> return for the "valid digest, no journal" case only; every input error
    /// arrives non-null with <see cref="PlanStatusResult.Issues"/> populated and
    /// <see cref="PlanStatusResult.Found"/> <c>false</c>. The adapter surfaces those
    /// distinctly so a caller can tell "you never previewed this plan" from "this file is
    /// not a valid plan" without re-running validate.
    /// <para>
    /// AB#832: <see cref="PlanStatusResult.Replacement"/> is surfaced as its own named
    /// document and its own non-zero exit, never folded into the <c>found:false</c> shape.
    /// It is reported even when a journal WAS found, because that is the dangerous case —
    /// the journal resolved from replaced bytes describes a transaction this file did not
    /// produce, and reporting it as an ordinary success is the silent corruption itself.
    /// </para>
    /// </remarks>
    public async Task<int> StatusAsync(string? file, string outputFormat, CancellationToken ct)
    {
        if (!TryRequireFile(file, out var resolved, out var usageError))
        {
            WriteUsage(usageError, outputFormat);
            return 2;
        }

        var result = await lifecycle.StatusAsync(resolved, ct);
        if (result is null)
        {
            RenderNotFound(outputFormat, "proposalStatusNotFound", $"No journal for proposal '{resolved}'.");
            return 1;
        }
        if (result.Issues.Count > 0)
        {
            RenderStatusInputErrors(result, outputFormat, resolved);
            return 2;
        }
        if (result.Replacement is { } replacement)
        {
            RenderSourceReplaced(result, replacement, outputFormat, resolved);
            return 2;
        }
        RenderStatus(result, outputFormat);
        return 0;
    }

    /// <summary>
    /// Describe a staged seed (identity + fingerprint) for plan authoring. The id must be
    /// negative; a positive id, an unknown alias, or an already-published seed returns
    /// exit 1.
    /// </summary>
    public async Task<int> DescribeSeedAsync(int? id, string outputFormat, CancellationToken ct)
    {
        if (id is null)
        {
            WriteUsage("proposal seed requires --id <negative-alias>.", outputFormat);
            return 2;
        }

        var descriptor = await lifecycle.DescribeSeedAsync(id.Value, ct);
        if (descriptor is null)
        {
            RenderNotFound(outputFormat, "proposalSeedNotFound", $"No staged seed for id #{id.Value}.");
            return 1;
        }
        RenderSeed(descriptor, outputFormat);
        return 0;
    }

    // ── input handling ────────────────────────────────────────────────

    private static bool TryRequireFile(
        string? file,
        [NotNullWhen(true)] out string? resolved,
        [NotNullWhen(false)] out string? usageError)
    {
        if (string.IsNullOrWhiteSpace(file))
        {
            resolved = null;
            usageError = "proposal requires --file <path>.";
            return false;
        }
        resolved = file!;
        usageError = null;
        return true;
    }

    private void WriteUsage(string message, string outputFormat)
    {
        var fmt = formatterFactory.GetFormatter(outputFormat);
        _stderr.WriteLine(fmt.FormatError(message));
    }

    // ── validate ──────────────────────────────────────────────────────

    private void RenderValidate(PlanValidationResult result, string outputFormat)
    {
        var fields = new List<DocumentField>
        {
            new("valid", new RenderNode.KeyValue("valid", RenderCell.Boolean(result.IsValid))),
            new("digest", new RenderNode.KeyValue("digest", DigestCell(result.Digest))),
            new("issues", new RenderNode.KeyValue("issues", IssuesCell(result.Issues))),
        };
        var human = new RenderNode.Section(null, BuildValidateHumanLines(result));
        var doc = new RenderNode.Document("proposalValidate", fields);
        var tree = new RenderTree.RenderTree([WrapHumanOverride(doc, human, outputFormat)]);
        _rendererFactory.GetRenderer(outputFormat, _stdout).Render(tree);
    }

    private static IReadOnlyList<RenderNode> BuildValidateHumanLines(PlanValidationResult result)
    {
        var lines = new List<RenderNode>();
        if (result.IsValid)
        {
            lines.Add(new RenderNode.Text($"proposal: valid  digest={result.Digest}", Severity.Success));
        }
        else
        {
            lines.Add(new RenderNode.Text($"proposal: {result.Issues.Count} issue(s)", Severity.Error));
            foreach (var issue in result.Issues)
                lines.Add(new RenderNode.Text($"  {issue.Code} at {DisplayPath(issue.Path)}: {issue.Message}"));
        }
        return lines;
    }

    // ── preview ───────────────────────────────────────────────────────

    private void RenderPreview(PlanPreviewResult result, string outputFormat, bool full)
    {
        var fields = new List<DocumentField>
        {
            new("digest", new RenderNode.KeyValue("digest", DigestCell(result.Digest))),
            new("canApply", new RenderNode.KeyValue("canApply", RenderCell.Boolean(result.CanApply))),
            new("issues", new RenderNode.KeyValue("issues", IssuesCell(result.Issues))),
            new("operations", new RenderNode.KeyValue("operations", OperationDefinitionsCell(result.Operations))),
            new("pendingChanges", new RenderNode.KeyValue(
                "pendingChanges",
                PendingChangeRenderer.PendingChangesCell(result.PendingChanges))),
            // The canonical semantic review model. Appended after the pre-existing fields so
            // every key an out-of-tree consumer already parses keeps its meaning; this is a
            // purely additive key.
            new("reviewModel", new RenderNode.KeyValue("reviewModel", ReviewModelCell(result.ReviewModel))),
        };
        var human = new RenderNode.Section(null, BuildPreviewHumanLines(result, full));
        var doc = new RenderNode.Document("proposalPreview", fields);
        var tree = new RenderTree.RenderTree([WrapHumanOverride(doc, human, outputFormat)]);
        _rendererFactory.GetRenderer(outputFormat, _stdout).Render(tree);
    }

    /// <summary>
    /// Human preview output. When a review model is present this IS the guaranteed
    /// terminal/text fallback of Spec #729: no agent-specific review adapter exists on the CLI,
    /// so the canonical semantic review model is rendered here in full rather than summarised.
    /// <para>
    /// The earlier thin summary — digest, canApply, an operation id per line — is deliberately
    /// gone rather than kept alongside. It showed the reviewer an operation's id and kind but
    /// never its preconditions or consequences, which is exactly the "authorized something they
    /// were not shown" failure the model exists to prevent. Keeping both would have left two
    /// presentations of one proposal, and the shorter one is the one a hurried reviewer reads.
    /// </para>
    /// </summary>
    private IReadOnlyList<RenderNode> BuildPreviewHumanLines(PlanPreviewResult result, bool full)
    {
        var lines = new List<RenderNode>();
        if (result.Issues.Count != 0)
        {
            lines.Add(new RenderNode.Text($"proposal: {result.Issues.Count} issue(s)", Severity.Error));
            foreach (var issue in result.Issues)
                lines.Add(new RenderNode.Text($"  {issue.Code} at {DisplayPath(issue.Path)}: {issue.Message}"));
            if (result.ReviewModel is null)
                return lines;
        }

        if (result.ReviewModel is { } model)
        {
            lines.AddRange(ChangeProposalReviewRenderer.Render(model, steering.Resolve(), full));
            lines.Add(new RenderNode.Text($"canApply: {(result.CanApply ? "yes" : "no")}"));
        }
        else
        {
            // No model means the document never parsed into a proposal at all, so there is
            // nothing semantic to render; Issues above carries the reason.
            lines.Add(new RenderNode.Text($"digest:   {result.Digest}", Severity.Warning));
            lines.Add(new RenderNode.Text($"canApply: {(result.CanApply ? "yes" : "no")}"));
        }

        lines.Add(new RenderNode.Text($"pending changes ({result.PendingChanges.Count}):"));
        foreach (var pc in result.PendingChanges)
            lines.Add(new RenderNode.Text($"  #{pc.WorkItemId} {pc.Kind} {pc.Field ?? "(no field)"}"));
        if (!result.CanApply && result.PendingChanges.Count > 0)
            lines.Add(new RenderNode.Hint("Flush pending changes with 'twig sync' before applying."));
        return lines;
    }

    // ── apply ─────────────────────────────────────────────────────────

    private void RenderApply(PlanApplyResult result, string outputFormat)
    {
        if (!IsJsonOutput(outputFormat))
        {
            _rendererFactory.GetRenderer(outputFormat, _stdout).Render(
                new RenderTree.RenderTree([new RenderNode.Section(null, BuildApplyHumanLines(result))]));
            return;
        }
        var fields = new List<DocumentField>
        {
            new("digest", new RenderNode.KeyValue("digest", RenderCell.String(result.Digest))),
            new("failed", new RenderNode.KeyValue("failed", RenderCell.Boolean(result.Failed))),
            new("operations", new RenderNode.KeyValue("operations", JournalOperationsCell(result.Operations))),
            new("error", new RenderNode.KeyValue("error", NullableStringCell(result.Error))),
        };
        var doc = new RenderNode.Document("proposalApply", fields);
        var tree = new RenderTree.RenderTree([doc]);
        _rendererFactory.GetRenderer(outputFormat, _stdout).Render(tree);
    }

    private static IReadOnlyList<RenderNode> BuildApplyHumanLines(PlanApplyResult result)
    {
        var lines = new List<RenderNode>
        {
            new RenderNode.Text(
                result.Failed ? $"proposal apply: failed  digest={result.Digest}" : $"proposal apply: ok  digest={result.Digest}",
                result.Failed ? Severity.Error : Severity.Success),
        };
        AppendOperationLines(result.Operations, lines);
        if (!string.IsNullOrEmpty(result.Error))
            lines.Add(new RenderNode.Text($"error: {result.Error}", Severity.Error));
        return lines;
    }

    /// <summary>
    /// Renders the per-operation journal rows shared by <c>plan apply</c> and
    /// <c>plan status</c> human output. Both surfaces render an operation identically by
    /// contract — a row that reads one way after apply must read the same way on a later
    /// status — so they share one writer rather than two hunks that can drift.
    /// <para>
    /// AB#754/755/881: the row line names the disposition token and, when the shared
    /// <see cref="PlanOperationDiagnostics"/> yielded a bounded per-code summary, an
    /// indented diagnostics line follows. Neither <see cref="PlanJournalOperation.Error"/>
    /// nor <see cref="PlanJournalOperation.Warning"/> is echoed inline: both may carry
    /// arbitrary ADO response fragments with user-authored values, and human/minimal is
    /// where a hurried reader looks. Callers who need the full evidence read <c>-o json</c>
    /// (raw <c>resultJson</c> / <c>warning</c> / <c>error</c> keys preserved).
    /// </para>
    /// </summary>
    private static void AppendOperationLines(
        IReadOnlyList<PlanJournalOperation> operations,
        List<RenderNode> lines)
    {
        var anyEvidence = false;
        foreach (var op in operations)
        {
            var diagnostics = op.Diagnostics;
            lines.Add(new RenderNode.Text(
                $"  [{op.Ordinal}] {op.OpId} {op.Kind} → {op.State}  ({diagnostics.Disposition})",
                SeverityFor(diagnostics.Disposition)));
            if (!string.Equals(diagnostics.Summary, diagnostics.Disposition, StringComparison.Ordinal))
                lines.Add(new RenderNode.Text(
                    $"      {(op.Warning is null ? "diagnostics" : "warning")}: {diagnostics.Summary}",
                    op.Warning is null ? SeverityFor(diagnostics.Disposition) : Severity.Warning));
            if (diagnostics.ExpectedRevision is not null || diagnostics.ObservedRevision is not null)
                lines.Add(new RenderNode.Text(
                    $"      revisions: expected={diagnostics.ExpectedRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(unknown)"}, "
                    + $"observed={diagnostics.ObservedRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "(unknown)"}"));
            if (!string.IsNullOrEmpty(op.Error) || !string.IsNullOrEmpty(op.Warning)
                || op.ResultJson is not null)
                anyEvidence = true;
        }
        if (anyEvidence)
            lines.Add(new RenderNode.Text(
                "  full evidence: rerun with '-o json' to inspect raw resultJson/warning/error."));
    }

    private static Severity SeverityFor(string disposition) => disposition switch
    {
        "verified" => Severity.Success,
        "failed" => Severity.Error,
        "outcome-unknown" => Severity.Warning,
        _ => Severity.Info,
    };

    // ── status ────────────────────────────────────────────────────────

    private void RenderStatus(PlanStatusResult result, string outputFormat)
    {
        if (!IsJsonOutput(outputFormat))
        {
            _rendererFactory.GetRenderer(outputFormat, _stdout).Render(
                new RenderTree.RenderTree([new RenderNode.Section(null, BuildStatusHumanLines(result))]));
            return;
        }
        var fields = new List<DocumentField>
        {
            new("digest", new RenderNode.KeyValue("digest", DigestCell(result.Digest))),
            new("state", new RenderNode.KeyValue("state", NullableStringCell(result.State?.ToString()))),
            new("operations", new RenderNode.KeyValue("operations", JournalOperationsCell(result.Operations))),
            new("error", new RenderNode.KeyValue("error", NullableStringCell(result.Error))),
        };
        var doc = new RenderNode.Document("proposalStatus", fields);
        var tree = new RenderTree.RenderTree([doc]);
        _rendererFactory.GetRenderer(outputFormat, _stdout).Render(tree);
    }

    private static IReadOnlyList<RenderNode> BuildStatusHumanLines(PlanStatusResult result)
    {
        var lines = new List<RenderNode>
        {
            new RenderNode.Text($"digest: {result.Digest ?? "(none)"}"),
            new RenderNode.Text($"state:  {result.State?.ToString() ?? "(none)"}"),
        };
        AppendOperationLines(result.Operations, lines);
        if (!string.IsNullOrEmpty(result.Error)
            && !result.Operations.Any(op => string.Equals(op.Error, result.Error, StringComparison.Ordinal)))
            lines.Add(new RenderNode.Text($"error: {result.Error}", Severity.Error));
        return lines;
    }

    /// <summary>
    /// Render the lifecycle's input-error branch to stderr. Distinguishes "this file is
    /// not a valid plan / not in this workspace" from the <c>null</c> "valid digest, no
    /// journal" case handled by <see cref="RenderNotFound"/>.
    /// </summary>
    private void RenderStatusInputErrors(PlanStatusResult result, string outputFormat, string resolved)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var isJsonLike = lower is "json" or "json-full" or "json-compact" or "ids";
        if (isJsonLike)
        {
            var fields = new List<DocumentField>
            {
                new("digest", new RenderNode.KeyValue("digest", DigestCell(result.Digest))),
                new("found", new RenderNode.KeyValue("found", RenderCell.Boolean(false))),
                new("issues", new RenderNode.KeyValue("issues", IssuesCell(result.Issues))),
            };
            var doc = new RenderNode.Document("proposalStatusInvalid", fields);
            _rendererFactory.GetRenderer(outputFormat, _stderr)
                .Render(new RenderTree.RenderTree(new RenderNode[] { doc }));
            return;
        }

        var lines = new List<RenderNode>
        {
            new RenderNode.Text($"proposal status: {result.Issues.Count} issue(s) in '{resolved}'", Severity.Error),
        };
        foreach (var issue in result.Issues)
            lines.Add(new RenderNode.Text($"  {issue.Code} at {DisplayPath(issue.Path)}: {issue.Message}"));
        var section = new RenderNode.Section(null, lines);
        _rendererFactory.GetRenderer(outputFormat, _stderr)
            .Render(new RenderTree.RenderTree(new RenderNode[] { section }));
    }

    /// <summary>
    /// AB#832: render the replaced-source condition. Carries the journal snapshot too when one
    /// resolved, so the caller can see exactly which transaction it would otherwise have been
    /// handed as this file's.
    /// </summary>
    private void RenderSourceReplaced(
        PlanStatusResult result,
        PlanSourceReplacement replacement,
        string outputFormat,
        string resolved)
    {
        var message = replacement.CurrentDigestJournaled
            ? $"Plan file '{resolved}' has carried more than one transaction; its journal no "
              + "longer uniquely identifies this file."
            : $"Plan file '{resolved}' was replaced after it was previewed; the transaction "
              + "journaled against this path is not the one these bytes describe.";

        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        var isJsonLike = lower is "json" or "json-full" or "json-compact" or "ids";
        if (isJsonLike)
        {
            var fields = new List<DocumentField>
            {
                new("found", new RenderNode.KeyValue("found", RenderCell.Boolean(result.Found))),
                new("digest", new RenderNode.KeyValue("digest", DigestCell(result.Digest))),
                new("sourcePath", new RenderNode.KeyValue("sourcePath", RenderCell.String(replacement.SourcePath))),
                new("currentDigestJournaled", new RenderNode.KeyValue(
                    "currentDigestJournaled",
                    RenderCell.Boolean(replacement.CurrentDigestJournaled))),
                new("supersededDigests", new RenderNode.KeyValue(
                    "supersededDigests",
                    DigestListCell(replacement.SupersededDigests))),
                new("state", new RenderNode.KeyValue("state", NullableStringCell(result.State?.ToString()))),
                new("operations", new RenderNode.KeyValue("operations", JournalOperationsCell(result.Operations))),
                new("error", new RenderNode.KeyValue("error", NullableStringCell(result.Error))),
                new("message", new RenderNode.KeyValue("message", RenderCell.String(message))),
            };
            var doc = new RenderNode.Document("proposalStatusSourceReplaced", fields);
            _rendererFactory.GetRenderer(outputFormat, _stderr)
                .Render(new RenderTree.RenderTree(new RenderNode[] { doc }));
            return;
        }

        var lines = new List<RenderNode>
        {
            new RenderNode.Text(message, Severity.Error),
            new RenderNode.Text($"  current digest: {replacement.CurrentDigest ?? "(none)"}"),
        };
        foreach (var superseded in replacement.SupersededDigests)
            lines.Add(new RenderNode.Text($"  also journaled: {superseded}"));
        lines.Add(new RenderNode.Text(
            "  plan files are single-use; author the next transaction at a fresh sequence."));

        _rendererFactory.GetRenderer(outputFormat, _stderr)
            .Render(new RenderTree.RenderTree(new RenderNode[] { new RenderNode.Section(null, lines) }));
    }

    // ── seed descriptor ───────────────────────────────────────────────

    private void RenderSeed(PlanSeedDescriptor descriptor, string outputFormat)
    {
        var fields = new List<DocumentField>
        {
            new("identity", new RenderNode.KeyValue("identity", RenderCell.String(descriptor.Identity.ToString()))),
            new("alias", new RenderNode.KeyValue("alias", RenderCell.Integer(descriptor.Alias.Value))),
            new("fingerprint", new RenderNode.KeyValue("fingerprint", RenderCell.String(descriptor.Fingerprint))),
            new("title", new RenderNode.KeyValue("title", RenderCell.String(descriptor.Title))),
            new("type", new RenderNode.KeyValue("type", RenderCell.String(descriptor.Type))),
        };
        var human = new RenderNode.Section(null, new RenderNode[]
        {
            new RenderNode.Text($"seed {descriptor.Alias.Value} ({descriptor.Type}): {descriptor.Title}"),
            new RenderNode.Text($"  identity:    {descriptor.Identity}"),
            new RenderNode.Text($"  fingerprint: {descriptor.Fingerprint}"),
        });
        var doc = new RenderNode.Document("proposalSeed", fields);
        var tree = new RenderTree.RenderTree([WrapHumanOverride(doc, human, outputFormat)]);
        _rendererFactory.GetRenderer(outputFormat, _stdout).Render(tree);
    }

    // ── shared helpers ────────────────────────────────────────────────

    private void RenderNotFound(string outputFormat, string kind, string message)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        RenderNode node = lower switch
        {
            "minimal" => new RenderNode.Text(message),
            "json" or "json-full" or "json-compact" or "ids" =>
                new RenderNode.Record(kind, new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["found"] = RenderCell.Boolean(false),
                    ["message"] = RenderCell.String(message),
                }),
            _ => new RenderNode.Text(message, Severity.Error),
        };
        _rendererFactory.GetRenderer(outputFormat, _stderr).Render(new RenderTree.RenderTree(new[] { node }));
    }

    private static bool IsJsonOutput(string outputFormat)
        => outputFormat.ToLowerInvariant() is "json" or "json-full" or "json-compact" or "ids";

    private static RenderNode WrapHumanOverride(RenderNode.Document machine, RenderNode human, string outputFormat)
    {
        var lower = (outputFormat ?? string.Empty).ToLowerInvariant();
        return lower is "json" or "json-full" or "json-compact" or "ids" or "minimal"
            ? machine
            : human;
    }

    private static RenderCell DigestCell(string? digest)
        => digest is null
            ? new RenderCell("(none)", new RenderValue.Null())
            : RenderCell.String(digest);

    private static RenderCell NullableStringCell(string? value)
        => value is null
            ? new RenderCell("(none)", new RenderValue.Null())
            : RenderCell.String(value);

    /// <summary>AB#832: the superseded digests of a replaced plan file, as a JSON string array.</summary>
    private static RenderCell DigestListCell(IReadOnlyList<string> digests)
    {
        if (digests.Count == 0)
            return new RenderCell("[]", new RenderValue.Array(Array.Empty<RenderCell>()));

        var items = new List<RenderCell>(digests.Count);
        foreach (var digest in digests)
            items.Add(RenderCell.String(digest));

        return new RenderCell($"[{digests.Count}]", new RenderValue.Array(items));
    }

    private static string DisplayPath(string path) => string.IsNullOrEmpty(path) ? "/" : path;

    private static RenderCell IssuesCell(IReadOnlyList<PlanValidationIssue> issues)
    {
        if (issues.Count == 0)
            return new RenderCell("[]", new RenderValue.Array(Array.Empty<RenderCell>()));

        var items = new List<RenderCell>(issues.Count);
        foreach (var issue in issues)
        {
            var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["code"] = RenderCell.String(issue.Code),
                ["path"] = RenderCell.String(issue.Path),
                ["message"] = RenderCell.String(issue.Message),
            };
            items.Add(new RenderCell(issue.Code, new RenderValue.Object(obj)));
        }
        return new RenderCell($"{issues.Count} issue(s)", new RenderValue.Array(items));
    }

    private static RenderCell OperationDefinitionsCell(IReadOnlyList<PlanOperationDefinition> operations)
    {
        if (operations.Count == 0)
            return new RenderCell("[]", new RenderValue.Array(Array.Empty<RenderCell>()));

        var items = new List<RenderCell>(operations.Count);
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["ordinal"] = RenderCell.Integer(i),
                ["id"] = RenderCell.String(op.Id),
                ["kind"] = RenderCell.String(op.Kind.ToString()),
            };
            items.Add(new RenderCell($"[{i}] {op.Id} {op.Kind}", new RenderValue.Object(obj)));
        }
        return new RenderCell($"{operations.Count} op(s)", new RenderValue.Array(items));
    }

    /// <summary>
    /// Projects the canonical semantic review model into render cells.
    /// <para>
    /// Every material entry is emitted — operations, preconditions, consequences,
    /// authorization choices and blockers are never summarised or truncated here, because a
    /// renderer that elides a material entry is a compliance failure rather than a display
    /// choice.
    /// </para>
    /// </summary>
    private static RenderCell ReviewModelCell(ChangeProposalReviewModel? model)
    {
        if (model is null) return new RenderCell("(none)", new RenderValue.Null());
        // Same writer as MCP and audit. This adapter only converts JSON primitives to cells.
        var serialized = Twig.Infrastructure.Plan.ChangeProposalReviewModelJson.Serialize(model);
        using var json = System.Text.Json.JsonDocument.Parse(serialized);
        // Minimal renders DisplayText; JSON keeps the same structured object value.
        return JsonCell(json.RootElement) with { DisplayText = serialized };
    }

    private static RenderCell JsonCell(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.Object => new RenderCell("", new RenderValue.Object(value.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonCell(p.Value), StringComparer.Ordinal))),
        System.Text.Json.JsonValueKind.Array => new RenderCell("", new RenderValue.Array(value.EnumerateArray().Select(JsonCell).ToArray())),
        System.Text.Json.JsonValueKind.String => RenderCell.String(value.GetString()!),
        System.Text.Json.JsonValueKind.Number => RenderCell.Integer(value.GetInt64()),
        System.Text.Json.JsonValueKind.True => RenderCell.Boolean(true),
        System.Text.Json.JsonValueKind.False => RenderCell.Boolean(false),
        _ => new RenderCell("(none)", new RenderValue.Null()),
    };

    private static RenderCell BlockersCell(IReadOnlyList<ReviewBlocker> blockers)
    {
        if (blockers.Count == 0)
            return new RenderCell("[]", new RenderValue.Array(Array.Empty<RenderCell>()));

        var cells = new List<RenderCell>(blockers.Count);
        foreach (var blocker in blockers)
        {
            var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["kind"] = RenderCell.String(blocker.Kind),
                ["workItemId"] = blocker.WorkItemId is { } id
                    ? RenderCell.Integer(id)
                    : new RenderCell("(none)", new RenderValue.Null()),
                ["detail"] = RenderCell.String(blocker.Detail),
            };
            cells.Add(new RenderCell($"{blocker.Kind}: {blocker.Detail}", new RenderValue.Object(obj)));
        }
        return new RenderCell($"{blockers.Count} blocker(s)", new RenderValue.Array(cells));
    }

    private static RenderCell StringArrayCell(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return new RenderCell("[]", new RenderValue.Array(Array.Empty<RenderCell>()));

        var cells = new List<RenderCell>(values.Count);
        foreach (var value in values)
            cells.Add(RenderCell.String(value));

        return new RenderCell(string.Join(", ", values), new RenderValue.Array(cells));
    }

    private static RenderCell JournalOperationsCell(IReadOnlyList<PlanJournalOperation> operations)
    {
        if (operations.Count == 0)
            return new RenderCell("[]", new RenderValue.Array(Array.Empty<RenderCell>()));

        var items = new List<RenderCell>(operations.Count);
        foreach (var op in operations)
        {
            var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["ordinal"] = RenderCell.Integer(op.Ordinal),
                ["opId"] = RenderCell.String(op.OpId),
                ["kind"] = RenderCell.String(op.Kind.ToString()),
                ["state"] = RenderCell.String(op.State.ToString()),
                ["startedAt"] = TimestampCell(op.StartedAt),
                ["appliedAt"] = TimestampCell(op.AppliedAt),
                ["verifiedAt"] = TimestampCell(op.VerifiedAt),
                // The success payload captured on Applied/Verified — e.g. new revision from
                // a batch, or the published id from a seed publish. RenderTree's RenderValue
                // union has no raw-JSON node, so we emit it as an opaque string named
                // "resultJson" to signal that its contents are a nested JSON document rather
                // than a display string; MCP's own writer emits it identically. Callers who
                // want a parsed value re-parse this string.
                ["resultJson"] = NullableStringCell(op.ResultJson),
                ["error"] = NullableStringCell(op.Error),
                // AB#754: non-fatal normalization detail carried alongside a Verified row.
                // Always present so a consumer can read it without probing for the key;
                // null means "no server-generated normalization was observed".
                ["warning"] = NullableStringCell(op.Warning),
                // AB#881: bounded shared projection so CLI and MCP surface the same
                // disposition/code/revisions/field-classifications on the same row. Full
                // evidence (raw resultJson/warning/error) stays present above.
                ["diagnostics"] = DiagnosticsCell(op.Diagnostics),
            };
            items.Add(new RenderCell($"[{op.Ordinal}] {op.OpId} {op.State}", new RenderValue.Object(obj)));
        }
        return new RenderCell($"{operations.Count} op(s)", new RenderValue.Array(items));
    }

    /// <summary>
    /// Projects a <see cref="PlanOperationDiagnostics"/> row into the shared render
    /// vocabulary. Every key is always present so a consumer never has to probe for
    /// existence — <c>null</c> is a first-class value here. Raw field expected/actual
    /// bodies are deliberately not emitted; the raw <c>resultJson</c> on the parent row
    /// is the full-evidence route (AB#881).
    /// </summary>
    private static RenderCell DiagnosticsCell(PlanOperationDiagnostics diagnostics)
    {
        var fieldItems = new List<RenderCell>(diagnostics.Fields.Count);
        foreach (var field in diagnostics.Fields)
        {
            var fieldObj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["field"] = RenderCell.String(field.Field),
                ["classification"] = RenderCell.String(field.Classification),
            };
            fieldItems.Add(new RenderCell($"{field.Field}={field.Classification}",
                new RenderValue.Object(fieldObj)));
        }
        var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["disposition"] = RenderCell.String(diagnostics.Disposition),
            ["code"] = NullableStringCell(diagnostics.Code),
            ["expectedRevision"] = NullableIntCell(diagnostics.ExpectedRevision),
            ["observedRevision"] = NullableIntCell(diagnostics.ObservedRevision),
            ["fields"] = new RenderCell(
                $"{diagnostics.Fields.Count} field(s)",
                new RenderValue.Array(fieldItems)),
            ["missingFields"] = StringArrayCell(diagnostics.MissingFields),
            ["summary"] = RenderCell.String(diagnostics.Summary),
        };
        return new RenderCell(diagnostics.Summary, new RenderValue.Object(obj));
    }

    private static RenderCell NullableIntCell(int? value)
        => value is null
            ? new RenderCell("(none)", new RenderValue.Null())
            : RenderCell.Integer(value.Value);

    private static RenderCell TimestampCell(DateTimeOffset? when)
        => when is null
            ? new RenderCell("(none)", new RenderValue.Null())
            : new RenderCell(when.Value.ToString("O"), new RenderValue.DateTime(when.Value));
}
