using Twig.Domain.Interfaces;
using Twig.Domain.Services.ChangeProposals;
using Twig.Domain.Services.Plan;
using Twig.Rendering;
using Twig.RenderTree;

namespace Twig.Browser;

/// <summary>Captures one canonical proposal; view gestures never preview a replacement or publish.</summary>
internal sealed class BrowserReviewModule(BrowserOperationModule operations, IPlanLifecycleService lifecycle,
    IPlanOriginProvider origins, RendererFactory renderers)
{
    internal Task<BrowserOperationResult<BrowserReviewObservation>> OpenAsync(string? file, BrowserCapture captured,
        CancellationToken ct = default) => operations.ExecuteAsync(captured, async (capture, token) =>
    {
        string? expectedDigest;
        if (file is null)
        {
            var latest = await lifecycle.LatestAsync(token);
            if (!latest.Found)
                return BrowserReviewObservation.Empty(capture, renderers);
            if (latest.Error is not null || latest.File is null || latest.Digest is null)
                throw Refuse(latest.Error ?? "The latest unresolved proposal has no qualified file and digest. No older proposal was substituted.");
            file = latest.File;
            expectedDigest = latest.Digest;
        }
        else
        {
            var validation = await lifecycle.ValidateAsync(file, token);
            if (!validation.IsValid || validation.Digest is null)
                throw Refuse(Issues(validation.Issues, "The proposal file is not valid."));
            expectedDigest = validation.Digest;
        }
        // Resolve once from the captured workspace, never from mutable ambient cwd.
        var path = Path.GetFullPath(file, capture.Origin.WorktreeRoot);
        var origin = await origins.GetOriginAsync(token);
        var preview = await lifecycle.PreviewExpectedAsync(path, expectedDigest, token);
        if (preview.Digest != expectedDigest || preview.ReviewModel is not { } model || model.Digest != expectedDigest)
            throw Refuse(Issues(preview.Issues, "The proposal changed while it was opened. No replacement was captured."));
        if (!ChangeProposalReviewRenderer.IsSupported(model.ModelVersion))
            throw Refuse($"Cannot review model version {model.ModelVersion}. Refusing partial review; upgrade twig.");
        await ValidateExactAsync(path, expectedDigest, origin, token);
        return new BrowserReviewObservation(capture, path, expectedDigest, origin, model, preview.Issues, renderers);
    }, ct);

    internal async Task<BrowserOperationResult<bool>> ValidateAsync(BrowserReviewObservation observation,
        CancellationToken ct = default)
    {
        if (!observation.IsValid)
            return new(false, new(BrowserReadFailureKind.Unavailable, "This review was invalidated. Explicitly open the proposal again."), observation.Capture);
        var result = await operations.ExecuteAsync(observation.Capture, async (_, token) =>
        {
            if (!observation.IsEmpty)
            {
                try { await ValidateExactAsync(observation.FilePath!, observation.Digest!, observation.Origin!, token); }
                catch (BrowserReadException invalid)
                {
                    observation.Invalidate(invalid.Message);
                    throw;
                }
            }
            return true;
        }, ct);
        if (result.Failure is { Kind: BrowserReadFailureKind.AuthorityInvalidated })
            observation.Invalidate(result.Failure.Message);
        return result;
    }

    private async Task ValidateExactAsync(string path, string digest, PlanOrigin origin, CancellationToken ct)
    {
        var validation = await lifecycle.ValidateAsync(path, ct);
        if (!validation.IsValid || validation.Digest != digest)
            throw Refuse("The captured proposal file changed or is unavailable. Its old content was cleared; no replacement was opened.");
        var status = await lifecycle.StatusAsync(path, ct);
        if (status is null || !status.Found || status.Digest != digest || status.Issues.Count != 0 || status.Replacement is not null)
            throw Refuse("The captured proposal is no longer the same qualified native journal entry. Its old content was cleared.");
        var current = await origins.GetOriginAsync(ct);
        if (status.Origin is null || status.Origin != origin || current != origin)
            throw new BrowserReadException(BrowserReadFailureKind.AuthorityInvalidated,
                "The captured proposal origin changed or is unknown. Its old content was cleared; explicitly reconnect and open a qualified proposal.");
        // Status reparses the original path, not any latest-selected proposal. The second
        // validation also fences a file changed while the journal/origin reads awaited I/O.
        validation = await lifecycle.ValidateAsync(path, ct);
        if (!validation.IsValid || validation.Digest != digest)
            throw Refuse("The captured proposal file changed while its origin was checked. Its old content was cleared.");
    }

    private static BrowserReadException Refuse(string message) => new(BrowserReadFailureKind.Unavailable, message);
    private static string Issues(IReadOnlyList<PlanValidationIssue> issues, string fallback)
        => issues.Count == 0 ? fallback : string.Join("\n", issues.Select(issue => issue.Message));
}

