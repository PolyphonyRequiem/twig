using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Config;

/// <summary>
/// Writes the pre-computed prompt state file (<c>.twig/prompt.json</c>).
/// Uses <see cref="Utf8JsonWriter"/> for AOT-compatible serialization.
/// Writes atomically via tmp + <see cref="File.Move(string, string, bool)"/>.
/// All exceptions are swallowed — this service MUST NOT fail the parent command.
/// </summary>
internal sealed class PromptStateWriter : IPromptStateWriter
{
    private const int DefaultMaxWidth = 120;

    private readonly IContextStore _contextStore;
    private readonly IWorkItemRepository _workItemRepo;
    private readonly TwigConfiguration _config;
    private readonly TwigPaths _paths;
    private readonly IProcessTypeStore _processTypeStore;

    public PromptStateWriter(
        IContextStore contextStore,
        IWorkItemRepository workItemRepo,
        TwigConfiguration config,
        TwigPaths paths,
        IProcessTypeStore processTypeStore)
    {
        _contextStore = contextStore;
        _workItemRepo = workItemRepo;
        _config = config;
        _paths = paths;
        _processTypeStore = processTypeStore;
    }

    public async Task WritePromptStateAsync()
    {
        try
        {
            await WritePromptStateCoreAsync();
        }
        catch (Exception)
        {
            // Intentionally swallowed — prompt state write MUST NOT fail the parent command.
        }
    }

    private async Task WritePromptStateCoreAsync()
    {
        var targetPath = Path.Combine(_paths.TwigDir, "prompt.json");
        var tmpPath = targetPath + ".tmp";

        var activeId = await _contextStore.GetActiveWorkItemIdAsync();
        if (activeId is null)
        {
            WriteEmptyState(targetPath, tmpPath);
            return;
        }

        var workItem = await _workItemRepo.GetByIdAsync(activeId.Value);
        if (workItem is null)
        {
            WriteEmptyState(targetPath, tmpPath);
            return;
        }

        var typeName = workItem.Type.Value;

        // Resolve badge using the full resolution chain (ADO iconId → hardcoded type → first-char fallback)
        var iconMode = _config.Display.Icons;
        var typeIconIds = _config.TypeAppearances?
            .Where(a => !string.IsNullOrEmpty(a.IconId))
            .ToDictionary(a => a.Name, a => a.IconId!);
        var badge = IconSet.ResolveTypeBadge(iconMode, typeName, typeIconIds);

        // Resolve state category using process type entries when available
        IReadOnlyList<StateEntry>? stateEntries = null;
        try
        {
            var processType = await _processTypeStore.GetByNameAsync(typeName);
            if (processType is not null)
                stateEntries = processType.States;
        }
        catch (Exception)
        {
            // Fall through to heuristic
        }

        var stateCategory = StateCategoryResolver.Resolve(workItem.State, stateEntries);

        // Resolve state color from process type entries
        string? stateColor = null;
        if (stateEntries is not null)
        {
            for (var i = 0; i < stateEntries.Count; i++)
            {
                if (string.Equals(stateEntries[i].Name, workItem.State, StringComparison.OrdinalIgnoreCase)
                    && stateEntries[i].Color is not null)
                {
                    stateColor = stateEntries[i].Color;
                    break;
                }
            }
        }

        var typeColor = NormalizeHexColor(ResolveColor(typeName));
        var typeTextColor = ContrastingTextColor(typeColor);
        var stateColorNorm = NormalizeHexColor(stateColor);
        var branch = GitBranchReader.GetCurrentBranch(Path.GetDirectoryName(_paths.TwigDir)!);
        var title = TruncateTitle(workItem.Title, DefaultMaxWidth);
        var text = FormatPlain(badge, workItem.Id, title, workItem.State, workItem.IsDirty);

        WriteFullState(targetPath, tmpPath, text, workItem.Id, typeName, badge, title,
            workItem.State, stateCategory.ToString(), workItem.IsDirty, typeColor, typeTextColor, stateColorNorm, branch);
    }

    private void WriteEmptyState(string targetPath, string tmpPath)
    {
        File.WriteAllText(tmpPath, "{}");
        File.Move(tmpPath, targetPath, overwrite: true);
    }

