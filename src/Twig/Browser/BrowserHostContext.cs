using System.Text.Json;
using Twig.Rendering;

namespace Twig.Browser;

/// <summary>Captured terminal-host display metadata, never an authentication or routing authority.</summary>
internal sealed record BrowserHostContext(string Label, string CompactLabel, string Details)
{
    internal static async Task<BrowserHostContext?> CaptureAsync(BrowserSource? admittedSource, CancellationToken ct)
    {
        var hint = EnvironmentHint(Environment.GetEnvironmentVariable);
        if (Environment.GetEnvironmentVariable("HERDR_ENV") != "1") return hint;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var pane = await BrowserHerdrHost.CaptureAsync(null, null, null, null, null, deadline.Token);
            if (admittedSource is not null && (pane.Socket != admittedSource.Socket || pane.Workspace != admittedSource.Workspace || pane.Tab != admittedSource.Tab))
                throw new InvalidDataException("Host pane differs from the admitted source workspace/tab.");
            using var workspace = await BrowserHerdrHost.ExecuteAsync(pane.Socket, ["workspace", "get", pane.Workspace], deadline.Token);
            using var tab = await BrowserHerdrHost.ExecuteAsync(pane.Socket, ["tab", "get", pane.Tab], deadline.Token);
            var workspaceInfo = workspace.RootElement.GetProperty("result").GetProperty("workspace");
            var tabInfo = tab.RootElement.GetProperty("result").GetProperty("tab");
            if (Text(workspaceInfo, "workspace_id") != pane.Workspace || Text(tabInfo, "workspace_id") != pane.Workspace || Text(tabInfo, "tab_id") != pane.Tab)
                throw new InvalidDataException("Herdr display metadata belongs to another workspace/tab.");
            var workspaceLabel = Text(workspaceInfo, "label");
            var tabLabel = Text(tabInfo, "label");
            var label = "Herdr · " + (workspaceLabel.Length == 0 ? pane.Workspace : workspaceLabel) + " / " +
                (tabLabel.Length == 0 ? pane.Tab : tabLabel) + " · " + pane.Pane;
            var details = label + " · captured host location: " + pane.Workspace + " / " + pane.Tab + " / " + pane.Pane;
            if (admittedSource is not null) details += " · source pane: " + admittedSource.Pane;
            return new(label, "Herdr · " + pane.Pane, details);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or ArgumentException or KeyNotFoundException or JsonException or System.ComponentModel.Win32Exception)
        {
            return new("Herdr · context unavailable", "Herdr · unavailable", "Herdr context unavailable: " + Safe(ex.Message));
        }
    }

    internal static BrowserHostContext? EnvironmentHint(Func<string, string?> environment)
    {
        if (environment("HERDR_ENV") == "1") return new("Herdr · context unavailable", "Herdr", "Herdr advertised by the environment; current pane topology has not been verified.");
        if (!string.IsNullOrWhiteSpace(environment("TMUX")))
        {
            var pane = Safe(environment("TMUX_PANE"));
            return new("tmux" + (pane.Length == 0 ? "" : " · " + pane), "tmux", "tmux environment hint" + (pane.Length == 0 ? "" : " · pane: " + pane) + "; display only, not verified topology.");
        }
        if (environment("ZELLIJ") is not null)
        {
            var session = Safe(environment("ZELLIJ_SESSION_NAME"));
            return new("Zellij" + (session.Length == 0 ? "" : " · " + session), "Zellij", "Zellij environment hint" +
                (session.Length == 0 ? "" : " · session at pane creation: " + session) + "; display only, not verified topology.");
        }
        return null;
    }

    internal string Fit(int width)
    {
        if (width <= 0) return "";
        var text = BrowserTerminalText.CellWidth(Label) <= width ? Label : CompactLabel;
        return BrowserTerminalText.CellWidth(text) <= width ? text : BrowserTerminalText.Clip(text, Math.Max(0, width - 1)) + "…";
    }

    private static string Text(JsonElement value, string name)
        => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? Safe(property.GetString()) : "";
    private static string Safe(string? value)
        => BrowserTerminalText.Clip(RichHtmlRenderer.SafeText(value ?? "").Replace('\n', ' ').Replace('\r', ' '), 512).Trim();
}
