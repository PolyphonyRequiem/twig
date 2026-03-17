using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.Interfaces;
using Twig.Domain.Services;
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
    private const int DefaultMaxWidth = 40;

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

    public void WritePromptState()
    {
        try
        {
            WritePromptStateCore();
        }
        catch
        {
            // Intentionally swallowed — prompt state write MUST NOT fail the parent command.
        }
    }

    private void WritePromptStateCore()
    {
        var targetPath = Path.Combine(_paths.TwigDir, "prompt.json");
        var tmpPath = targetPath + ".tmp";

        var activeId = _contextStore.GetActiveWorkItemIdAsync().GetAwaiter().GetResult();
        if (activeId is null)
        {
            WriteEmptyState(targetPath, tmpPath);
            return;
        }

        var workItem = _workItemRepo.GetByIdAsync(activeId.Value).GetAwaiter().GetResult();
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
            var processType = _processTypeStore.GetByNameAsync(typeName).GetAwaiter().GetResult();
            if (processType is not null)
                stateEntries = processType.States;
        }
        catch
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

        var typeColor = ResolveColor(typeName);
        var branch = GitBranchReader.GetCurrentBranch(Path.GetDirectoryName(_paths.TwigDir)!);
        var title = TruncateTitle(workItem.Title, DefaultMaxWidth);
        var text = FormatPlain(badge, workItem.Id, title, workItem.State, workItem.IsDirty);

        WriteFullState(targetPath, tmpPath, text, workItem.Id, typeName, badge, title,
            workItem.State, stateCategory.ToString(), workItem.IsDirty, typeColor, stateColor, branch);
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
        string? typeColor, string? stateColor, string? branch)
    {
        using (var stream = File.Create(tmpPath))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false
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
