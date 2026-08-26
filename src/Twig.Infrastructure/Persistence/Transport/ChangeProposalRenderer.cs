using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §10.1–§10.3 renderer implementation. Owns the four-clause
/// selection rule and the invocation-time refusal fallback. Both
/// paths guarantee the universal terminal/text fallback (§10.4) —
/// transport selects appearance, never authority.
/// </summary>
internal sealed class ChangeProposalRenderer : IChangeProposalRenderer
{
    private readonly IChangeProposalPresentationSupportRegistry _supportRegistry;
    private readonly ITerminalTextChangeProposalRenderer _terminalTextRenderer;
    private readonly IReadOnlyDictionary<RichAdapterId, IRichChangeProposalRenderer> _richRenderersById;

    public ChangeProposalRenderer(
        IChangeProposalPresentationSupportRegistry supportRegistry,
        ITerminalTextChangeProposalRenderer terminalTextRenderer,
        IEnumerable<IRichChangeProposalRenderer> richRenderers)
    {
        _supportRegistry = supportRegistry;
        _terminalTextRenderer = terminalTextRenderer;
        var byId = new Dictionary<RichAdapterId, IRichChangeProposalRenderer>();
        foreach (var r in richRenderers)
            byId[r.Id] = r;
        _richRenderersById = byId;
    }

    public Presentation SelectPresentation(
        ChangeProposalRenderProposal proposal,
        TransportAttachmentRecord? transportRecord)
    {
        _ = proposal; // opaque payload, not inspected here (§10.5).

        // Clause 1 — null or unreadable.
        if (transportRecord is null)
            return Presentation.TerminalText.Instance;

        // Clause 2 — agent payload present + supported.
        if (transportRecord.Agent is { } agent)
        {
            var id = new RichAdapterId(agent.Target.AdapterId, TransportAdapterRole.Agent);
            if (_supportRegistry.IsSupported(id))
                return new Presentation.RichAdapter(id);
        }

        // Clause 3 — terminal payload present + supported.
        if (transportRecord.Terminal is { } terminal)
        {
            var id = new RichAdapterId(terminal.Target.AdapterId, TransportAdapterRole.Terminal);
            if (_supportRegistry.IsSupported(id))
                return new Presentation.RichAdapter(id);
        }

        // Clause 4 — fallback.
        return Presentation.TerminalText.Instance;
    }

    public async Task<RenderedProposal> RenderAsync(
        ChangeProposalRenderProposal proposal,
        Presentation presentation,
        CancellationToken ct = default)
    {
        switch (presentation)
        {
            case Presentation.TerminalText:
                return _terminalTextRenderer.Render(proposal);

            case Presentation.RichAdapter rich:
                {
                    // 2a — rich renderer not registered / unavailable at
                    // invocation time.
                    if (!_richRenderersById.TryGetValue(rich.AdapterId, out var renderer))
                        return _terminalTextRenderer.Render(proposal);

                    // 2b — invoke; refusal / throw / timeout falls
                    // through to the unchanged terminal-text fallback.
                    try
                    {
                        RichRenderResult result;
                        if (renderer.TimeoutMs > 0)
                        {
                            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var invocation = renderer.RenderAsync(proposal, cts.Token);
                            var winner = await Task.WhenAny(
                                invocation,
                                Task.Delay(renderer.TimeoutMs, cts.Token)).ConfigureAwait(false);
                            if (winner != invocation)
                            {
                                try { cts.Cancel(); } catch { /* best-effort */ }
                                return _terminalTextRenderer.Render(proposal);
                            }
                            result = await invocation.ConfigureAwait(false);
                        }
                        else
                        {
                            result = await renderer.RenderAsync(proposal, ct).ConfigureAwait(false);
                        }

                        return result switch
                        {
                            RichRenderResult.Rendered r => r.Proposal,
                            RichRenderResult.Refused => _terminalTextRenderer.Render(proposal),
                            _ => _terminalTextRenderer.Render(proposal),
                        };
                    }
                    catch (System.OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return _terminalTextRenderer.Render(proposal);
                    }
                    catch
                    {
                        return _terminalTextRenderer.Render(proposal);
                    }
                }

            default:
                // Unreachable — Presentation is a sealed abstract
                // record with two variants — but a defensive fallback
                // is authorization-neutral.
                return _terminalTextRenderer.Render(proposal);
        }
    }
}
