using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Twig.Infrastructure.Persistence.Transport.Adapters.Herdr;

/// <summary>
/// Production <see cref="IHerdrHostSurface"/> that shells to the
/// <c>herdr</c> CLI on the workstation the twig process is running on.
/// Each method runs exactly ONE Herdr command, capturing stdout on
/// success and mapping non-zero exit / timeout to the named
/// <see cref="HerdrOperationOutcome"/> value. No pipeline is ever
/// constructed — §12.2 forbids piping a mutating Herdr verb because the
/// pipeline exit status hides its failure, and §5.1 forbids any
/// indefinite-blocking read.
/// <para>
/// Every invocation of <c>herdr agent wait</c> passes an explicit
/// <c>--timeout &lt;ms&gt;</c> so the omission-blocks-forever rule from
/// <c>local://host-surfaces.md</c> cannot bite. There is no
/// subscription, dedicated thread, reconnect loop, or broker event
/// handler — Herdr's surface has no push feed to subscribe to (§5.3,
/// §12.2).
/// </para>
/// <para>
/// This class is a thin process-launcher. It does NOT parse Herdr's
/// snapshot JSON schemas verbatim — the parse is defensive: known
/// status keys are pulled out by name and anything unrecognized maps
/// to <see cref="HerdrHostStatus.Unknown"/> (never to
/// <see cref="HerdrHostStatus.Done"/>, §4.3). Tests never spawn a real
/// <c>herdr</c> process because they inject <see cref="IHerdrHostSurface"/>
/// directly.
/// </para>
/// </summary>
internal sealed class HerdrProcessHostSurface : IHerdrHostSurface
{
    private readonly string _herdrExecutable;
    private readonly TimeProvider _clock;

    public HerdrProcessHostSurface(string herdrExecutable, TimeProvider clock)
    {
        _herdrExecutable = string.IsNullOrEmpty(herdrExecutable) ? "herdr" : herdrExecutable;
        _clock = clock;
    }

    public async Task<HerdrStatusReadout> QueryStatusAsync(HerdrTargetLocator target, int budgetMs, CancellationToken ct)
    {
        // §12.2 — prefer `herdr agent explain <target> --json` when the
        // target names an agent; otherwise fall back to
        // `herdr api snapshot`. Neither reads use `agent wait` without
        // an explicit --timeout, and neither reaches a mutating verb.
        var recordedAt = _clock.GetUtcNow();
        string[] args;
        if (!string.IsNullOrEmpty(target.AgentTarget))
        {
            args = ["agent", "explain", target.AgentTarget!, "--json"];
        }
        else
        {
            args = ["api", "snapshot"];
        }
        var invocation = await InvokeAsync(args, budgetMs, ct).ConfigureAwait(false);
        switch (invocation.Outcome)
        {
            case HerdrOperationOutcome.Ok:
                var status = ParseStatus(invocation.StdOut);
                return new HerdrStatusReadout(HerdrOperationOutcome.Ok, status, recordedAt);
            case HerdrOperationOutcome.Timeout:
                return new HerdrStatusReadout(HerdrOperationOutcome.Timeout, HerdrHostStatus.Unknown, recordedAt);
            default:
                return new HerdrStatusReadout(HerdrOperationOutcome.Failed, HerdrHostStatus.Unknown, recordedAt);
        }
    }

    public async Task<HerdrLivenessReadout> QueryLivenessAsync(HerdrTargetLocator target, int budgetMs, CancellationToken ct)
    {
        // §12.2 — `pane current` / `agent explain`; NEVER `agent wait`
        // / `pane wait-output` without a --timeout.
        var recordedAt = _clock.GetUtcNow();
        string[] args;
        if (!string.IsNullOrEmpty(target.AgentTarget))
        {
            args = ["agent", "explain", target.AgentTarget!, "--json"];
        }
        else if (!string.IsNullOrEmpty(target.Pane))
        {
            args = ["pane", "current", "--current"];
        }
        else
        {
            args = ["api", "snapshot"];
        }
        var invocation = await InvokeAsync(args, budgetMs, ct).ConfigureAwait(false);
        switch (invocation.Outcome)
        {
            case HerdrOperationOutcome.Ok:
                var presence = LooksLikePresent(invocation.StdOut, target)
                    ? TransportLivenessPresence.Present
                    : TransportLivenessPresence.Absent;
                return new HerdrLivenessReadout(HerdrOperationOutcome.Ok, presence, recordedAt);
            case HerdrOperationOutcome.Timeout:
                return new HerdrLivenessReadout(HerdrOperationOutcome.Timeout, TransportLivenessPresence.Error, recordedAt);
            default:
                return new HerdrLivenessReadout(HerdrOperationOutcome.Failed, TransportLivenessPresence.Unknown, recordedAt);
        }
    }

