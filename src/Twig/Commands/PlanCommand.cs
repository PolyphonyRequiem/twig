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
    public async Task<int> PreviewAsync(string? file, string outputFormat, CancellationToken ct)
    {
        if (!TryRequireFile(file, out var resolved, out var usageError))
        {
            WriteUsage(usageError, outputFormat);
            return 2;
        }

        var result = await lifecycle.PreviewAsync(resolved, ct);
        RenderPreview(result, outputFormat);
        return result.Issues.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Apply a plan. Exit 0 when every operation reached
    /// <see cref="PlanOperationState.Verified"/>; 1 when any operation failed or the
    /// digest did not match; 2 when <c>--confirm</c> is missing.
    /// </summary>
    public async Task<int> ApplyAsync(string? file, string? confirmedDigest, string outputFormat, CancellationToken ct)
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

        var result = await lifecycle.ApplyAsync(resolved, confirmedDigest!, ct);
        RenderApply(result, outputFormat);
        return result.Failed ? 1 : 0;
    }

    /// <summary>
    /// Show journal state for a plan file. Exit 0 when a journal exists, 1 when the file
    /// parsed cleanly but no journal has ever been imported for its digest, 2 for usage
    /// errors (missing <c>--file</c>) or lifecycle input errors (path outside workspace,
    /// unreadable file, invalid JSON, workspace mismatch).
    /// </summary>
    /// <remarks>
    /// The peer contract (<see cref="IPlanLifecycleService.StatusAsync"/>) reserves the
    /// <c>null</c> return for the "valid digest, no journal" case only; every input error
    /// arrives non-null with <see cref="PlanStatusResult.Issues"/> populated and
    /// <see cref="PlanStatusResult.Found"/> <c>false</c>. The adapter surfaces those
    /// distinctly so a caller can tell "you never previewed this plan" from "this file is
    /// not a valid plan" without re-running validate.
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

    private void RenderPreview(PlanPreviewResult result, string outputFormat)
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
        var human = new RenderNode.Section(null, BuildPreviewHumanLines(result));
        var doc = new RenderNode.Document("proposalPreview", fields);
        var tree = new RenderTree.RenderTree([WrapHumanOverride(doc, human, outputFormat)]);
        _rendererFactory.GetRenderer(outputFormat, _stdout).Render(tree);
    }

    private static IReadOnlyList<RenderNode> BuildPreviewHumanLines(PlanPreviewResult result)
    {
        var lines = new List<RenderNode>();
        if (result.Issues.Count != 0)
        {
            lines.Add(new RenderNode.Text($"proposal: {result.Issues.Count} issue(s)", Severity.Error));
            foreach (var issue in result.Issues)
                lines.Add(new RenderNode.Text($"  {issue.Code} at {DisplayPath(issue.Path)}: {issue.Message}"));
            return lines;
        }
        lines.Add(new RenderNode.Text(
            $"digest:   {result.Digest}",
            result.CanApply ? Severity.Success : Severity.Warning));
        lines.Add(new RenderNode.Text($"canApply: {(result.CanApply ? "yes" : "no")}"));
        lines.Add(new RenderNode.Text($"operations ({result.Operations.Count}):"));
        for (var i = 0; i < result.Operations.Count; i++)
        {
            var op = result.Operations[i];
            lines.Add(new RenderNode.Text($"  [{i}] {op.Id}  {op.Kind}"));
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
        var fields = new List<DocumentField>
        {
            new("digest", new RenderNode.KeyValue("digest", RenderCell.String(result.Digest))),
            new("failed", new RenderNode.KeyValue("failed", RenderCell.Boolean(result.Failed))),
            new("operations", new RenderNode.KeyValue("operations", JournalOperationsCell(result.Operations))),
            new("error", new RenderNode.KeyValue("error", NullableStringCell(result.Error))),
        };
        var human = new RenderNode.Section(null, BuildApplyHumanLines(result));
        var doc = new RenderNode.Document("proposalApply", fields);
        var tree = new RenderTree.RenderTree([WrapHumanOverride(doc, human, outputFormat)]);
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
    /// AB#754/755: a warning is its own indented line at <see cref="Severity.Warning"/>,
    /// never appended to the state line and never rendered as an error. It is additive
    /// detail on a row that is already <c>Verified</c>; a Verified operation must not read
    /// as failed merely because ADO normalized a field it owns.
    /// </para>
    /// </summary>
    private static void AppendOperationLines(
        IReadOnlyList<PlanJournalOperation> operations,
        List<RenderNode> lines)
    {
        foreach (var op in operations)
        {
            var line = $"  [{op.Ordinal}] {op.OpId} {op.Kind} → {op.State}";
            if (!string.IsNullOrEmpty(op.Error))
                line += $"  ({op.Error})";
            lines.Add(new RenderNode.Text(line));
            if (!string.IsNullOrEmpty(op.Warning))
                lines.Add(new RenderNode.Text($"      warning: {op.Warning}", Severity.Warning));
        }
    }

    // ── status ────────────────────────────────────────────────────────

    private void RenderStatus(PlanStatusResult result, string outputFormat)
    {
        var fields = new List<DocumentField>
        {
            new("digest", new RenderNode.KeyValue("digest", DigestCell(result.Digest))),
            new("state", new RenderNode.KeyValue("state", NullableStringCell(result.State?.ToString()))),
            new("operations", new RenderNode.KeyValue("operations", JournalOperationsCell(result.Operations))),
            new("error", new RenderNode.KeyValue("error", NullableStringCell(result.Error))),
        };
        var human = new RenderNode.Section(null, BuildStatusHumanLines(result));
        var doc = new RenderNode.Document("proposalStatus", fields);
        var tree = new RenderTree.RenderTree([WrapHumanOverride(doc, human, outputFormat)]);
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
        if (!string.IsNullOrEmpty(result.Error))
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
        if (model is null)
            return new RenderCell("(none)", new RenderValue.Null());

        var workspace = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["organization"] = RenderCell.String(model.Workspace.Organization),
            ["project"] = RenderCell.String(model.Workspace.Project),
        };

        var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["model"] = RenderCell.String(model.Model),
            ["modelVersion"] = RenderCell.Integer(model.ModelVersion),
            ["digest"] = RenderCell.String(model.Digest),
            ["workspace"] = new RenderCell(
                $"{model.Workspace.Organization}/{model.Workspace.Project}",
                new RenderValue.Object(workspace)),
            ["rationale"] = NullableStringCell(model.Rationale),
            ["recipe"] = RecipeCell(model.Recipe),
            ["affectedItems"] = AffectedItemsCell(model.AffectedItems),
            ["operations"] = ReviewOperationsCell(model.Operations),
            ["authorizationChoices"] = StringArrayCell(model.AuthorizationChoices),
            ["blockers"] = BlockersCell(model.Blockers),
        };

        return new RenderCell($"{model.Model} v{model.ModelVersion}", new RenderValue.Object(obj));
    }

    private static RenderCell RecipeCell(ChangeRecipeReference? recipe)
    {
        // Null is meaningful: the proposal is ad hoc and there is no template to inspect.
        if (recipe is null)
            return new RenderCell("(ad hoc)", new RenderValue.Null());

        var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
        {
            ["recipeId"] = RenderCell.String(recipe.RecipeId),
            ["version"] = RenderCell.Integer(recipe.Version),
        };
        return new RenderCell($"{recipe.RecipeId} v{recipe.Version}", new RenderValue.Object(obj));
    }

    private static RenderCell AffectedItemsCell(IReadOnlyList<ReviewAffectedItem> items)
    {
        if (items.Count == 0)
            return new RenderCell("[]", new RenderValue.Array(Array.Empty<RenderCell>()));

        var cells = new List<RenderCell>(items.Count);
        foreach (var item in items)
        {
            var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["id"] = RenderCell.Integer(item.Id),
                ["type"] = NullableStringCell(item.Type),
                ["title"] = NullableStringCell(item.Title),
                ["state"] = NullableStringCell(item.State),
                ["role"] = RenderCell.String(item.Role),
            };
            cells.Add(new RenderCell($"#{item.Id} ({item.Role})", new RenderValue.Object(obj)));
        }
        return new RenderCell($"{items.Count} item(s)", new RenderValue.Array(cells));
    }

    private static RenderCell ReviewOperationsCell(IReadOnlyList<ReviewOperation> operations)
    {
        if (operations.Count == 0)
            return new RenderCell("[]", new RenderValue.Array(Array.Empty<RenderCell>()));

        var cells = new List<RenderCell>(operations.Count);
        foreach (var op in operations)
        {
            var target = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["workItemId"] = op.Target.WorkItemId is { } id
                    ? RenderCell.Integer(id)
                    : new RenderCell("(none)", new RenderValue.Null()),
                ["stagedIdentity"] = NullableStringCell(op.Target.StagedIdentity),
            };

            var preconditions = new List<RenderCell>(op.Preconditions.Count);
            foreach (var pre in op.Preconditions)
            {
                var preObj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["kind"] = RenderCell.String(pre.Kind),
                    ["value"] = RenderCell.String(pre.Value),
                };
                preconditions.Add(new RenderCell($"{pre.Kind}={pre.Value}", new RenderValue.Object(preObj)));
            }

            var consequences = new List<RenderCell>(op.Consequences.Count);
            foreach (var con in op.Consequences)
            {
                var conObj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                {
                    ["kind"] = RenderCell.String(con.Kind),
                    ["field"] = NullableStringCell(con.Field),
                    ["to"] = NullableStringCell(con.To),
                    ["relation"] = NullableStringCell(con.Relation),
                    ["otherId"] = con.OtherId is { } other
                        ? RenderCell.Integer(other)
                        : new RenderCell("(none)", new RenderValue.Null()),
                };
                consequences.Add(new RenderCell(con.Kind, new RenderValue.Object(conObj)));
            }

            var obj = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
            {
                ["ordinal"] = RenderCell.Integer(op.Ordinal),
                ["opId"] = RenderCell.String(op.OpId),
                ["kind"] = RenderCell.String(op.Kind),
                ["target"] = new RenderCell(
                    op.Target.WorkItemId is { } tid ? $"#{tid}" : op.Target.StagedIdentity ?? "(none)",
                    new RenderValue.Object(target)),
                ["summary"] = RenderCell.String(op.Summary),
                ["preconditions"] = new RenderCell(
                    $"{preconditions.Count} precondition(s)", new RenderValue.Array(preconditions)),
                ["consequences"] = new RenderCell(
                    $"{consequences.Count} consequence(s)", new RenderValue.Array(consequences)),
            };
            cells.Add(new RenderCell($"[{op.Ordinal}] {op.Summary}", new RenderValue.Object(obj)));
        }
        return new RenderCell($"{operations.Count} op(s)", new RenderValue.Array(cells));
    }

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
            };
            items.Add(new RenderCell($"[{op.Ordinal}] {op.OpId} {op.State}", new RenderValue.Object(obj)));
        }
        return new RenderCell($"{operations.Count} op(s)", new RenderValue.Array(items));
    }

    private static RenderCell TimestampCell(DateTimeOffset? when)
        => when is null
            ? new RenderCell("(none)", new RenderValue.Null())
            : new RenderCell(when.Value.ToString("O"), new RenderValue.DateTime(when.Value));
}
