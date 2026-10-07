using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Twig.Domain.Aggregates;
using Twig.Domain.Enums;
using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>The saved automatic scope, independent of pins and protected work.</summary>
internal sealed record BenchQueryRule(string? AssignedTo, string? UniqueName,
    IReadOnlyList<AreaPathFilter> Areas, IReadOnlyList<IterationExpression> Sprints)
{
    internal const string Name = "bench-filter";
    private const char Separator = '\u001f';

    internal static BenchQueryRule Parse(BenchSelector selector)
    {
        if (selector.Kind != SelectorKind.Query)
            throw new InvalidOperationException("The selector is not an automatic query rule.");
        if (selector.QueryRule == BenchSelector.CurrentSprintRule)
        {
            var separators = selector.Payload.Count(c => c == Separator);
            if (separators > 2 || separators == 2 && string.IsNullOrWhiteSpace(selector.QueryAssignedToUniqueName))
                throw new InvalidOperationException("Malformed current-sprint rule; no scope was widened.");
            return new(selector.QueryAssignedTo, selector.QueryAssignedToUniqueName, [],
                [IterationExpression.Parse("@Current").Value]);
        }
        if (selector.QueryRule != Name)
            throw new InvalidOperationException($"Unknown Bench query rule '{selector.QueryRule}'; no scope was widened.");
        try
        {
            using var document = JsonDocument.Parse(selector.Payload[(Name.Length + 1)..]);
            var root = document.RootElement;
            RequireProperties(root, "version", "assignedTo", "uniqueName", "areas", "sprints");
            if (root.GetProperty("version").GetInt32() != 1)
                throw new InvalidOperationException("Unsupported Bench filter version.");
            var assigned = ReadIdentity(root.GetProperty("assignedTo"));
            var unique = ReadIdentity(root.GetProperty("uniqueName"));
            var areas = new List<AreaPathFilter>();
            foreach (var entry in root.GetProperty("areas").EnumerateArray())
            {
                RequireProperties(entry, "path", "includeChildren");
                var parsed = AreaPath.Parse(entry.GetProperty("path").GetString());
                if (!parsed.IsSuccess) throw new InvalidOperationException(parsed.Error);
                areas.Add(new(parsed.Value.Value, entry.GetProperty("includeChildren").GetBoolean()));
            }
            var sprints = new List<IterationExpression>();
            foreach (var entry in root.GetProperty("sprints").EnumerateArray())
                sprints.Add(ParseSprint(entry.GetString()));
            return new(assigned, unique, areas.Distinct().ToArray(), sprints.Distinct().ToArray());
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidOperationException("Malformed Bench filter settings; no scope was widened. " + ex.Message, ex);
        }
    }

    internal static IterationExpression ParseSprint(string? value)
    {
        var parsed = IterationExpression.Parse(value);
        if (!parsed.IsSuccess) throw new ArgumentException(parsed.Error);
        if (!parsed.Value.IsRelative)
        {
            var path = IterationPath.Parse(parsed.Value.Raw);
            if (!path.IsSuccess) throw new ArgumentException(path.Error);
            return IterationExpression.Parse(path.Value.Value).Value;
        }
        var offset = parsed.Value.Offset;
        return IterationExpression.Parse(offset == 0 ? "@Current"
            : "@Current" + (offset > 0 ? "+" : "") + offset.ToString(CultureInfo.InvariantCulture)).Value;
    }


    internal BenchSelector ToLegacySelector() => UniqueName is not null
        ? BenchSelector.ForCurrentSprintCanonical(UniqueName) : BenchSelector.ForCurrentSprint(AssignedTo);
    internal BenchSelector ToSelector()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("assignedTo", AssignedTo);
            writer.WriteString("uniqueName", UniqueName);
            writer.WriteStartArray("areas");
            foreach (var area in Areas)
            {
                writer.WriteStartObject();
                writer.WriteString("path", area.Path);
                writer.WriteBoolean("includeChildren", area.IncludeChildren);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("sprints");
            foreach (var sprint in Sprints) writer.WriteStringValue(sprint.Raw);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return new(SelectorKind.Query, Name + Separator + Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length));
    }

    internal async Task<ResolvedBenchQueryRule> ResolveAsync(IIterationCalendar calendar,
        IReadOnlyList<IterationPath>? legacyOverride = null, CancellationToken ct = default)
    {
        var paths = new List<IterationPath>();
        foreach (var expression in Sprints)
        {
            IReadOnlyList<IterationPath> resolved = !expression.IsRelative
                ? [IterationPath.Parse(expression.Raw).Value]
                : expression.Offset == 0 ? legacyOverride ?? await calendar.GetCurrentIterationsAsync(ct)
                : await calendar.ResolveExpressionAsync(expression, ct);
            foreach (var path in resolved)
                if (!paths.Any(p => string.Equals(p.Value, path.Value, StringComparison.OrdinalIgnoreCase))) paths.Add(path);
        }
        return new(this, paths);
    }

    internal static string SettingsDigest(IEnumerable<BenchSelector> selectors)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            foreach (var payload in selectors.Where(s => s.Kind == SelectorKind.Query)
                .Select(s => s.Payload).Order(StringComparer.Ordinal)) writer.Write(payload);
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();
    }

    /// <summary>Hashes the complete stored selector set, including unnormalized query payloads.</summary>
    internal static string ContentsDigest(IEnumerable<BenchSelector> selectors)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var selector in selectors.OrderBy(s => s.Kind).ThenBy(s => s.Payload, StringComparer.Ordinal))
            {
                writer.Write((int)selector.Kind);
                writer.Write(selector.Payload);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length))).ToLowerInvariant();
    }

    private static string? ReadIdentity(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Null) return null;
        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value) || value.Contains(Separator))
            throw new InvalidOperationException("Empty or malformed ownership identity.");
        return value;
    }

    private static void RequireProperties(JsonElement element, params string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw new InvalidOperationException("Unknown or duplicate filter setting.");
        if (seen.Count != names.Length) throw new InvalidOperationException("Missing filter setting.");
    }
}

/// <summary>One resolved scope supplies both local matching and its bounded refresh query.</summary>
internal sealed record ResolvedBenchQueryRule(BenchQueryRule Rule, IReadOnlyList<IterationPath> Iterations)
{
    internal bool Matches(WorkItem item)
        => Iterations.Any(path => string.Equals(path.Value, item.IterationPath.Value, StringComparison.OrdinalIgnoreCase))
            && (Rule.Areas.Count == 0 || !string.IsNullOrWhiteSpace(item.AreaPath.Value) && Rule.Areas.Any(area => area.Matches(item.AreaPath)))
            && (Rule.UniqueName is not null ? item.IsAssignedToIdentity(Rule.UniqueName)
                : Rule.AssignedTo is null || string.Equals(item.AssignedTo, Rule.AssignedTo, StringComparison.OrdinalIgnoreCase));

    internal string? BuildWiql(string project)
    {
        if (Iterations.Count == 0) return null;
        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = '{Escape(project)}' AND ("
            + string.Join(" OR ", Iterations.Select(path => $"[System.IterationPath] = '{Escape(path.Value)}'")) + ")";
        if (Rule.Areas.Count > 0)
            wiql += " AND (" + string.Join(" OR ", Rule.Areas.Select(area =>
                $"[System.AreaPath] {(area.IncludeChildren ? "UNDER" : "=")} '{Escape(area.Path)}'")) + ")";
        var assignee = Rule.UniqueName ?? Rule.AssignedTo;
        if (assignee is not null) wiql += $" AND [System.AssignedTo] = '{Escape(assignee)}'";
        return wiql;
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