/// <summary>Retained semantic capture and pure native reflow. It contains no lifecycle or publication callback.</summary>
internal sealed class BrowserReviewObservation
{
    private readonly RendererFactory _renderers;
    private IReadOnlyList<RenderNode> _brief = [];
    private IReadOnlyList<RenderNode> _full = [];
    private string? _invalidReason;

    internal BrowserReviewObservation(BrowserCapture capture, string file, string digest, PlanOrigin origin,
        ChangeProposalReviewModel model, IReadOnlyList<PlanValidationIssue> issues, RendererFactory renderers)
    {
        Capture = capture;
        FilePath = file;
        Digest = digest;
        Origin = origin;
        _renderers = renderers;
        Model = CaptureModel(model);
        var warnings = issues.Select(issue => (RenderNode)new RenderNode.Text(issue.Message, Severity.Error)).ToArray();
        _brief = [.. warnings, .. ChangeProposalReviewRenderer.Render(Model, false)];
        _full = [.. warnings, .. ChangeProposalReviewRenderer.Render(Model, true)];
        BriefAnsi = Render(120, false);
        FullAnsi = Render(120, true);
    }

    private BrowserReviewObservation(BrowserCapture capture, RendererFactory renderers)
    {
        Capture = capture;
        _renderers = renderers;
        IsEmpty = true;
        BriefAnsi = FullAnsi = "No unresolved proposal has been previewed in this workspace. Open an explicit proposal file to review it.";
    }

    internal static BrowserReviewObservation Empty(BrowserCapture capture, RendererFactory renderers) => new(capture, renderers);
    internal BrowserCapture Capture { get; }
    internal string? Digest { get; }
    internal string? FilePath { get; }
    internal PlanOrigin? Origin { get; }
    internal ChangeProposalReviewModel? Model { get; private set; }
    internal bool IsEmpty { get; }
    internal bool IsValid => _invalidReason is null;
    internal string BriefAnsi { get; private set; }
    internal string FullAnsi { get; private set; }

    internal string Render(int width, bool full)
    {
        if (width < 1 || width > 10000) throw new ArgumentOutOfRangeException(nameof(width));
        if (_invalidReason is not null) return _invalidReason;
        if (IsEmpty) return BriefAnsi;
        return _renderers.CaptureHuman(new RenderTree.RenderTree(
            [new RenderNode.Section(null, full ? _full : _brief)]), width, "always").Text;
    }

    internal void Invalidate(string reason)
    {
        _invalidReason = RichHtmlRenderer.SafeText(reason);
        Model = null;
        _brief = [];
        _full = [];
        BriefAnsi = FullAnsi = _invalidReason;
    }

    private static ChangeProposalReviewModel CaptureModel(ChangeProposalReviewModel model) => model with
    {
        AffectedItems = Array.AsReadOnly(model.AffectedItems.ToArray()),
        ContextItems = Array.AsReadOnly(model.ContextItems.ToArray()),
        AuthorizationChoices = Array.AsReadOnly(model.AuthorizationChoices.ToArray()),
        Blockers = Array.AsReadOnly(model.Blockers.ToArray()),
        PolicyDecisions = Array.AsReadOnly(model.PolicyDecisions.ToArray()),
        Operations = Array.AsReadOnly(model.Operations.Select(operation => operation with
        {
            Preconditions = Array.AsReadOnly(operation.Preconditions.ToArray()),
            Consequences = Array.AsReadOnly(operation.Consequences.ToArray()),
        }).ToArray()),
    };
}
