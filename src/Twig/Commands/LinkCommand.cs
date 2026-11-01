using Twig.Infrastructure.Telemetry;
using System.Diagnostics;
using System.Globalization;
using Twig.Core;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Persistence;
using Twig.Infrastructure.Persistence.ProviderTargets;
using Twig.Formatters;
using Twig.Infrastructure.Services;
using Twig.RenderTree;
using Twig.Rendering;

namespace Twig.Commands;

/// <summary>Published relationships through the admitted Core proposal and journal workflow.</summary>
internal sealed class LinkCommand(
    RendererFactory rendererFactory,
    CoreWorkspaceFactory? factory = null,
    ITelemetryClient? telemetryClient = null,
    TextWriter? stderr = null)
{
    private readonly TextWriter _stderr = stderr ?? Console.Error;

    public Task<int> ParentAsync(int targetId, int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, int? child = null, int? positionalChild = null)
        => ExecuteAsync("parent", targetId, id, child, null, false, outputFormat, ct, positionalChild);

    public Task<int> UnparentAsync(int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, int? child = null, int? positionalChild = null)
        => ExecuteAsync("unparent", null, id, child, null, true, outputFormat, ct, positionalChild);

    public Task<int> ReparentAsync(int targetId, int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default, int? child = null, int? positionalChild = null)
        => ExecuteAsync("reparent", targetId, id, child, null, false, outputFormat, ct, positionalChild);

    public Task<int> DependencyAsync(string linkType, int targetId, int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => ExecuteAsync(linkType, targetId, id, null, null, false, outputFormat, ct);

    public Task<int> RelatedAsync(int targetId, int? id = null, string? comment = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => ExecuteAsync("related", targetId, id, null, comment, false, outputFormat, ct);

    public Task<int> UnrelateAsync(int targetId, int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => ExecuteAsync("related", targetId, id, null, null, true, outputFormat, ct);

    public Task<int> UnlinkDependencyAsync(string linkType, int targetId, int? id = null,
        string outputFormat = OutputFormatterFactory.DefaultFormat, CancellationToken ct = default)
        => ExecuteAsync(linkType.Trim().ToLowerInvariant() is "predecessor" or "successor" or "related" ? linkType : "",
            targetId, id, null, null, true, outputFormat, ct);

    private async Task<int> ExecuteAsync(string verb, int? targetId, int? id, int? child, string? comment,
        bool remove, string output, CancellationToken ct, int? positionalChild = null)
    {
        var started = Stopwatch.GetTimestamp();
        var exit = 1;
        var format = OutputFormats.Normalize(output);
        var kind = verb.Trim().ToLowerInvariant();
        // Never derive telemetry command metadata from an unrecognized user-supplied link type.
        var command = kind switch
        {
            "parent" => "link-parent", "unparent" => "link-unparent", "reparent" => "link-reparent",
            "predecessor" => "link-predecessor", "successor" => "link-successor",
            "related" => remove ? "link-unrelate" : "link-related", _ => "link-unlink",
        };
        try
        {
            return exit = await RunAsync();
        }
        finally
        {
            try { TelemetryHelper.TrackCommand(telemetryClient, command, format ?? "invalid", exit, started); }
            catch (Exception) { /* Optional telemetry cannot change the command outcome. */ }
        }

        async Task<int> RunAsync()
        {
            if (format is null) return Error(OutputFormats.Default, $"Unsupported output format. Use {OutputFormats.Describe()}.", 2);
            if (format == "ids") return Error(format, "Bare IDs discard relationship and journal evidence. Use -o json or -o minimal.", 2);
            if (new[] { id, child, positionalChild }.Where(value => value.HasValue).Distinct().Count() > 1)
                return Error(format, "Conflicting --id and child targets; no relationship was changed.", 2);
            var explicitId = child ?? id ?? positionalChild;
            if (explicitId is <= 0 || targetId is <= 0)
                return Error(format, "Published link targets must be positive native IDs. Use seed link for unpublished seeds.", 2);
            if (kind is not ("parent" or "unparent" or "reparent" or "predecessor" or "successor" or "related"))
                return Error(format, "Supported unlink types: predecessor, successor, related. Use link unparent for hierarchy.", 2);
            if (explicitId is not null && explicitId == targetId)
                return Error(format, "Cannot link a work item to itself.", 2);
            if (id is null && format != "human")
                return Error(format, "Structured relationship operations require an explicit --id option; --child alone is not machine authorization.", 2);
            if (kind == "reparent")
                return Error(format, "Reparent is unsupported: Core cannot bind parent replacement to one safe journaled request. No link was removed.");
            if (comment is not null)
                return Error(format, "--comment is unsupported by the typed relationship effect. No comment or link was sent.");
            if (factory is null)
                return Error(format, "Published links require an explicitly initialized Core workspace; no legacy writer was used.");
            var opened = await factory.OpenAsync(ct);
            if (!opened.IsSuccess) return Error(format, opened.Error.Message);
            using var handle = opened.Value;
            var workspace = handle.Workspace;
            if (workspace.Connection.Provider.Value != "ado")
                return Error(format, "Native CLI relationships are qualified only for the admitted ADO workspace.");
            ItemKey source;
            if (explicitId.HasValue) source = Key(explicitId.Value);
            else
            {
                var context = await workspace.Context.ReadAsync(ct);
                if (!context.IsSuccess) return Error(format, context.Error.Message);
                if (context.Value.Position < 0)
                    return Error(format, "No active Core item is selected. Run 'twig set <id>' or pass --id.");
                source = context.Value.History[context.Value.Position].Resolve(workspace.Connection);
                if (source.IsLocal)
                {
                    var captured = await workspace.SnapshotAsync(ct);
                    if (!captured.IsSuccess) return Error(format, captured.Error.Message);
                    source = captured.Value.Mappings.FirstOrDefault(mapping => mapping.Local == source)?.Remote ?? source;
                }
                if (source.IsLocal || !source.BelongsTo(workspace.Connection))
                    return Error(format, "Select a published native item or pass --id. Unpublished links belong to seed link.");
            }
            if (targetId.HasValue && source == Key(targetId.Value))
                return Error(format, "Cannot link a work item to itself.", 2);
            var schema = await workspace.SchemaAsync(ct);
            if (!schema.IsSuccess) return Error(format, schema.Error.Message);
            var shape = kind is "parent" or "unparent" ? RelationShape.Hierarchy
                : kind == "related" ? RelationShape.Association : RelationShape.Dependency;
            var kinds = schema.Value.Relations.Where(relation => relation.Shape == shape
                && relation.Directed == (shape != RelationShape.Association)).ToArray();
            if (kinds.Length != 1)
                return Error(format, "A unique discovered canonical relationship shape is required; no native reference was guessed.");
            var relation = kinds[0];
            var sourceRead = await workspace.ReadAsync(source, ct);
            if (!sourceRead.IsSuccess) return Error(format, sourceRead.Error.Message);
            var snapshot = await workspace.SnapshotAsync(ct);
            if (!snapshot.IsSuccess) return Error(format, snapshot.Error.Message);
            ItemKey target;
            if (kind == "unparent")
            {
                var parents = snapshot.Value.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Provider
                    && !edge.Deleted && edge.Kind == relation.Reference && edge.To == source).Select(edge => edge.From).Distinct().ToArray();
                if (parents.Length != 1)
                    return Error(format, "Exactly one observed native parent is required for unparenting.");
                target = parents[0];
            }
            else target = Key(targetId!.Value);
            var targetRead = await workspace.ReadAsync(target, ct);
            if (!targetRead.IsSuccess) return Error(format, targetRead.Error.Message);
            snapshot = await workspace.SnapshotAsync(ct);
            if (!snapshot.IsSuccess) return Error(format, snapshot.Error.Message);
            if (!sourceRead.Value.RelationshipsComplete || !targetRead.Value.RelationshipsComplete)
                return Error(format, "Complete native endpoint observations are required.");
            var reverse = kind is "parent" or "unparent" or "predecessor";
            var from = reverse ? targetRead.Value : sourceRead.Value;
            var to = reverse ? source : target;
            var exists = snapshot.Value.Relationships.Any(edge => edge.Origin == RelationshipOrigin.Provider
                && !edge.Deleted && edge.Kind == relation.Reference
                && (edge.From == from.Key && edge.To == to || !relation.Directed && edge.To == from.Key && edge.From == to));
            if (kind == "parent" && !exists && snapshot.Value.Relationships.Any(edge =>
                edge.Origin == RelationshipOrigin.Provider && !edge.Deleted && edge.Kind == relation.Reference && edge.To == source))
                return Error(format, "The child already has a parent. Parent replacement remains unsupported; no link was removed.");
            if (remove && !exists) return Error(format, "The observed native relationship to remove is absent.");
            if (!remove && exists)
                return Error(format, "The native relationship already exists; no new effect or verification is claimed.");
            var prepared = await workspace.PrepareAsync([new("relationship", remove ? OpKind.Unrelate : OpKind.Relate,
                from.Key, from.Revision, other: to, relation: relation.Reference)], ct);
            if (!prepared.IsSuccess) return Error(format, prepared.Error.Message);
            var applied = await workspace.ApplyAsync(prepared.Value, prepared.Value.Digest, ct);
            if (!applied.IsSuccess) return Error(format, applied.Error.Message);
            if (applied.Value.Error is { } failure) return Error(format, failure.Message);
            if (!applied.Value.EffectsVerified)
                return Error(format, "The relationship did not verify; its journal remains authoritative. No retry was performed.");
            snapshot = await workspace.SnapshotAsync(ct);
            if (!snapshot.IsSuccess) return Error(format, snapshot.Error.Message);
            Render(kind == "parent" ? "linkParented" : kind == "unparent" ? "linkUnparented" : remove ? "linkRemoved" : "linkAdded",
                remove ? $"Removed {kind} link between #{source.NativeId} and #{target.NativeId}."
                    : $"#{source.NativeId} now has {kind} #{target.NativeId}.", prepared.Value.Digest, "Verified");
            return 0;

            ItemKey Key(int number) => ItemKey.Remote(workspace.Connection.Provider, workspace.Connection.ResourceScope,
                number.ToString(CultureInfo.InvariantCulture));

            void Render(string record, string message, string? digest, string status)
            {
                var links = snapshot.Value.Relationships.Where(edge => edge.Origin == RelationshipOrigin.Provider
                    && !edge.Deleted && (edge.From == source || edge.To == source)).ToArray();
                var rows = links.Select(edge => new RenderRow("link", new Dictionary<string, RenderCell>
                {
                    ["sourceId"] = RenderCell.Integer(int.Parse(source.NativeId!, CultureInfo.InvariantCulture)),
                    ["targetId"] = RenderCell.Integer(int.Parse((edge.From == source ? edge.To : edge.From).NativeId!, CultureInfo.InvariantCulture)),
                    ["linkType"] = RenderCell.String(Friendly(edge)),
                })).ToArray();
                var fields = new List<DocumentField>
                {
                    new("message", new RenderNode.KeyValue("message", RenderCell.String(message))),
                    new("status", new RenderNode.KeyValue("status", RenderCell.String(status))),
                    new("count", new RenderNode.KeyValue("count", RenderCell.Integer(rows.Length))),
                    new("links", new RenderNode.Table(null, [new("sourceId", "Source"), new("targetId", "Target"), new("linkType", "Type")], rows)),
                };
                if (digest is not null) fields.Add(new("digest", new RenderNode.KeyValue("digest", RenderCell.String(digest))));
                rendererFactory.GetRenderer(format).Render(new([new RenderNode.Document(record, fields)]));
            }

            string Friendly(Relationship edge) => schema.Value.Relations.FirstOrDefault(value => value.Reference == edge.Kind)?.Shape switch
            {
                RelationShape.Hierarchy => edge.From == source ? "child" : "parent",
                RelationShape.Dependency => edge.From == source ? "successor" : "predecessor",
                RelationShape.Association => "related",
                _ => edge.Kind,
            };
        }
    }

    private int Error(string output, string message, int code = 1)
    {
        CommandError.Write(rendererFactory, _stderr, output, message);
        return code;
    }
}
