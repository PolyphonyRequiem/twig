using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Twig.Browser;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>
/// Actual opt-in, in-process Windows detail viewport. No fixture store, command
/// subprocess, reconnect selector, authentication policy or database logic lives here.
/// This is a detail slice, not full Bench parity or a supported-platform retirement.
/// </summary>
internal sealed class BenchNativeDetailCommand(CommandContext ctx, BrowserReadModule browser, RendererFactory renderers)
{
    internal async Task<int> ExecuteAsync(int id, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        try
        {
            if (id == 0)
            {
                CommandError.Write(renderers, ctx.StderrWriter, "human", "A nonzero work-item ID or negative seed alias is required.");
                return exit = 2;
            }
            if (!OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected)
            {
                CommandError.Write(renderers, ctx.StderrWriter, "human",
                    "The opt-in native detail terminal slice requires a Windows console/ConPTY; supported Twig browsers and platform decisions are unchanged.");
                return exit = 2;
            }
            using var terminal = new BrowserWindowsTerminal();
            await RunAsync(terminal, id, ct);
            return exit = 0;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            CommandError.Write(renderers, ctx.StderrWriter, "human", ex.Message);
            return exit;
        }
        finally
        {
            ctx.TelemetryClient?.TrackEvent("CommandExecuted", new Dictionary<string, string>
            {
                ["command"] = "bench-native-detail",
                ["output_format"] = "human",
                ["exit_code"] = exit.ToString(CultureInfo.InvariantCulture),
                ["twig_version"] = VersionHelper.GetVersion().Split('+')[0],
                ["os_platform"] = RuntimeInformation.OSDescription,
            }, new Dictionary<string, double> { ["duration_ms"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds });
        }
    }

    private async Task RunAsync(BrowserWindowsTerminal terminal, int id, CancellationToken ct)
    {
        BrowserDetailObservation? observation = null;
        BrowserCapture? capture = null;
        IReadOnlyList<string> rows = [];
        Task<BrowserDetailResult>? operation = null;
        CancellationTokenSource? cancellation = null;
        bool? nextSync = false;
        var serial = 0L;
        var operationSerial = 0L;
        var width = terminal.Geometry().Width;
        var height = terminal.Geometry().Height;
        var operationWidth = width;
        var top = 0;
        var dirty = true;
        var running = true;
        var invalidated = false;
        var message = "Loading cached detail; no automatic refresh. R cache | S selected root/links | Esc cancel | Q quit";
        int ContentHeight() => Math.Max(1, height - 2);
        void Clamp() => top = Math.Clamp(top, 0, Math.Max(0, rows.Count - ContentHeight()));
        void Cancel()
        {
            serial++;
            cancellation?.Cancel();
            nextSync = null;
        }
        void Request(bool sync)
        {
            if (invalidated) return;
            Cancel();
            nextSync = sync;
            message = sync ? "Pulling only selected root/links; local changes remain protected, never published."
                : "Reading cached detail only; no refresh.";
            dirty = true;
        }
        try
        {
            while (running && !ct.IsCancellationRequested)
            {
                // One repository operation at a time: cancellation never starts another
                // read until the preceding task has completed and disposed its leases.
                if (operation is { IsCompleted: true })
                {
                    var result = await operation;
                    operation = null;
                    cancellation!.Dispose();
                    cancellation = null;
                    // Even a superseded first cache miss/cancellation establishes refusal
                    // context. Never let a later R/S implicitly retarget another origin.
                    if (!invalidated) capture ??= result.Capture;
                    // This host never rebinds its origin. A known invalidation therefore
                    // remains authoritative even when input superseded/canceled the read
                    // after it completed but before this dispatch consumed its result.
                    if (result.Failure?.Kind == BrowserReadFailureKind.AuthorityInvalidated)
                    {
                        Cancel();
                        invalidated = true;
                        observation = null;
                        capture = null;
                        rows = [];
                        top = 0;
                        message = "Authority invalidated: detail cleared. Q quit, then explicitly reconnect/restart; R/S disabled.";
                        dirty = true;
                    }
                    if (operationSerial == serial && !invalidated)
                    {
                        if (result.Failure is { } failure)
                        {
                            message = RichHtmlRenderer.SafeText(failure.Message);
                        }
                        else
                        {
                            observation = result.Observation!;
                            capture ??= observation.Capture;
                            var detail = operationWidth == width ? observation.InitialPresentation : await observation.RenderAsync(width);
                            rows = BrowserTerminalText.Rows(detail.Ansi);
                            Clamp();
                            message = $"Cached rev {observation.Revision}; synced {Stamp(observation.LastSyncedAt)}; links {Stamp(observation.LinksVerifiedAt)}; local {observation.HasLocalChanges}. R cache | S root/links | Q quit";
                        }
                        dirty = true;
                    }
                    // Only ordinary superseded/canceled completions are discarded.
                    // Known invalidation above cannot be dismissed by a newer input.
                }
                var geometry = terminal.Geometry();
                if (geometry.Width != width || geometry.Height != height)
                {
                    var oldWidth = width;
                    width = geometry.Width;
                    height = geometry.Height;
                    if (observation is not null && oldWidth != width)
                    {
                        // Preserve proportional reading position across native full-Show
                        // reflow; no cache read, provider admission, shell or network.
                        var oldCount = rows.Count;
                        var detail = await observation.RenderAsync(width);
                        rows = BrowserTerminalText.Rows(detail.Ansi);
                        if (oldCount > 0) top = (int)((long)top * rows.Count / oldCount);
                    }
                    Clamp();
                    dirty = true;
                }
                if (operation is null && nextSync is { } sync && !invalidated)
                {
                    nextSync = null;
                    cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    operationSerial = serial;
                    operationWidth = width;
                    var request = new BrowserDetailRequest(id, width, Captured: capture, Sync: sync);
                    var token = cancellation.Token;
                    operation = Task.Run(() => browser.ReadDetailAsync(request, token));
                }
                if (dirty)
                {
                    BrowserWindowsTerminal.Paint(rows, top, width, height,
                        $"Native Windows detail slice | #{id} | {observation?.Title ?? (invalidated ? "cleared" : "cached read")} | {top + 1}/{Math.Max(1, rows.Count)}", message);
                    dirty = false;
                }
                if (!terminal.Read(out var input)) continue;
                if (input.Type == 1 && input.KeyDown != 0)
                {
                    for (var repeat = 0; repeat < Math.Max(1, (int)input.Repeat) && running; repeat++)
                    {
                        var key = input.VirtualKey;
                        var character = (char)input.Character;
                        if (character is 'q' or 'Q' or '\x03' || (input.KeyModifiers & 12) != 0 && key == 67)
                        {
                            running = false;
                            Cancel();
                        }
                        else if (key == 27)
                        {
                            if (operation is not null || nextSync is not null)
                            {
                                Cancel();
                                message = "Read canceled; late results discarded. R cache | S root/links | Q quit";
                            }
                            else running = false;
                        }
                        else if (character is 'r' or 'R') Request(false);
                        else if (character is 's' or 'S') Request(true);
                        else if (key == 36) top = 0;
                        else if (key == 35) top = Math.Max(0, rows.Count - ContentHeight());
                        else if (key == 33) top -= ContentHeight();
                        else if (key == 34) top += ContentHeight();
                        else if (key == 38 || character == 'k') top--;
                        else if (key == 40 || character == 'j') top++;
                        else if (character == '?') message = "Arrows/j/k, PgUp/PgDn, Home/End, wheel scroll | R cache | S selected root/links only | Esc cancel | Q quit. No full Bench/platform cutover.";
                    }
                    Clamp();
                    dirty = true;
                }
                else if (input.Type == 2 && input.MouseFlags == 4)
                {
                    top += (short)(input.Buttons >> 16) > 0 ? -3 : 3;
                    Clamp();
                    dirty = true;
                }
            }
        }
        finally
        {
            Cancel();
            // Do not dispose a provider/store while a noncooperative service completion
            // still owns its admitted operation. Nothing may paint during this drain.
            if (operation is not null) await operation;
            cancellation?.Dispose();
        }
        ct.ThrowIfCancellationRequested();
    }

    private static string Stamp(DateTimeOffset? value)
        => value?.ToString("u", CultureInfo.InvariantCulture) ?? "unverified";
}
