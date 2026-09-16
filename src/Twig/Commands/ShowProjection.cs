using Twig.Domain.Aggregates;
using Twig.Domain.Projections;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Config;
using Twig.RenderTree;

namespace Twig.Commands;

/// <summary>
/// Opt-in field/section projection for <c>twig show</c> and <c>twig show-batch</c> (AB#880).
/// </summary>
/// <remarks>
/// When the caller passes <c>--fields</c> or <c>--sections</c>, the read answers with a
/// compact envelope carrying only the requested facts plus provenance the caller needs:
/// <c>contractVersion</c>, <c>connection</c>, <c>revision</c> when known,
/// <c>freshness</c> (<c>linksVerifiedAt</c>, <c>lastSyncedAt</c>), and
/// <c>completeness</c> naming the read route (cache vs refresh).
/// Field absence follows the shared detail projector; uncaptured fields remain unknown.
/// Section names accepted: <c>links</c>, <c>children</c>, <c>parent</c>.
/// </remarks>
internal static class ShowProjection
{
    /// <summary>Wire version. Bumped on any breaking shape change.</summary>
    public const string ContractVersion = "1";

    /// <summary>The section names the projection recognises.</summary>
    private static readonly HashSet<string> KnownSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "links",
        "children",
        "parent",
    };

    /// <summary>
    /// Parsed opt-in projection request. <see cref="IsActive"/> is true when the
    /// caller passed either <c>--fields</c> or <c>--sections</c>.
    /// </summary>
    public sealed record Request(IReadOnlyList<string> Fields, IReadOnlyList<string> Sections)
    {
        public bool IsActive => Fields.Count > 0 || Sections.Count > 0;

        /// <summary>Parses the two CSV strings the CLI receives.</summary>
        public static Request Parse(string? fields, string? sections)
            => new(ParseCsv(fields), ParseCsv(sections));

        private static IReadOnlyList<string> ParseCsv(string? csv)
        {
            if (string.IsNullOrWhiteSpace(csv))
                return Array.Empty<string>();
            var parts = new List<string>();
            foreach (var segment in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                parts.Add(segment);
            return parts;
        }
    }

    /// <summary>Which read route produced this projection.</summary>
    public enum ReadRoute
    {
        Cache,
        Refresh,
    }

    /// <summary>
    /// Formats the connection identifier as <c>org/project</c>, or <c>null</c>
    /// when neither is configured.
    /// </summary>
    public static string? FormatConnection(TwigConfiguration config)
    {
        var org = config.Organization ?? string.Empty;
        var proj = config.Project ?? string.Empty;
        if (org.Length == 0 && proj.Length == 0)
            return null;
        return $"{org}/{proj}";
    }

    /// <summary>
    /// Builds the single-item projection document. The result is a
    /// <see cref="RenderNode.Document"/> the JSON renderer projects as a top-level
    /// object — same seam every other machine emission uses.
    /// </summary>
    public static RenderNode.Document BuildItem(
        WorkItem item,
        IReadOnlyList<WorkItemLink> links,
        DateTimeOffset? linksVerifiedAt,
        WorkItem? parent,
        IReadOnlyList<WorkItem> children,
        Request request,
        string? connection,
        ReadRoute route)
    {
        var fields = new List<DocumentField>
        {
            new("contractVersion", KeyValue("contractVersion", RenderCell.String(ContractVersion))),
            new("id", KeyValue("id", RenderCell.Integer(item.Id))),
            new("connection", KeyValue("connection", ConnectionCell(connection))),
            new("revision", KeyValue("revision", RevisionCell(item.Revision))),
            new("fullRead", KeyValue("fullRead", RenderCell.String($"twig show {item.Id} --refresh -o json"))),
        };

        if (request.Fields.Count > 0)
            fields.Add(new DocumentField("requestedFields", BuildRequestedFields(item, request.Fields)));

        if (request.Sections.Count > 0)
            fields.Add(new DocumentField("requestedSections", BuildRequestedSections(item, request.Sections, links, parent, children, linksVerifiedAt)));

        fields.Add(new DocumentField("freshness", BuildFreshness(item, linksVerifiedAt)));
        fields.Add(new DocumentField("completeness", BuildCompleteness(route)));

        return new RenderNode.Document(null, fields);
    }

    /// <summary>
    /// Builds the batch projection wrapper: found items, requested-but-missing
    /// ids, and provenance. Batch uses a wrapper rather than a top-level array
    /// so misses can be disclosed without polluting each row.
    /// </summary>
    public static RenderNode.Document BuildBatch(
        IReadOnlyList<int> requestedIds,
        IReadOnlyList<RenderNode.Document> foundItems,
        IReadOnlyList<int> missingIds,
        string? connection,
        ReadRoute route)
    {
        var itemNodes = new List<RenderNode>(foundItems.Count);
        foreach (var doc in foundItems)
            itemNodes.Add(doc);

        var missingCells = new List<RenderCell>(missingIds.Count);
        foreach (var id in missingIds)
            missingCells.Add(RenderCell.Integer(id));

        var requestedCells = new List<RenderCell>(requestedIds.Count);
        foreach (var id in requestedIds)
            requestedCells.Add(RenderCell.Integer(id));

        var fields = new List<DocumentField>
        {
            new("contractVersion", KeyValue("contractVersion", RenderCell.String(ContractVersion))),
            new("connection", KeyValue("connection", ConnectionCell(connection))),
            new("requestedIds", KeyValue("requestedIds", ArrayCell(requestedCells))),
            new("items", new RenderNode.Section(null, itemNodes)),
            new("missing", KeyValue("missing", ArrayCell(missingCells))),
            new("completeness", BuildCompleteness(route)),
        };

        return new RenderNode.Document(null, fields);
    }

    // ── Fields ─────────────────────────────────────────────────────────

    private static RenderNode BuildRequestedFields(
        WorkItem item,
        IReadOnlyList<string> fieldRefs)
    {
        var docFields = new List<DocumentField>(fieldRefs.Count);
        var snapshot = new WorkItemSnapshot
        {
            Id = item.Id, Revision = item.Revision, TypeName = item.Type.ToString(),
            Title = item.Title, State = item.State, AssignedTo = item.AssignedTo,
            AreaPath = item.AreaPath.ToString(), IterationPath = item.IterationPath.ToString(),
            Fields = item.Fields,
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var refName in fieldRefs)
        {
            if (!seen.Add(refName))
                continue;

            docFields.Add(new DocumentField(refName, BuildFieldStatus(refName, snapshot)));
        }
        return new RenderNode.Document(null, docFields);
    }

    private static RenderNode.Document BuildFieldStatus(string refName, WorkItemSnapshot snapshot)
    {
        var resolved = WorkItemDetailProjector.ResolveValue(refName, snapshot);
        var status = resolved.State switch
        {
            DetailFieldState.HasValue => "present",
            DetailFieldState.EmptyOnServer => "absent",
            _ => "unknown",
        };
        var fields = new List<DocumentField>
        {
            new("status", KeyValue("status", RenderCell.String(status))),
        };
        if (resolved.State == DetailFieldState.HasValue)
            fields.Add(new("value", KeyValue("value", RenderCell.String(resolved.Full!))));
        return new RenderNode.Document(null, fields);
    }

    // ── Sections ───────────────────────────────────────────────────────

    private static RenderNode BuildRequestedSections(
        WorkItem item,
        IReadOnlyList<string> sectionNames,
        IReadOnlyList<WorkItemLink> links,
        WorkItem? parent,
        IReadOnlyList<WorkItem> children, DateTimeOffset? linksVerifiedAt)
    {
        var docFields = new List<DocumentField>(sectionNames.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in sectionNames)
        {
            if (!seen.Add(raw))
                continue;

            if (!KnownSections.Contains(raw))
            {
                docFields.Add(new DocumentField(raw, UnknownSection()));
                continue;
            }

            var normalized = raw.ToLowerInvariant();
            docFields.Add(new DocumentField(normalized, BuildSectionBody(normalized, item, links, parent, children, linksVerifiedAt)));
        }
        return new RenderNode.Document(null, docFields);
    }

    private static RenderNode.Document UnknownSection()
        => new(null, new List<DocumentField>
        {
            new("status", KeyValue("status", RenderCell.String("unknown"))),
        });

    private static RenderNode.Document BuildSectionBody(
        string name,
        WorkItem item,
        IReadOnlyList<WorkItemLink> links,
        WorkItem? parent,
        IReadOnlyList<WorkItem> children, DateTimeOffset? linksVerifiedAt)
    {
        switch (name)
        {
            case "links":
            {
                var cells = new List<RenderCell>(links.Count);
                foreach (var link in links)
                    cells.Add(new RenderCell(string.Empty, new RenderValue.Object(BuildLinkCells(link))));
                return new RenderNode.Document(null, new List<DocumentField>
                {
                    new("status", KeyValue("status", RenderCell.String(linksVerifiedAt is null ? "unknown" : "present"))),
                    new("items", KeyValue("items", ArrayCell(cells))),
                });
            }
            case "children":
            {
                var cells = new List<RenderCell>(children.Count);
                foreach (var child in children)
                    cells.Add(new RenderCell(string.Empty, new RenderValue.Object(BuildChildCells(child))));
                return new RenderNode.Document(null, new List<DocumentField>
                {
                    new("status", KeyValue("status", RenderCell.String("partial"))),
                    new("items", KeyValue("items", ArrayCell(cells))),
                });
            }
            case "parent":
            {
                if (parent is not null)
                {
                    return new RenderNode.Document(null, new List<DocumentField>
                    {
                        new("status", KeyValue("status", RenderCell.String("present"))),
                        new("value", KeyValue("value", new RenderCell(string.Empty, new RenderValue.Object(BuildParentCells(parent))))),
                    });
                }
                if (item.ParentId is int pid)
                {
                    // ParentId known but the parent aggregate is not cached.
                    // Report the id under status "partial".
                    var idOnly = new Dictionary<string, RenderCell>(StringComparer.Ordinal)
                    {
                        ["id"] = RenderCell.Integer(pid),
                    };
                    return new RenderNode.Document(null, new List<DocumentField>
                    {
                        new("status", KeyValue("status", RenderCell.String("partial"))),
                        new("value", KeyValue("value", new RenderCell(string.Empty, new RenderValue.Object(idOnly)))),
                    });
                }
                return new RenderNode.Document(null, new List<DocumentField>
                {
                    new("status", KeyValue("status", RenderCell.String(linksVerifiedAt is null ? "unknown" : "absent"))),
                });
            }
            default:
                return UnknownSection();
        }
    }

    private static Dictionary<string, RenderCell> BuildLinkCells(WorkItemLink link)
        => new(StringComparer.Ordinal)
        {
            ["sourceId"] = RenderCell.Integer(link.SourceId),
            ["targetId"] = RenderCell.Integer(link.TargetId),
            ["linkType"] = RenderCell.String(link.LinkType ?? string.Empty),
        };

    private static Dictionary<string, RenderCell> BuildChildCells(WorkItem child)
        => new(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(child.Id),
            ["title"] = RenderCell.String(child.Title ?? string.Empty),
            ["type"] = RenderCell.String(child.Type.ToString()),
            ["state"] = RenderCell.String(child.State ?? string.Empty),
        };

    private static Dictionary<string, RenderCell> BuildParentCells(WorkItem parent)
        => new(StringComparer.Ordinal)
        {
            ["id"] = RenderCell.Integer(parent.Id),
            ["title"] = RenderCell.String(parent.Title ?? string.Empty),
            ["type"] = RenderCell.String(parent.Type.ToString()),
            ["state"] = RenderCell.String(parent.State ?? string.Empty),
        };

    // ── Freshness / completeness ───────────────────────────────────────

    private static RenderNode.Document BuildFreshness(WorkItem item, DateTimeOffset? linksVerifiedAt)
    {
        return new RenderNode.Document(null, new List<DocumentField>
        {
            new("linksVerifiedAt", KeyValue("linksVerifiedAt", InstantCell(linksVerifiedAt))),
            new("hasLocalChanges", KeyValue("hasLocalChanges", RenderCell.Boolean(item.IsDirty))),
            new("lastSyncedAt", KeyValue("lastSyncedAt", InstantCell(item.LastSyncedAt))),
        });
    }

    private static RenderNode.Document BuildCompleteness(ReadRoute route)
    {
        // `fullRead: false` marks the projection; a consumer that needs
        // the full item drops --fields/--sections and reruns.
        return new RenderNode.Document(null, new List<DocumentField>
        {
            new("fullRead", KeyValue("fullRead", RenderCell.Boolean(false))),
            new("route", KeyValue("route", RenderCell.String(route == ReadRoute.Refresh ? "refresh" : "cache"))),
        });
    }

    // ── Cell helpers ───────────────────────────────────────────────────

    private static RenderNode.KeyValue KeyValue(string key, RenderCell cell)
        => new(key, cell);

    private static RenderCell ConnectionCell(string? connection)
        => connection is null
            ? new RenderCell(string.Empty, new RenderValue.Null())
            : RenderCell.String(connection);

    private static RenderCell RevisionCell(int revision)
        => revision > 0
            ? RenderCell.Integer(revision)
            : new RenderCell(string.Empty, new RenderValue.Null());

    private static RenderCell InstantCell(DateTimeOffset? instant)
        => instant is { } when_
            ? new RenderCell(when_.ToString("O", System.Globalization.CultureInfo.InvariantCulture), new RenderValue.DateTime(when_))
            : new RenderCell(string.Empty, new RenderValue.Null());

    private static RenderCell ArrayCell(IReadOnlyList<RenderCell> items)
        => new(string.Empty, new RenderValue.Array(items));

    /// <summary>
    /// Stderr disclosure text for a cache-miss set: one line, comma-separated
    /// <c>#N</c> tokens in requested order.
    /// </summary>
    public static string FormatMissingMessage(IReadOnlyList<int> missing)
    {
        if (missing.Count == 0)
            return string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.Append("Work items not found in cache: ");
        for (int i = 0; i < missing.Count; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append('#').Append(missing[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        sb.Append(". Run 'twig show <id> --refresh' to fetch.");
        return sb.ToString();
    }
}