    public async Task<HerdrPreflightReadout> PreflightCloseAsync(HerdrTargetLocator target, CancellationToken ct)
    {
        // §12.2 preflight cross-check — read the live records and
        // confirm workspace/tab/pane ids resolve. Uses `herdr api
        // snapshot` because that's the read-only surface that carries
        // every id shape at once.
        var invocation = await InvokeAsync(new[] { "api", "snapshot" }, budgetMs: 2000, ct).ConfigureAwait(false);
        switch (invocation.Outcome)
        {
            case HerdrOperationOutcome.Ok:
                var confirmed = ConfirmsIds(invocation.StdOut, target);
                return new HerdrPreflightReadout(HerdrOperationOutcome.Ok, confirmed);
            case HerdrOperationOutcome.Timeout:
                return new HerdrPreflightReadout(HerdrOperationOutcome.Timeout, false);
            default:
                return new HerdrPreflightReadout(HerdrOperationOutcome.Failed, false);
        }
    }

    public async Task<HerdrCloseReadout> CloseAsync(HerdrTargetLocator target, CancellationToken ct)
    {
        // §12.2 — exactly one unpiped `herdr tab close <tab_id>` or
        // `herdr pane close <pane_id>`. This method NEVER wraps the
        // process in a shell; the two args go straight to Herdr.
        string[]? args = target.HostAttachmentIdKind switch
        {
            HerdrAdapterConstants.HostAttachmentIdKindTab => ["tab", "close", target.HostAttachmentId],
            HerdrAdapterConstants.HostAttachmentIdKindPane => ["pane", "close", target.HostAttachmentId],
            _ => null,
        };
        if (args is null)
            return new HerdrCloseReadout(HerdrOperationOutcome.Failed);
        // Close budget is generous: the host is expected to complete or
        // fail; the timeout here is a floor against a genuinely hung
        // Herdr process, not the §5.1 probe budget.
        var invocation = await InvokeAsync(args, budgetMs: 10_000, ct).ConfigureAwait(false);
        return invocation.Outcome == HerdrOperationOutcome.Ok
            ? new HerdrCloseReadout(HerdrOperationOutcome.Ok)
            : new HerdrCloseReadout(HerdrOperationOutcome.Failed);
    }

    public async Task<HerdrRemainingReadout> ObservePartialCloseRemainingAsync(HerdrTargetLocator parent, CancellationToken ct)
    {
        // §6.3 — Herdr's partial-close outcome is UNVERIFIED. The
        // pane-list read here is defensive: even on OK we compare the
        // recorded pane against the fresh snapshot. If we cannot read
        // or cannot classify, we return Unknown per §6.3.
        var invocation = await InvokeAsync(new[] { "api", "snapshot" }, budgetMs: 2000, ct).ConfigureAwait(false);
        if (invocation.Outcome != HerdrOperationOutcome.Ok)
            return new HerdrRemainingReadout(HerdrOperationOutcome.Ok, HerdrRemainingSummary.Unknown);
        var summary = ClassifyRemaining(invocation.StdOut, parent);
        return new HerdrRemainingReadout(HerdrOperationOutcome.Ok, summary);
    }

