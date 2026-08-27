using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Plan;
using Twig.Domain.ValueObjects;
using Twig.Mcp.Services;

namespace Twig.Mcp.Tools;

/// <summary>
/// MCP tools for the native plan lifecycle: twig_plan_validate, twig_plan_preview,
/// twig_plan_apply, twig_plan_status, twig_plan_seed, and twig_pending.
/// <para>
/// Every tool routes through <see cref="IPlanLifecycleService"/>, the ONE shared lifecycle
/// used by every surface. This class is a thin adapter that:
/// </para>
/// <list type="bullet">
///   <item>resolves the per-workspace <see cref="ConnectionScope"/> via
///   <see cref="ConnectionResolver"/>, exactly as the other tool classes do;</item>
///   <item>enforces the input contract the service will otherwise refuse — non-empty
///   <c>file</c>, exact digest shape, and the strict <c>confirmed:true</c> + digest pair on
///   apply — so a caller gets a fast, specific error instead of a lifecycle exception;</item>
///   <item>emits the service's result verbatim through the same
///   <see cref="EnvelopeBuilder"/> the rest of the MCP surface uses; and</item>
///   <item>for <c>twig_pending</c>, forwards the raw <see cref="PendingChangeDetail"/>
///   rows to the response with no rendering, no formatter, no logging, and no telemetry —
///   the row order and the row values are exactly what
///   <see cref="IPendingChangeReader.GetAllChangesAsync"/> returned.</item>
/// </list>
/// </summary>
[McpServerToolType]
public sealed class PlanTools(ConnectionResolver resolver)
{
    /// <summary>SHA-256 in lowercase hex — exactly 64 [0-9a-f] characters.</summary>
    internal const string DigestPattern = "^[0-9a-f]{64}$";

    private const string PlanFileDescription =
        "Path to a plan v1 JSON file. May be absolute or relative to the current working " +
        "directory; the lifecycle resolves it to an absolute path and refuses paths outside " +
        "the current workspace root.";

    // ── twig_plan_validate ──────────────────────────────────────────

    [McpServerTool(Name = "twig_plan_validate"), Description(
        "Validate a plan v1 file. No ADO mutation.")]
    public async Task<CallToolResult> PlanValidate(
        [Description(PlanFileDescription)] string file,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "The 'file' parameter is required.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var result = await InvokeLifecycleAsync(
            ctx, ct, static (svc, path, token) => svc.ValidateAsync(path, token), file);
        if (result.Error is { } error) return await error.Materialize(ctx, ct);

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            var validation = result.Value!;
            writer.WriteBoolean("isValid", validation.IsValid);
            if (validation.Digest is not null) writer.WriteString("digest", validation.Digest);
            else writer.WriteNull("digest");

