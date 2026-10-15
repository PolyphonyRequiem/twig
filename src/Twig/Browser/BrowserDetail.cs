using Twig.Domain.Aggregates;
using Twig.Domain.Common;
using Twig.Domain.Services;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Auth;
using Twig.Rendering;

namespace Twig.Browser;

internal sealed record BrowserDetailRequest(int Id, int Width = 80, bool Ansi = true,
    string? ExpectedBench = null, string? ExpectedBinding = null, string? ExpectedIdentity = null,
    bool Sync = false, BrowserCapture? Captured = null);

/// <summary>Opaque selection and admitted native origin evidence, never credentials.</summary>
internal sealed record BrowserCapture(string BenchId, string BenchName, ResolvedConnectionBinding Origin,
    string BenchContentsDigest)
{
    internal string BindingId => Origin.Binding.BindingId;
    internal string IdentityId => Origin.Identity.IdentityId;
    internal long BindingRevision => Origin.Binding.Revision;
    internal string SelectionSource => Origin.SelectionSource;
    internal long SelectionRevision => Origin.SelectionRevision;
    internal string? StorageGeneration => Origin.StorageGeneration;

    internal bool Matches(Bench current)
        => BenchId == current.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
            && BenchName == current.Name
            && BenchContentsDigest == BenchQueryRule.ContentsDigest(current.Selectors);

    internal bool Matches(ResolvedConnectionBinding current)
        => SameOrigin(Origin, current);

    internal static bool SameOrigin(ResolvedConnectionBinding expected, ResolvedConnectionBinding current)
        => expected.Binding == current.Binding
            && expected.WorktreeRoot == current.WorktreeRoot
            && expected.SelectionSource == current.SelectionSource
            && expected.SelectionRevision == current.SelectionRevision
            && expected.Operation == current.Operation
            && expected.StorageGeneration == current.StorageGeneration
            && SamePrincipal(expected.Identity, current.Identity);

    // Registration renewal can rotate credentials and update display metadata without
    // becoming a new actor. Compare proved principal authority, not a token or name.
    private static bool SamePrincipal(AuthenticationIdentity left, AuthenticationIdentity right)
        => left.IdentityId == right.IdentityId && left.Method == right.Method
            && left.TenantId == right.TenantId && left.ObjectId == right.ObjectId
            && left.Issuer == right.Issuer && left.AuthorityHost == right.AuthorityHost
            && left.AdoPrincipalId == right.AdoPrincipalId && left.AdoAuthority == right.AdoAuthority;
}

internal enum BrowserReadFailureKind { Usage, Unavailable, AuthorityInvalidated, Canceled }
internal sealed record BrowserReadFailure(BrowserReadFailureKind Kind, string Message);
/// <summary>Capture is refusal context, not authorization or a renderable observation.</summary>
internal sealed record BrowserDetailResult(BrowserDetailObservation? Observation, BrowserReadFailure? Failure,
    BrowserCapture? Capture = null);
internal sealed record BrowserRenderedDetail(string Ansi, string Title);

/// <summary>
/// An admitted cached observation, not a live database view. Related facts are cached
/// only; link verification does not imply that every related target is materialized.
/// Rendering retains private copies and has no repository/provider/file dependencies.
/// </summary>
internal sealed class BrowserDetailObservation
{
    private readonly BrowserDetailSnapshot _snapshot;
    internal BrowserDetailObservation(BrowserCapture capture, BrowserDetailSnapshot snapshot,
        DateTimeOffset? linksVerifiedAt, BrowserRenderedDetail initialPresentation)
    {
        Capture = capture;
        _snapshot = snapshot;
        WorkItemId = snapshot.Item.Id;
        Revision = snapshot.Item.Revision;
        Title = initialPresentation.Title;
        LastSyncedAt = snapshot.Item.LastSyncedAt;
        HasLocalChanges = snapshot.Item.IsDirty || snapshot.Item.PendingNotes.Count > 0 || snapshot.Changes.Count > 0;
        LinksVerifiedAt = linksVerifiedAt;
        InitialPresentation = initialPresentation;
    }

    internal BrowserCapture Capture { get; }
    internal int WorkItemId { get; }
    internal int Revision { get; }
    internal string Title { get; }
    internal DateTimeOffset? LastSyncedAt { get; }
    internal bool HasLocalChanges { get; }
    internal DateTimeOffset? LinksVerifiedAt { get; }
    internal BrowserRenderedDetail InitialPresentation { get; }
    internal Task<BrowserRenderedDetail> RenderAsync(int width, bool ansi = true)
        => _snapshot.RenderAsync(width, ansi);
    internal Task<IReadOnlyList<BrowserSourceRow>> RenderRowsAsync(int width) => _snapshot.RenderRowsAsync(width);
    internal Task<IReadOnlyList<BrowserSourceRow>> RenderEditingRowsAsync(int width, WorkItemSnapshot baseline,
        IReadOnlyDictionary<string, string?> values, string? selectedField = null) => _snapshot.RenderEditingRowsAsync(width, baseline, values, selectedField);
}

