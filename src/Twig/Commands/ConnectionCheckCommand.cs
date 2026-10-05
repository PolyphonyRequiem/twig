using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Twig.Domain.Interfaces;
using Twig.Formatters;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Implements <c>twig connection check</c> (Task #1108 / Spec #1103 §7–9):
/// a read-only report that answers "can I switch identity on this attached worktree
/// without silently discarding unfinished work, breaking an active claim, or crossing
/// an unresolved publication receipt?".
/// <para>
/// The command never force-publishes, discards, erases, releases, or writes anything.
/// It calls <see cref="IIdentityChangeEligibilityService.InspectAsync"/> exactly once
/// and projects the resulting classification into the human / minimal / JSON surfaces
/// through <see cref="IdentityChangeEligibilityProjection"/>, the one serializer the
/// MCP tool uses too.
/// </para>
/// <para>
/// Current binding selection travels with the inspector snapshot (<c>CurrentBindingId</c>,
/// <c>CurrentIdentityId</c>, <c>CurrentIdentityName</c>, <c>SelectionRevision</c>,
/// <c>SelectionSource</c>) — the command NEVER resolves <see cref="IConnectionBindingService"/>
/// in parallel. A missing selection is a valid blocked payload, not a licence to invent
/// an identity or fall back on a different binding.
/// </para>
/// <para>
/// Exit codes:
/// <list type="bullet">
///   <item><c>0</c> — the snapshot's <c>IsEligible</c> is <see langword="true"/>.</item>
///   <item><c>1</c> — one or more blockers are present, OR the inspector's attachment/registry
///         read refused inspection (runtime hard failure).</item>
///   <item><c>2</c> — reserved for CLI usage errors (never for blocked state).</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ConnectionCheckCommand
{
    private readonly IIdentityChangeEligibilityService _eligibility;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;
    private readonly OutputFormatterFactory _formatterFactory;
    private readonly RendererFactory _rendererFactory;
    private readonly ITelemetryClient? _telemetry;

    public ConnectionCheckCommand(
        IIdentityChangeEligibilityService eligibility,
        TwigConfiguration config,
        TwigPaths paths,
        OutputFormatterFactory formatterFactory,
        RendererFactory? rendererFactory = null,
        ITelemetryClient? telemetryClient = null)
    {
        _eligibility = eligibility;
        _config = config;
        _paths = paths;
        _formatterFactory = formatterFactory;
        _rendererFactory = rendererFactory ?? new RendererFactory();
        _telemetry = telemetryClient;
    }

    public async Task<int> ExecuteAsync(
        string outputFormat = OutputFormatterFactory.DefaultFormat,
        CancellationToken ct = default)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var exitCode = await ExecuteCoreAsync(outputFormat, ct).ConfigureAwait(false);
        try
        {
            TelemetryHelper.TrackCommand(_telemetry, "connection check", OutputFormats.Normalize(outputFormat) ?? OutputFormatterFactory.DefaultFormat, exitCode, startTimestamp);
        }
        catch (Exception)
        {
            Trace.TraceWarning("Optional command telemetry was unavailable.");
        }

        return exitCode;
    }

    private async Task<int> ExecuteCoreAsync(string outputFormat, CancellationToken ct)
    {
        var fmt = _formatterFactory.GetFormatter(outputFormat);

        IdentityChangeEligibility snapshot;
        try
        {
            snapshot = await _eligibility.InspectAsync(_config, _paths, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Runtime inspection hard failure (no attached worktree, attachment or registry
            // refusal). The spec is explicit that this is NOT a "clear" or a "fallback":
            // the command refuses the switch check and exits 1 — never 0 — and never
            // suppresses the error into a fabricated all-clear payload.
            Console.Error.WriteLine(fmt.FormatError($"Eligibility inspection refused: {ex.Message}"));
            return 1;
        }

        Render(snapshot, outputFormat);
        var exit = snapshot.IsEligible ? 0 : 1;
        return exit;
    }

    private void Render(IdentityChangeEligibility snapshot, string outputFormat)
    {
        var blockerCount = CountBlockers(snapshot);
        var headline = snapshot.IsEligible
            ? "Identity-change eligibility: eligible — no blockers found."
            : $"Identity-change eligibility: BLOCKED by {blockerCount} unfinished item(s).";

        if (IsJsonFormat(outputFormat))
        {
            WriteJsonToStdout(snapshot);
            return;
        }

        RenderNode node = ConnectionRenderHelpers.IsHumanFormat(outputFormat)
            ? BuildHumanNode(snapshot, headline)
            : new RenderNode.Text(headline);

        _rendererFactory.GetRenderer(outputFormat).Render(new RenderTree.RenderTree(new[] { node }));
    }

    private static bool IsJsonFormat(string outputFormat)
    {
        var normalized = OutputFormats.Normalize(outputFormat);
        return normalized is "json" or "json-full" or "json-compact";
    }

    private static void WriteJsonToStdout(IdentityChangeEligibility snapshot)
    {
        // One AOT-safe projection feeds both standalone CLI JSON and the MCP envelope.
        var options = new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            IdentityChangeEligibilityProjection.WriteDocument(writer, snapshot);
        }
        var characters = ArrayPool<char>.Shared.Rent(Encoding.UTF8.GetMaxCharCount(buffer.WrittenCount));
        try
        {
            var count = Encoding.UTF8.GetChars(buffer.WrittenSpan, characters);
            Console.Out.WriteLine(characters.AsSpan(0, count));
        }
        finally
        {
            ArrayPool<char>.Shared.Return(characters);
        }
    }

    private static int CountBlockers(IdentityChangeEligibility s) =>
        s.PendingEdits.Count + s.LocalSeeds.Count + s.OpenPublishIntents.Count
        + s.UnresolvedJournals.Count + s.ReservedClaims.Count
        + s.WorktreeGuards.Count + s.UnknownRows.Count;

    private static RenderNode BuildHumanNode(IdentityChangeEligibility s, string headline)
    {
        var lines = new List<RenderNode>
        {
            new RenderNode.Text($"  connection:         {s.Organization}/{s.Project}"),
            new RenderNode.Text($"  connectionRef:      {s.ConnectionRef}"),
            new RenderNode.Text($"  worktreeRoot:       {s.WorktreeRoot}"),
            new RenderNode.Text($"  attachmentRevision: {s.AttachmentRevision}"),
            new RenderNode.Text($"  currentIdentity:    {s.CurrentIdentityName ?? "(unbound)"}"),
            new RenderNode.Text($"  currentIdentityId:  {s.CurrentIdentityId ?? "(unbound)"}"),
            new RenderNode.Text($"  currentBindingId:   {s.CurrentBindingId ?? "(unbound)"}"),
            new RenderNode.Text($"  selectionRevision:  {FormatOptionalLong(s.SelectionRevision)}"),
            new RenderNode.Text($"  selectionSource:    {s.SelectionSource ?? "(none)"}"),
            new RenderNode.Text(""),
        };

        // Human view bounds the display of free-text TITLES only — IDs, digests, op ids,
        // scope kinds and other evidence must stay exact so an actor can act on the row.
        // Every blocker is listed; nothing is dropped.
        AppendCountedSection(lines, "pending edits", s.PendingEdits.Count,
            s.PendingEdits.Select(p =>
                $"    pending[{p.PendingChangeId}] workItem={p.WorkItemId} kind={p.Kind} seed={p.IsSeed}"));
        AppendCountedSection(lines, "local seeds", s.LocalSeeds.Count,
            s.LocalSeeds.Select(l =>
                $"    seed alias={l.SeedAlias} type={l.TypeName} title={TruncateTitle(l.Title)}"));
        AppendCountedSection(lines, "open publish intents", s.OpenPublishIntents.Count,
            s.OpenPublishIntents.Select(i =>
                $"    intent identity={i.Identity} type={i.TypeName} title={TruncateTitle(i.Title)} recordedAt={i.RecordedAt:O}"));
        AppendCountedSection(lines, "unresolved plan journals", s.UnresolvedJournals.Count,
            s.UnresolvedJournals.Select(j =>
                $"    digest={j.Digest} op={j.OpId} source={j.SourcePath} state={j.State} reason={j.Reason}"));
        AppendCountedSection(lines, "reserved claims", s.ReservedClaims.Count,
            s.ReservedClaims.Select(c =>
                $"    claim={c.ClaimId} state={c.ObservedState} workItem={c.WorkItemId} scope={c.PrimaryScopeKind} cas={c.CasToken} mintedAt={c.MintedAt:O}"));
        AppendCountedSection(lines, "worktree guards", s.WorktreeGuards.Count,
            s.WorktreeGuards.Select(g =>
                $"    guard={g.Guard} observed={g.Observed} expected={g.Expected}"));
        AppendCountedSection(lines, "unknown rows", s.UnknownRows.Count,
            s.UnknownRows.Select(u =>
                $"    source={u.Source} detail={u.Detail}"));

        lines.Add(new RenderNode.Text(""));
        lines.Add(new RenderNode.Text("  next steps:"));
        if (s.ActionableNextSteps.Count == 0)
            lines.Add(new RenderNode.Text("    (none)"));
        else
            foreach (var step in s.ActionableNextSteps)
                lines.Add(new RenderNode.Text($"    - {step}"));

        return new RenderNode.Section(headline, lines);
    }

    private static void AppendCountedSection(
        List<RenderNode> lines, string label, int count, IEnumerable<string> rows)
    {
        lines.Add(new RenderNode.Text($"  {label}: {count}"));
        if (count == 0) return;
        foreach (var r in rows)
            lines.Add(new RenderNode.Text(r));
    }

    private static string FormatOptionalLong(long? value) =>
        value is null ? "(none)" : value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string TruncateTitle(string s, int max = 72) =>
        s.Length <= max ? s : s[..max] + "…";

}