    private async Task<ProcessInvocation> InvokeAsync(IReadOnlyList<string> args, int budgetMs, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_herdrExecutable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi);
        if (process is null)
            return new ProcessInvocation(HerdrOperationOutcome.Failed, string.Empty);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var waitTask = process.WaitForExitAsync(ct);
        var budget = System.TimeSpan.FromMilliseconds(budgetMs);
        var completed = await Task.WhenAny(waitTask, Task.Delay(budget, ct)).ConfigureAwait(false);
        if (completed != waitTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return new ProcessInvocation(HerdrOperationOutcome.Timeout, string.Empty);
        }
        var stdout = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);
        return process.ExitCode == 0
            ? new ProcessInvocation(HerdrOperationOutcome.Ok, stdout)
            : new ProcessInvocation(HerdrOperationOutcome.Failed, stdout);
    }

    /// <summary>Defensive JSON parse for a status field. Only the six
    /// tokens §4.2 fixes map to a status; anything else is Unknown.
    /// This never maps <c>idle</c> to <c>done</c> (§4.3).</summary>
    private static HerdrHostStatus ParseStatus(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return HerdrHostStatus.Unknown;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var token = ExtractStatusToken(doc.RootElement);
            return token switch
            {
                "idle" => HerdrHostStatus.Idle,
                "working" => HerdrHostStatus.Working,
                "blocked" => HerdrHostStatus.Blocked,
                "done" => HerdrHostStatus.Done,
                "unknown" => HerdrHostStatus.Unknown,
                _ => HerdrHostStatus.Unknown,
            };
        }
        catch (JsonException)
        {
            return HerdrHostStatus.Unknown;
        }
    }

    private static string? ExtractStatusToken(JsonElement root)
    {
        // Herdr's `agent explain --json` documents an `agent_status`
        // field; `api snapshot` uses the same key when the agent status
        // is embedded. Search shallowly to avoid pulling a "status"
        // token from an unrelated node.
        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("agent_status", out var direct) && direct.ValueKind == JsonValueKind.String)
                return direct.GetString();
            if (root.TryGetProperty("status", out var statusProp) && statusProp.ValueKind == JsonValueKind.String)
                return statusProp.GetString();
        }
        return null;
    }

    private static bool LooksLikePresent(string json, HerdrTargetLocator target)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            // Any non-null id in the payload counts as "present" — the
            // adapter cannot distinguish finer without a Herdr-side
            // contract. This is a heuristic sized to §3.3 liveness:
            // present, absent, unknown, error.
            var idsToCheck = new[] { target.Workspace, target.Tab, target.Pane, target.AgentTarget };
            var raw = json.AsSpan();
            foreach (var id in idsToCheck)
            {
                if (string.IsNullOrEmpty(id)) continue;
                if (raw.IndexOf(id.AsSpan()) >= 0) return true;
            }
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ConfirmsIds(string json, HerdrTargetLocator target)
    {
        // Preflight is strict: every declared workspace/tab/pane id
        // MUST appear in the fresh snapshot. If it does not, §12.2
        // refuses the close.
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            _ = JsonDocument.Parse(json);
            var raw = json.AsSpan();
            if (raw.IndexOf(target.Workspace.AsSpan()) < 0) return false;
            if (!string.IsNullOrEmpty(target.Tab) && raw.IndexOf(target.Tab.AsSpan()) < 0) return false;
            if (!string.IsNullOrEmpty(target.Pane) && raw.IndexOf(target.Pane.AsSpan()) < 0) return false;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static HerdrRemainingSummary ClassifyRemaining(string json, HerdrTargetLocator parent)
    {
        // §6.3 UNVERIFIED-safe: only claim Subset / None when the
        // fresh snapshot lets us do so unambiguously. The safe default
        // is Unknown.
        if (string.IsNullOrWhiteSpace(json)) return HerdrRemainingSummary.Unknown;
        try
        {
            _ = JsonDocument.Parse(json);
            if (string.IsNullOrEmpty(parent.Tab)) return HerdrRemainingSummary.Unknown;
            var raw = json.AsSpan();
            return raw.IndexOf(parent.Tab!.AsSpan()) >= 0
                ? HerdrRemainingSummary.Subset
                : HerdrRemainingSummary.None;
        }
        catch (JsonException)
        {
            return HerdrRemainingSummary.Unknown;
        }
    }

    private readonly record struct ProcessInvocation(HerdrOperationOutcome Outcome, string StdOut);
}