internal sealed record BrowserDetailSnapshot(WorkItem Item, WorkItem? Parent,
    IReadOnlyList<WorkItem> Children, IReadOnlyList<FieldDefinition> Definitions,
    IReadOnlyList<StatusFieldEntry>? StatusFields, IReadOnlyList<WorkItemLink> Links,
    IReadOnlyList<PendingChangeRecord> Changes, (int Done, int Total)? ChildProgress,
    int CacheStaleMinutes, SpectreTheme Theme)
{
    internal async Task<BrowserRenderedDetail> RenderAsync(int width, bool ansi)
        => new(await RenderAnsiAsync(width, ansi, false), RichHtmlRenderer.SafeText(Item.Title));

    internal async Task<IReadOnlyList<BrowserSourceRow>> RenderRowsAsync(int width)
        => BrowserTerminalText.ContentRows(await RenderAnsiAsync(width, true, true), width);

    internal async Task<IReadOnlyList<BrowserSourceRow>> RenderEditingRowsAsync(int width, WorkItemSnapshot baseline,
        IReadOnlyDictionary<string, string?> values, string? selectedField = null)
    {
        var fields = new Dictionary<string, string?>(baseline.Fields, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values) fields[key] = value;
        var item = EditingMapper.Map(baseline with
        {
            Title = fields.GetValueOrDefault("System.Title", baseline.Title) ?? string.Empty,
            State = fields.GetValueOrDefault("System.State", baseline.State) ?? string.Empty,
            Fields = fields,
        });
        return BrowserTerminalText.ContentRows(await (this with { Item = item }).RenderAnsiAsync(width, true, true, selectedField), width);
    }

    private static readonly WorkItemMapper EditingMapper = new();

    private async Task<string> RenderAnsiAsync(int width, bool ansi, bool sourceSections, string? selectedField = null)
    {
        if (width < 1 || width > 10000)
            throw new ArgumentException("Width must be between 1 and 10000 columns.");
        using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
        var console = Spectre.Console.AnsiConsole.Create(new Spectre.Console.AnsiConsoleSettings
        {
            Out = new Spectre.Console.AnsiConsoleOutput(writer),
            Ansi = ansi ? Spectre.Console.AnsiSupport.Yes : Spectre.Console.AnsiSupport.No,
            ColorSystem = ansi ? Spectre.Console.ColorSystemSupport.TrueColor : Spectre.Console.ColorSystemSupport.NoColors,
            Interactive = Spectre.Console.InteractionSupport.No,
        });
        console.Profile.Width = width;
        console.Profile.Capabilities.Ansi = ansi;
        console.Profile.Capabilities.ColorSystem = ansi ? Spectre.Console.ColorSystem.TrueColor : Spectre.Console.ColorSystem.NoColors;
        console.Profile.Capabilities.Links = false;
        console.Profile.Capabilities.Interactive = false;
        console.Profile.Capabilities.Unicode = true;
        var view = await new SpectreRenderer(console, Theme).BuildStatusViewAsync(Item,
            () => Task.FromResult(Changes), fieldDefinitions: Definitions, statusFieldEntries: StatusFields,
            childProgress: ChildProgress, links: Links, parent: Parent, children: Children,
            cacheStaleMinutes: CacheStaleMinutes, fullContent: true,
            sourceSection: sourceSections ? (key, value) => new SourceSection(key, value, key == selectedField) : null);
        console.Write(sourceSections ? new ContentSurface(view) : view);
        return writer.ToString();
    }

    private sealed class ContentSurface(Spectre.Console.Rendering.IRenderable inner) : Spectre.Console.Rendering.IRenderable
    {
        private readonly Dictionary<Spectre.Console.Style, Spectre.Console.Style> _styles = [];
        public Spectre.Console.Rendering.Measurement Measure(Spectre.Console.Rendering.RenderOptions options, int maxWidth)
            => inner.Measure(options, maxWidth);
        public IEnumerable<Spectre.Console.Rendering.Segment> Render(Spectre.Console.Rendering.RenderOptions options, int maxWidth)
        {
            foreach (var segment in inner.Render(options, maxWidth))
            {
                if (segment.IsControlCode || segment.IsLineBreak) { yield return segment; continue; }
                if (!_styles.TryGetValue(segment.Style, out var style))
                    _styles.Add(segment.Style, style = BrowserTerminalFrame.OnContentSurface(segment.Style));
                yield return new(segment.Text, style);
            }
        }
    }

    private sealed class SourceSection(string key, Spectre.Console.Rendering.IRenderable inner, bool selected) : Spectre.Console.Rendering.IRenderable
    {
        private readonly Spectre.Console.Rendering.Segment _start = Spectre.Console.Rendering.Segment.Control(BrowserTerminalText.SourceMarker(key));
        private static readonly Spectre.Console.Rendering.Segment End = Spectre.Console.Rendering.Segment.Control(BrowserTerminalText.SourceMarker(null));

        public Spectre.Console.Rendering.Measurement Measure(Spectre.Console.Rendering.RenderOptions options, int maxWidth)
            => inner.Measure(options, maxWidth);

        public IEnumerable<Spectre.Console.Rendering.Segment> Render(Spectre.Console.Rendering.RenderOptions options, int maxWidth)
        {
            var marked = false;
            foreach (var segment in inner.Render(options, maxWidth))
            {
                if (segment.IsLineBreak)
                {
                    if (marked) yield return End;
                    marked = false;
                    yield return segment;
                }
                else
                {
                    if (!marked) { yield return _start; marked = true; }
                    yield return selected && !segment.IsControlCode
                        ? new(segment.Text, BrowserTerminalFrame.OnContentSurface(segment.Style, selected: true))
                        : segment;
                }
            }
            if (marked) yield return End;
        }
    }
}
