namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Root POCO for the <c>tracking.json</c> file schema.
/// Serialized/deserialized via <see cref="Serialization.TwigJsonContext"/>.
/// </summary>
public sealed class TrackingFile
{
    /// <summary>Work items explicitly tracked in this workspace.</summary>
    public List<TrackingFileEntry> Tracked { get; set; } = [];

    /// <summary>Work items explicitly excluded from this workspace.</summary>
    public List<ExclusionFileEntry> Excluded { get; set; } = [];
}

/// <summary>
/// A single tracked-item entry in <c>tracking.json</c>.
/// </summary>
public sealed class TrackingFileEntry
{
    /// <summary>The ADO work item ID.</summary>
    public int Id { get; set; }

    /// <summary>
    /// Tracking mode: <c>"single"</c> or <c>"tree"</c>.
    /// Stored as a lowercase string to keep the JSON human-readable.
    /// </summary>
    public string Mode { get; set; } = "single";

    /// <summary>ISO 8601 timestamp when the item was added to tracking.</summary>
    public string AddedAt { get; set; } = string.Empty;
}

/// <summary>
/// A single exclusion entry in <c>tracking.json</c>.
/// </summary>
public sealed class ExclusionFileEntry
{
    /// <summary>The ADO work item ID.</summary>
    public int Id { get; set; }

    /// <summary>ISO 8601 timestamp when the item was excluded.</summary>
    public string AddedAt { get; set; } = string.Empty;
}
