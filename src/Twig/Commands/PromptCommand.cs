using System.Text.Json;
using Microsoft.Data.Sqlite;
using Twig.Domain.Services;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Serialization;

namespace Twig.Commands;

/// <summary>
/// Data returned by <see cref="PromptCommand"/> for a single active work item.
/// </summary>
internal readonly record struct PromptData(
    int Id,
    string Type,
    string TypeBadge,
    string Title,
    string State,
    string StateCategory,
    bool IsDirty,
    string? Color,
    string? Branch);

/// <summary>
/// Implements <c>twig prompt</c>: outputs a compact work item summary optimized for shell prompts.
/// Reads directly from SQLite (read-only, 100ms busy timeout) — no DI-resolved repositories.
/// MUST NOT write to stderr (NFR-004). Returns empty string on any error.
///
/// <para><strong>Intentional IOutputFormatter bypass (RD-003):</strong> This command does NOT use
/// <see cref="Twig.Formatters.IOutputFormatter"/> or <see cref="Twig.Formatters.OutputFormatterFactory"/>.
/// It accepts a <c>format</c> parameter of <c>"plain"</c> or <c>"json"</c> (not <c>"human"/"minimal"</c>)
/// and implements its own compact formatting. This is by design:
/// (1) The prompt use case requires sub-100ms reads with no DI resolution overhead;
/// (2) It must never write to stderr — not even ANSI escape codes in "human" mode;
/// (3) The <c>plain</c> format predates the <c>--output</c> flag and serves a different audience
///     (shell integrations that embed twig output directly in PS1/RPROMPT).
/// Future maintainers: do NOT wire this command to IOutputFormatter without revisiting these constraints.</para>
/// </summary>
internal sealed class PromptCommand(TwigConfiguration config)
{
    private const int DefaultMaxWidth = 40;

    public int Execute(string format = "plain", int maxWidth = DefaultMaxWidth)
    {
        var data = ReadPromptData(maxWidth);
        if (data is null)
            return 0;

        var output = string.Equals(format, "json", StringComparison.OrdinalIgnoreCase)
            ? FormatJson(data.Value)
            : FormatPlain(data.Value);

        Console.Write(output);
        return 0;
    }

    internal PromptData? ReadPromptData(int maxWidth = DefaultMaxWidth)
    {
        var twigDir = Path.Combine(Directory.GetCurrentDirectory(), ".twig");
        if (!Directory.Exists(twigDir))
            return null;

        var dbPath = ResolveDbPath(twigDir, config);
        if (!File.Exists(dbPath))
            return null;

        try
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
            conn.Open();

            // PRAGMA for fast prompt reads
            using var pragmaCmd = conn.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA busy_timeout = 100;";
            pragmaCmd.ExecuteNonQuery();

            // Query 1: Get active work item ID
            using var ctxCmd = conn.CreateCommand();
            ctxCmd.CommandText = "SELECT value FROM context WHERE key = 'active_work_item_id'";
            var activeValue = ctxCmd.ExecuteScalar() as string;
            if (activeValue is null || !int.TryParse(activeValue, out var activeId))
                return null;

            // Query 2: Get work item details (including is_dirty)
            using var wiCmd = conn.CreateCommand();
            wiCmd.CommandText = "SELECT id, type, title, state, is_dirty FROM work_items WHERE id = @id";
            wiCmd.Parameters.AddWithValue("@id", activeId);

            using var reader = wiCmd.ExecuteReader();
            if (!reader.Read())
                return null;

            var id = reader.GetInt32(0);
            var type = reader.GetString(1);
            var title = reader.GetString(2);
            var state = reader.GetString(3);
            var isDirty = reader.GetInt32(4) != 0;

            // Query 3: Get state entries from process_types for accurate category resolution
            IReadOnlyList<StateEntry>? stateEntries = null;
            try
            {
                using var ptCmd = conn.CreateCommand();
                ptCmd.CommandText = "SELECT states_json FROM process_types WHERE type_name = @type";
                ptCmd.Parameters.AddWithValue("@type", type);
                var statesJson = ptCmd.ExecuteScalar() as string;
                if (statesJson is not null)
                    stateEntries = JsonSerializer.Deserialize(statesJson, TwigJsonContext.Default.ListStateEntry);
            }
            catch (SqliteException) { /* fall through to heuristic */ }

            var iconMode = config.Display.Icons;
            var iconId = ResolveIconId(type);
            var badge = IconSet.GetIconByIconId(iconMode, iconId)
                ?? IconSet.GetIcon(IconSet.GetIcons(iconMode), type);
            var stateCategory = StateCategoryResolver.Resolve(state, stateEntries).ToString();
            var color = ResolveColor(type);
            var branch = GitBranchReader.GetCurrentBranch(Directory.GetCurrentDirectory());

            return new PromptData(
                id,
                type,
                badge,
                TruncateTitle(title, maxWidth),
                state,
                stateCategory,
                isDirty,
                color,
                branch);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    internal static string ResolveDbPath(string twigDir, TwigConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.Organization) && !string.IsNullOrWhiteSpace(config.Project))
            return TwigPaths.GetContextDbPath(twigDir, config.Organization, config.Project);

        return Path.Combine(twigDir, "twig.db");
    }

    /// <summary>
    /// Maps an ADO state string to a human-readable category by delegating to
    /// <see cref="StateCategoryResolver.Resolve"/>. Returns <c>"Unknown"</c> for
    /// unrecognized or empty states.
    /// </summary>
    internal static string GetStateCategory(string state)
    {
        return StateCategoryResolver.Resolve(state, null).ToString();
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

    private string? ResolveIconId(string typeName)
    {
        var appearance = config.TypeAppearances?.Find(t =>
            string.Equals(t.Name, typeName, StringComparison.OrdinalIgnoreCase));
        return appearance?.IconId;
    }

    private string? ResolveColor(string type)
    {
        var appearanceColors = config.TypeAppearances?
            .Where(a => !string.IsNullOrEmpty(a.Color))
            .ToDictionary(a => a.Name, a => a.Color);
        return TypeColorResolver.ResolveHex(type, config.Display.TypeColors, appearanceColors);
    }

    internal static string FormatPlain(PromptData data)
    {
        var dirty = data.IsDirty ? " •" : "";
        return $"{data.TypeBadge} #{data.Id} {data.Title} [{data.State}]{dirty}";
    }

    internal static string FormatJson(PromptData data)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        writer.WriteStartObject();
        writer.WriteNumber("id", data.Id);
        writer.WriteString("type", data.Type);
        writer.WriteString("typeBadge", data.TypeBadge);
        writer.WriteString("title", data.Title);
        writer.WriteString("state", data.State);
        writer.WriteString("stateCategory", data.StateCategory);
        writer.WriteBoolean("isDirty", data.IsDirty);

        if (data.Color is not null)
            writer.WriteString("color", data.Color);
        else
            writer.WriteNull("color");

        if (data.Branch is not null)
            writer.WriteString("branch", data.Branch);
        else
            writer.WriteNull("branch");

        writer.WriteEndObject();
        writer.Flush();

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}

/// <summary>
/// Reads the current git branch from <c>.git/HEAD</c> via file I/O (no subprocess).
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
