using Twig.Domain.Interfaces;
using Twig.Domain.Services.Field;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Workspace;

/// <summary>
/// Combines field profiles (fill-rate data from cached items), field definitions
/// (display name / data type from the ADO fields API), and optional config overrides
/// to produce an ordered list of <see cref="ColumnSpec"/> for dynamic table rendering.
/// </summary>
public static class ColumnResolver
{
    /// <summary>
    /// Resolves the ordered list of extra columns to display beyond the core 4 (ID, Type, Title, State).
    /// </summary>
    /// <param name="profiles">Fill-rate data from <see cref="FieldProfileService"/>.</param>
    /// <param name="definitions">Cached field definitions (may be empty if not yet synced).</param>
    /// <param name="configuredColumns">Explicit column list from config (null = auto-discover).</param>
    /// <param name="fillRateThreshold">Minimum fill rate to auto-include (default 0.4).</param>
    /// <param name="maxExtraColumns">Maximum number of extra columns in human output (default 3).</param>
    /// <param name="isJsonOutput">When true, include all discovered fields (no cap).</param>
    public static IReadOnlyList<ColumnSpec> Resolve(
        IReadOnlyList<FieldProfile> profiles,
        IReadOnlyList<FieldDefinition> definitions,
        IReadOnlyList<string>? configuredColumns,
        double fillRateThreshold = 0.4,
        int maxExtraColumns = 3,
        bool isJsonOutput = false)
    {
        var defLookup = new Dictionary<string, FieldDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in definitions)
            defLookup[def.ReferenceName] = def;

        if (configuredColumns is { Count: > 0 })
            return ResolveConfigured(configuredColumns, defLookup);

        return ResolveAutoDiscovered(profiles, defLookup, fillRateThreshold, maxExtraColumns, isJsonOutput);
    }

    private static IReadOnlyList<ColumnSpec> ResolveConfigured(
        IReadOnlyList<string> configuredColumns,
        Dictionary<string, FieldDefinition> defLookup)
    {
        var specs = new List<ColumnSpec>(configuredColumns.Count);
        foreach (var refName in configuredColumns)
        {
            var displayName = defLookup.TryGetValue(refName, out var def)
                ? def.DisplayName
                : DeriveDisplayName(refName);
            var dataType = def?.DataType ?? "string";
            specs.Add(new ColumnSpec(refName, displayName, dataType));
        }
        return specs;
    }

    private static IReadOnlyList<ColumnSpec> ResolveAutoDiscovered(
        IReadOnlyList<FieldProfile> profiles,
        Dictionary<string, FieldDefinition> defLookup,
        double fillRateThreshold,
        int maxExtraColumns,
        bool isJsonOutput)
    {
        var specs = new List<ColumnSpec>();

        foreach (var profile in profiles)
        {
            if (profile.FillRate < fillRateThreshold)
                break; // profiles are sorted by fill rate descending

            if (!isJsonOutput && specs.Count >= maxExtraColumns)
                break;

            var displayName = defLookup.TryGetValue(profile.ReferenceName, out var def)
                ? def.DisplayName
                : DeriveDisplayName(profile.ReferenceName);
            var dataType = def?.DataType ?? "string";
            specs.Add(new ColumnSpec(profile.ReferenceName, displayName, dataType));
        }

        return specs;
    }

    /// <summary>
    /// Derives a display name from a reference name when field definitions are not yet cached.
    /// E.g., "Microsoft.VSTS.Scheduling.StoryPoints" → "Story Points".
    /// </summary>
    internal static string DeriveDisplayName(string referenceName)
    {
        // Take the last segment after the final '.'
        var lastDot = referenceName.LastIndexOf('.');
        var segment = lastDot >= 0 && lastDot < referenceName.Length - 1
            ? referenceName[(lastDot + 1)..]
            : referenceName;

        // Insert spaces before uppercase letters (PascalCase → "Pascal Case")
        var result = new System.Text.StringBuilder(segment.Length + 4);
        for (var i = 0; i < segment.Length; i++)
        {
            if (i > 0 && char.IsUpper(segment[i]) && !char.IsUpper(segment[i - 1]))
                result.Append(' ');
            result.Append(segment[i]);
        }

        return result.ToString();
    }
}