            PlanJsonWriter.WriteIssues(writer, validation.Issues);
        }, verbose, ct);
    }

    // ── twig_plan_preview ───────────────────────────────────────────

    [McpServerTool(Name = "twig_plan_preview"), Description(
        "Preview a plan: import journal, snapshot pending changes, report digest & canApply. " +
        "No ADO mutation.")]
    public async Task<CallToolResult> PlanPreview(
        [Description(PlanFileDescription)] string file,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "The 'file' parameter is required.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var result = await InvokeLifecycleAsync(
            ctx, ct, static (svc, path, token) => svc.PreviewAsync(path, token), file);
        if (result.Error is { } error) return await error.Materialize(ctx, ct);

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            var preview = result.Value!;
            if (preview.Digest is not null) writer.WriteString("digest", preview.Digest);
            else writer.WriteNull("digest");

            writer.WriteBoolean("canApply", preview.CanApply);

            if (preview.Workspace is not null)
            {
                writer.WriteStartObject("workspace");
                writer.WriteString("organization", preview.Workspace.Organization);
                writer.WriteString("project", preview.Workspace.Project);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("workspace");
            }

            PlanJsonWriter.WriteIssues(writer, preview.Issues);
            PlanJsonWriter.WriteOperationSummaries(writer, preview.Operations);
            PlanJsonWriter.WritePendingChanges(writer, preview.PendingChanges);
        }, verbose, ct);
    }

    // ── twig_plan_apply ─────────────────────────────────────────────

    [McpServerTool(Name = "twig_plan_apply"), Description(
        "Apply a plan. Requires confirmed:true AND confirmedDigest matching current file digest exactly.")]
    public async Task<CallToolResult> PlanApply(
        [Description(PlanFileDescription)] string file,
        [Description(
            "Strict boolean confirmation. MUST be exactly true; false or absent refuses.")]
            bool confirmed,
        [Description(
            "Canonical plan digest the caller is committing to. MUST equal the digest of the " +
            "file at call time; a mismatch refuses without touching ADO. Lowercase 64-character " +
            "hex string.")]
            string confirmedDigest,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "The 'file' parameter is required.");

        if (!confirmed)
        {
            return EnvelopeBuilder.Error(
                McpErrorCode.ConfirmationRequired,
                "Apply refuses without 'confirmed:true'. Pass exactly confirmed:true together " +
                "with the current plan digest to confirm.");
        }

        if (string.IsNullOrWhiteSpace(confirmedDigest))
        {
            return EnvelopeBuilder.Error(
                McpErrorCode.InvalidInput,
                "The 'confirmedDigest' parameter is required and must equal the current plan " +
                "digest exactly.");
        }

        if (!IsCanonicalDigest(confirmedDigest))
        {
            return EnvelopeBuilder.Error(
                McpErrorCode.InvalidInput,
                "The 'confirmedDigest' parameter must be exactly 64 lowercase hex characters " +
                "(SHA-256 in canonical form).");
        }

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var result = await InvokeLifecycleAsync(
            ctx,
            ct,
            static (svc, args, token) => svc.ApplyAsync(args.File, args.Digest, token),
            (File: file, Digest: confirmedDigest));
        if (result.Error is { } error) return await error.Materialize(ctx, ct);

        // 🔴 Success and failure emit exactly the same JSON payload — digest, failed,
        // error, and the full per-operation journal (state, startedAt/appliedAt/
        // verifiedAt, result, error) — so the caller can drive recovery without a
        // second status round-trip. On failure the transport-level IsError flag is
        // set so the sequential BatchExecutionEngine fails/stops the enclosing batch;
        // direct clients still receive the full digest/error/operations payload.
        var apply = result.Value!;
        return await EnvelopeBuilder.PayloadAsync(ctx, writer =>
        {
            writer.WriteString("digest", apply.Digest);
            writer.WriteBoolean("failed", apply.Failed);
            if (apply.Error is not null) writer.WriteString("error", apply.Error);
            else writer.WriteNull("error");

            PlanJsonWriter.WriteJournalOperations(writer, apply.Operations);
        }, verbose, isError: apply.Failed, ct);
    }

    // ── twig_plan_status ────────────────────────────────────────────

    [McpServerTool(Name = "twig_plan_status"), Description(
        "Show journal state for a plan file. Returns null when the plan has never been previewed.")]
    public async Task<CallToolResult> PlanStatus(
        [Description(PlanFileDescription)] string file,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(file))
            return EnvelopeBuilder.Error(McpErrorCode.InvalidInput, "The 'file' parameter is required.");

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var result = await InvokeLifecycleAsync(
            ctx, ct, static (svc, path, token) => svc.StatusAsync(path, token), file);
        if (result.Error is { } error) return await error.Materialize(ctx, ct);

        var status = result.Value;
        if (status is null)
        {
            return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
            {
                writer.WriteNull("status");
            }, verbose, ct);
        }

        // 🔴 The lifecycle now returns three distinct shapes for a non-null status:
        //   (a) input-error — Found=false, Issues populated;
        //   (b) valid file, no journal — service returns null (handled above);
        //   (c) journal loaded — Found=true, Digest/State/Operations populated.
        // Digest and State are both nullable on the record; the projection surfaces
        // 'found' explicitly so callers can distinguish the shapes without inferring
        // from missing fields, and preserves every field the lifecycle sets.
        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteStartObject("status");
            writer.WriteBoolean("found", status.Found);

            if (status.Digest is not null) writer.WriteString("digest", status.Digest);
            else writer.WriteNull("digest");

            if (status.State is { } state) writer.WriteString("state", state.ToString());
            else writer.WriteNull("state");

            if (status.Error is not null) writer.WriteString("error", status.Error);
            else writer.WriteNull("error");

            PlanJsonWriter.WriteIssues(writer, status.Issues);
            PlanJsonWriter.WriteJournalOperations(writer, status.Operations);
            writer.WriteEndObject();
        }, verbose, ct);
    }

    // ── twig_plan_seed ──────────────────────────────────────────────

    [McpServerTool(Name = "twig_plan_seed"), Description(
        "Describe a staged seed (identity + fingerprint) for plan authoring. Requires a " +
        "negative alias; returns null for a positive id, an unknown alias, or an already-" +
        "published seed.")]
    public async Task<CallToolResult> PlanSeed(
        [Description(
            "Negative display alias of a currently-staged seed. Positive ids are rejected " +
            "at the schema — describe is a plan-authoring convenience for STAGED seeds only.")]
            int id,
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (id >= 0)
        {
            return EnvelopeBuilder.Error(
                McpErrorCode.InvalidInput,
                $"Seed ID must be a negative integer (got {id}). Only staged seeds have a plan " +
                "descriptor; published items are addressed by their positive ADO ID.");
        }

        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var result = await InvokeLifecycleAsync(
            ctx, ct, static (svc, alias, token) => svc.DescribeSeedAsync(alias, token), id);
        if (result.Error is { } error) return await error.Materialize(ctx, ct);

        var descriptor = result.Value;
        if (descriptor is null)
        {
            return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
            {
                writer.WriteNull("descriptor");
            }, verbose, ct);
        }

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            writer.WriteStartObject("descriptor");
            writer.WriteString("stagedIdentity", descriptor.Identity.ToString());
            writer.WriteNumber("stagedAlias", descriptor.Alias.Value);
            writer.WriteString("fingerprint", descriptor.Fingerprint);
            writer.WriteString("title", descriptor.Title);
            writer.WriteString("type", descriptor.Type);
            writer.WriteEndObject();
        }, verbose, ct);
    }

    // ── twig_pending ────────────────────────────────────────────────

    [McpServerTool(Name = "twig_pending"), Description(
        "List raw staged pending changes in exact staging order. Returns opaque per-row " +
        "values verbatim — no rendering, no coalescing, no telemetry.")]
    public async Task<CallToolResult> Pending(
        [Description(McpToolDescriptions.WorkspaceOverride)] string? workspace = null,
        [Description("When true, includes contextual hints in the response")] bool verbose = false,
        CancellationToken ct = default)
    {
        if (!resolver.TryResolve(workspace, out var ctx, out var err))
            return EnvelopeBuilder.Error(McpErrorCode.WorkspaceNotFound, err!);

        var rows = await ctx.Get<IPendingChangeReader>().GetAllChangesAsync(ct);

        return await EnvelopeBuilder.SuccessAsync(ctx, writer =>
        {
            PlanJsonWriter.WritePendingChanges(writer, rows);
        }, verbose, ct);
    }

    // ── Internal helpers ────────────────────────────────────────────

    internal static bool IsCanonicalDigest(string value)
    {
        if (value.Length != 64) return false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var isDigit = c >= '0' && c <= '9';
            var isLowerHex = c >= 'a' && c <= 'f';
            if (!isDigit && !isLowerHex) return false;
        }
        return true;
    }

    /// <summary>
    /// Executes a lifecycle call and materializes the known failure modes into an MCP error
    /// envelope. Every real work-item error the service surfaces stays a typed result — this
    /// exists only for the paths the interface documents as exceptional (file-not-found,
    /// path-outside-workspace, cancellation).
    /// </summary>
    private static async Task<LifecycleOutcome<TValue>> InvokeLifecycleAsync<TArgs, TValue>(
        ConnectionScope ctx,
        CancellationToken ct,
        Func<IPlanLifecycleService, TArgs, CancellationToken, Task<TValue>> call,
        TArgs args)
    {
        var svc = ctx.Get<IPlanLifecycleService>();
        try
        {
            var value = await call(svc, args, ct);
            return LifecycleOutcome<TValue>.Ok(value);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (FileNotFoundException ex)
        {
            return LifecycleOutcome<TValue>.Failure(McpErrorCode.InvalidInput, ex.Message);
        }
        catch (DirectoryNotFoundException ex)
        {
            return LifecycleOutcome<TValue>.Failure(McpErrorCode.InvalidInput, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            return LifecycleOutcome<TValue>.Failure(McpErrorCode.PermissionDenied, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return LifecycleOutcome<TValue>.Failure(McpErrorCode.InvalidInput, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return LifecycleOutcome<TValue>.Failure(McpErrorCode.InvalidInput, ex.Message);
        }
    }

    /// <summary>Result envelope for the lifecycle-call helper.</summary>
    private readonly record struct LifecycleOutcome<TValue>
    {
        public TValue? Value { get; init; }
        public LifecycleError? Error { get; init; }

        public static LifecycleOutcome<TValue> Ok(TValue value) =>
            new() { Value = value, Error = null };

        public static LifecycleOutcome<TValue> Failure(string code, string message) =>
            new() { Value = default, Error = new LifecycleError(code, message) };
    }

    private readonly record struct LifecycleError(string Code, string Message)
    {
        public Task<CallToolResult> Materialize(ConnectionScope ctx, CancellationToken ct) =>
            EnvelopeBuilder.ErrorAsync(Code, Message, ctx, ct);
    }
}