    /// <summary>
    /// Writes the full prompt state JSON using <see cref="Utf8JsonWriter"/> (AOT-compatible, no reflection).
    /// Schema fields: text, id, type, typeBadge, title, state, stateCategory, isDirty,
    /// typeColor, stateColor, branch, generatedAt.
    /// </summary>
    private static void WriteFullState(
        string targetPath, string tmpPath,
        string text, int id, string type, string typeBadge, string title,
        string state, string stateCategory, bool isDirty,
        string? typeColor, string? typeTextColor, string? stateColor, string? branch)
    {
        using (var stream = File.Create(tmpPath))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = true
        }))
        {
            writer.WriteStartObject();
            writer.WriteString("text", text);
            writer.WriteNumber("id", id);
            writer.WriteString("type", type);
            writer.WriteString("typeBadge", typeBadge);
            writer.WriteString("title", title);
            writer.WriteString("state", state);
            writer.WriteString("stateCategory", stateCategory);
            writer.WriteBoolean("isDirty", isDirty);

            if (typeColor is not null)
                writer.WriteString("typeColor", typeColor);
            else
                writer.WriteNull("typeColor");

            if (typeTextColor is not null)
                writer.WriteString("typeTextColor", typeTextColor);
            else
                writer.WriteNull("typeTextColor");

            if (stateColor is not null)
                writer.WriteString("stateColor", stateColor);
            else
                writer.WriteNull("stateColor");

            if (branch is not null)
                writer.WriteString("branch", branch);
            else
                writer.WriteNull("branch");

            writer.WriteString("generatedAt", DateTime.UtcNow.ToString("o"));
            writer.WriteEndObject();
        }

        File.Move(tmpPath, targetPath, overwrite: true);
    }

    /// <summary>
    /// Formats the plain text prompt string: badge + id + truncated title + state + dirty indicator.
    /// </summary>
    internal static string FormatPlain(string badge, int id, string title, string state, bool isDirty)
    {
        var dirty = isDirty ? " •" : "";
        return $"{badge} #{id} {title} [{state}]{dirty}";
    }

    /// <summary>
    /// Truncates a title to <paramref name="maxWidth"/> characters, appending '…' if truncated.
    /// </summary>
    internal static string TruncateTitle(string title, int maxWidth)
    {
        if (string.IsNullOrEmpty(title) || maxWidth <= 0)
            return string.Empty;

        if (title.Length <= maxWidth)
            return title;

        return string.Concat(title.AsSpan(0, maxWidth - 1), "…");
    }

    private string? ResolveColor(string typeName)
    {
        var appearanceColors = _config.TypeAppearances?
            .Where(a => !string.IsNullOrEmpty(a.Color))
            .ToDictionary(a => a.Name, a => a.Color);
        return TypeColorResolver.ResolveHex(typeName, _config.Display.TypeColors, appearanceColors);
    }

    /// <summary>
    /// Normalizes a hex color to <c>#RRGGBB</c> format for OMP compatibility.
    /// Handles: <c>FFF2CB1D</c> (ARGB no hash), <c>#FFF2CB1D</c> (ARGB with hash),
    /// <c>F2CB1D</c> (RGB no hash), <c>#F2CB1D</c> (already correct).
    /// </summary>
    /// <summary>
    /// Returns <c>#000000</c> or <c>#ffffff</c> — whichever has better contrast against the given background.
    /// Uses ITU-R BT.601 perceived brightness.
    /// </summary>
    internal static string? ContrastingTextColor(string? hexColor)
    {
        if (string.IsNullOrEmpty(hexColor))
            return null;

        var hex = hexColor.AsSpan().TrimStart('#');
        if (hex.Length < 6)
            return "#ffffff";

        // Take last 6 chars (handles both RRGGBB and AARRGGBB)
        if (hex.Length > 6)
            hex = hex[^6..];

        var r = int.Parse(hex[..2], System.Globalization.NumberStyles.HexNumber);
        var g = int.Parse(hex[2..4], System.Globalization.NumberStyles.HexNumber);
        var b = int.Parse(hex[4..6], System.Globalization.NumberStyles.HexNumber);

        var luminance = (0.299 * r) + (0.587 * g) + (0.114 * b);
        return luminance > 128 ? "#000000" : "#ffffff";
    }

    internal static string? NormalizeHexColor(string? color)
    {
        if (string.IsNullOrEmpty(color))
            return null;

        var span = color.AsSpan();
        if (span[0] == '#')
            span = span[1..];

        // AARRGGBB (8 hex digits) → strip alpha, take last 6
        if (span.Length == 8)
            return string.Concat("#", span[2..].ToString());

        // RRGGBB (6 hex digits) → add #
        if (span.Length == 6)
            return string.Concat("#", span.ToString());

        // Already has # or unknown format — return as-is with #
        return color.StartsWith('#') ? color : "#" + color;
    }
}

/// <summary>
/// Reads the current git branch from <c>.git/HEAD</c> via file I/O (no subprocess).
/// Used by <see cref="PromptStateWriter"/> to populate the <c>branch</c> field in <c>prompt.json</c>.
/// </summary>
internal static class GitBranchReader
{
    private const string RefPrefix = "ref: refs/heads/";

    /// <summary>
    /// Returns the current branch name, or null for detached HEAD, missing <c>.git/</c>, or I/O errors.
    /// </summary>
    internal static string? GetCurrentBranch(string workingDirectory)
    {
        try
        {
            var headPath = Path.Combine(workingDirectory, ".git", "HEAD");
            if (!File.Exists(headPath))
                return null;

            var content = File.ReadAllText(headPath).Trim();
            if (content.StartsWith(RefPrefix, StringComparison.Ordinal))
                return content[RefPrefix.Length..];

            // Detached HEAD (raw SHA or other)
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
