using System.Globalization;
using Twig.Commands;
using Twig.Domain.Aggregates;
using Twig.Domain.Extensions;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Sync;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Rendering;

namespace Twig.Browser;

/// <summary>
/// Owns admission, cached facts, selected-root pull and final mutable-origin checks.
/// Provider and repository leases exclude supported exclusive transitions only during
/// the operation. SQLite calls are serialized by the caller; this is not a physically
/// atomic transaction spanning the registry, configuration files and cache mirrors.
/// </summary>
internal sealed class BrowserReadModule(CommandContext ctx, CurrentBenchResolver resolver,
    IWorkItemRepository repository, IFieldDefinitionStore fieldDefinitions,
    IAuthenticationProvider authentication, IConnectionBindingService bindings, TwigPaths paths,
    IWorkItemLinkRepository links, IPendingChangeStore pendingChanges, StatusFieldConfigReader statusFields,
    SyncCoordinatorFactory syncCoordinators, IProcessConfigurationProvider processConfiguration, SpectreTheme theme)
{
    internal async Task<BrowserDetailResult> ReadDetailAsync(BrowserDetailRequest request, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested) return Failure(BrowserReadFailureKind.Canceled, "Detail read canceled.");
        if (request.Id == 0) return Failure(BrowserReadFailureKind.Usage, "A nonzero work-item ID or negative seed alias is required.");
        if (request.Width < 1 || request.Width > 10000) return Failure(BrowserReadFailureKind.Usage, "Width must be between 1 and 10000 columns.");
        if (request.Sync && request.Id < 0) return Failure(BrowserReadFailureKind.Usage, "Local seeds cannot sync or publish from the detail viewer.");

        IDisposable? admission = null;
        IDisposable? operation = null;
        BrowserCapture? capture = request.Captured;
        BrowserCapture? admittedCapture = null;
        ResolvedConnectionBinding? origin = null;
        try
        {
            ResolvedConnectionBinding binding;
            try
            {
                admission = await ConnectionOperationAdmission.AcquireAsync(authentication, ct);
                ct.ThrowIfCancellationRequested();
                if (admission is null)
                    return Failure(BrowserReadFailureKind.Unavailable, "Bench detail requires an admitted native connection.");
                operation = repository.AcquireOperation();
                binding = await bindings.ResolveAsync(ctx.Config, paths, ct);
                BrowserOriginGuard.EnsureExpected(binding, request.ExpectedBinding, request.ExpectedIdentity);
            }
            catch (InvalidOperationException ex) when (ex is not ConnectionBindingResolutionException { IsUnavailable: true })
            {
                throw new BrowserAuthorityChangedException(ex.Message);
            }
            origin = binding;
            if (capture is not null && !capture.Matches(binding)) throw Changed();

            // Never initialize first-use selectors: that can discover a remote identity.
            var bench = await resolver.TryResolveStoredAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (bench is null)
            {
                if (capture is not null || request.ExpectedBench is not null) throw Changed();
                await EnsureOriginAsync(binding, ct);
                return Failure(BrowserReadFailureKind.Unavailable, "No cached Bench is available. Open the Bench before requesting detail.");
            }
            if (request.ExpectedBench is not null && request.ExpectedBench != bench.Id.ToString(CultureInfo.InvariantCulture))
                throw Changed();
            if (capture is not null && !capture.Matches(bench)) throw Changed();
            capture ??= new BrowserCapture(bench.Id.ToString(CultureInfo.InvariantCulture), bench.Name, binding,
                BenchQueryRule.ContentsDigest(bench.Selectors));
            admittedCapture = capture;

            if (request.Sync)
            {
                await EnsureCaptureAsync(capture, ct);
                try
                {
                    // SyncLinksAsync pulls the selected root and edges only. Do not use
                    // SyncRootLinksAsync: it also materializes related target items.
                    await syncCoordinators.ReadOnly.SyncLinksAsync(request.Id, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await EnsureCaptureAsync(capture, ct);
                    return Failure(BrowserReadFailureKind.Unavailable, $"Sync failed or incomplete for #{request.Id}: {ex.Message}", admittedCapture);
                }
                await EnsureCaptureAsync(capture, ct);
            }

            var item = await repository.GetByIdAsync(request.Id, ct)
                ?? throw new InvalidOperationException(request.Sync
                    ? $"Work item #{request.Id} could not be loaded after sync; local pending changes may protect it."
                    : $"Work item #{request.Id} is not cached. Press S or run 'twig bench detail {request.Id} --sync' to pull this item explicitly; no refresh was performed.");
            // Copy each fact immediately after its read; repositories can hand out mutable
            // aggregate instances. No live aggregate escapes into retained rendering.
            item = Copy(item);
            var definitions = (await fieldDefinitions.GetAllAsync(ct)).ToArray();
            var entries = (await statusFields.ReadAsync(ct))?.ToArray();
            var children = (await repository.GetChildrenAsync(item.Id, ct)).Select(Copy).ToArray();
            var parent = item.ParentId.HasValue ? await repository.GetByIdAsync(item.ParentId.Value, ct) : null;
            if (parent is not null) parent = Copy(parent);
            var cachedLinks = (await links.GetLinksAsync(item.Id, ct)).ToArray();
            var verifiedAt = await links.GetLinksVerifiedAtAsync(item.Id, ct);
            var changes = (await pendingChanges.GetChangesAsync(item.Id, ct)).ToArray();
            var snapshot = new BrowserDetailSnapshot(item, parent, children, definitions, entries, cachedLinks,
                changes, processConfiguration.ComputeChildProgress(children), ctx.Config.Display.CacheStaleMinutes, theme.Snapshot());
            var presentation = await snapshot.RenderAsync(request.Width, request.Ansi);
            await EnsureCaptureAsync(capture, ct);
            ct.ThrowIfCancellationRequested();
            return new(new BrowserDetailObservation(capture, snapshot, verifiedAt, presentation), null, admittedCapture);
        }
        catch (OperationCanceledException)
        {
            return Failure(BrowserReadFailureKind.Canceled, "Detail read canceled.", admittedCapture);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return Failure(BrowserReadFailureKind.Canceled, "Detail read canceled.", admittedCapture);
            if (ex is BrowserAuthorityChangedException)
                return Failure(BrowserReadFailureKind.AuthorityInvalidated, ex.Message, admittedCapture);
            try
            {
                if (capture is not null) await EnsureCaptureAsync(capture, ct);
                else if (origin is not null) await EnsureOriginAsync(origin, ct);
            }
            catch (OperationCanceledException)
            {
                return Failure(BrowserReadFailureKind.Canceled, "Detail read canceled.", admittedCapture);
            }
            catch (BrowserAuthorityChangedException changed)
            {
                return Failure(BrowserReadFailureKind.AuthorityInvalidated, changed.Message, admittedCapture);
            }
            catch (Exception)
            {
                // An unavailable metadata/configuration read cannot establish that the
                // actor changed. It still cannot yield an observation or publish data.
                return Failure(BrowserReadFailureKind.Unavailable, ex.Message, admittedCapture);
            }
            return Failure(BrowserReadFailureKind.Unavailable, ex.Message, admittedCapture);
        }
        finally
        {
            try { operation?.Dispose(); }
            finally { admission?.Dispose(); }
        }
    }

    private async Task EnsureCaptureAsync(BrowserCapture capture, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureOriginAsync(capture.Origin, ct);
        var bench = await resolver.TryResolveStoredAsync(ct);
        if (bench is null || !capture.Matches(bench)) throw Changed();
        ct.ThrowIfCancellationRequested();
    }

    private async Task<ResolvedConnectionBinding> ResolveCurrentOriginAsync(CancellationToken ct)
    {
        // Use the same canonical split/global preference loader as native provider
        // admission. Worktree operation leases do not physically lock these files.
        var configuration = await TwigConfiguration.LoadSplitAsync(paths, ct);
        try { return await bindings.ResolveAsync(configuration, paths, ct); }
        catch (InvalidOperationException ex) when (ex is not ConnectionBindingResolutionException { IsUnavailable: true })
        {
            throw new BrowserAuthorityChangedException(ex.Message);
        }
    }

    private async Task EnsureOriginAsync(ResolvedConnectionBinding captured, CancellationToken ct)
    {
        var current = await ResolveCurrentOriginAsync(ct);
        if (!BrowserCapture.SameOrigin(captured, current)) throw Changed();
        ct.ThrowIfCancellationRequested();
    }

    private static WorkItem Copy(WorkItem source)
    {
        var copy = WorkItemCopier.Copy(source, preserveDirty: true);
        foreach (var note in source.PendingNotes) copy.AddPendingNote(note);
        return copy;
    }

    private static BrowserDetailResult Failure(BrowserReadFailureKind kind, string message, BrowserCapture? capture = null)
        => new(null, new(kind, message), capture);
    private static BrowserAuthorityChangedException Changed()
        => new("The browser Bench, connection, identity or storage generation changed. Quit and explicitly reconnect/restart before retrying.");
    private sealed class BrowserAuthorityChangedException(string message) : InvalidOperationException(message);
}
