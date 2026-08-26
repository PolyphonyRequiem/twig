using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Twig.Infrastructure.Persistence.Transport;

/// <summary>
/// Contract §10.1–§10.3 renderer implementation. Owns the four-clause
/// selection rule and the invocation-time refusal fallback. Both
/// paths guarantee the universal terminal/text fallback (§10.4) —
/// transport selects appearance, never authority.
/// <para>
/// §10.2 clause 1 gate: the renderer consults
/// <see cref="ITransportAdapterRegistry"/> before evaluating the
/// per-adapter support registry, so a record whose referenced adapter
/// is unregistered forces the terminal-text fallback rather than
/// selecting a rich renderer for the OTHER role. This is what the
/// contract calls "or its adapter is unregistered → TerminalText" and
/// runs BEFORE clauses 2 and 3.
/// </para>
/// <para>
/// §10.3 fallback boundary preservation: rich renderers are resolved
/// via a factory delegate INSIDE the render-time try/fallback boundary,
/// not enumerated in the constructor. A rich renderer that throws
/// during construction therefore triggers the §10.3 unchanged
/// terminal-text fallback rather than preventing this service from
/// resolving at all — DI resolving <c>IEnumerable&lt;IRichChangeProposalRenderer&gt;</c>
/// eagerly would defeat the guarantee in exactly the case it exists for.
/// </para>
/// </summary>
internal sealed class ChangeProposalRenderer : IChangeProposalRenderer
{
    private readonly IChangeProposalPresentationSupportRegistry _supportRegistry;
    private readonly ITransportAdapterRegistry _adapterRegistry;
    private readonly ITerminalTextChangeProposalRenderer _terminalTextRenderer;
    private readonly System.Func<IEnumerable<IRichChangeProposalRenderer>> _richRendererFactory;

    public ChangeProposalRenderer(
        IChangeProposalPresentationSupportRegistry supportRegistry,
        ITransportAdapterRegistry adapterRegistry,
        ITerminalTextChangeProposalRenderer terminalTextRenderer,
        System.Func<IEnumerable<IRichChangeProposalRenderer>> richRendererFactory)
    {
        _supportRegistry = supportRegistry;
        _adapterRegistry = adapterRegistry;
        _terminalTextRenderer = terminalTextRenderer;
        _richRendererFactory = richRendererFactory;
    }

    public Presentation SelectPresentation(
        ChangeProposalRenderProposal proposal,
        TransportAttachmentRecord? transportRecord)
    {
        _ = proposal; // opaque payload, not inspected here (§10.5).

        // Clause 1 — null, unreadable, OR any referenced adapter is
        // not registered. §10.2 clause 1 forces TerminalText BEFORE
        // clauses 2 and 3: a record whose agent adapter is unknown
        // must not silently select a rich terminal presentation for
        // the OTHER role.
        if (transportRecord is null)
            return Presentation.TerminalText.Instance;
        if (transportRecord.Agent is not null
            && !_adapterRegistry.Resolve(transportRecord.Agent.Target.AdapterId).IsSuccess)
            return Presentation.TerminalText.Instance;
        if (transportRecord.Terminal is not null
            && !_adapterRegistry.Resolve(transportRecord.Terminal.Target.AdapterId).IsSuccess)
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
                    // invocation time. Resolve INSIDE the try/fallback
                    // boundary so a renderer that throws in its
                    // constructor still routes through the §10.3
                    // terminal-text fallback — DI eagerly enumerating
                    // the registered renderers at ChangeProposalRenderer
                    // construction time would prevent this service from
                    // resolving at all in exactly the case §10.3 exists
                    // for.
                    IRichChangeProposalRenderer? renderer;
                    try
                    {
                        renderer = _richRendererFactory()
                            .FirstOrDefault(r => r.Id == rich.AdapterId);
                    }
                    catch
                    {
                        return _terminalTextRenderer.Render(proposal);
                    }
                    if (renderer is null)
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
