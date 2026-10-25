using System.Globalization;
using Twig.Commands;
using Twig.Domain.Interfaces;
using Twig.Domain.Services.Workspace;
using Twig.Infrastructure.Auth;
using Twig.Infrastructure.Config;
using Twig.Infrastructure.Persistence;

namespace Twig.Browser;

internal sealed record BrowserOperationResult<T>(T? Value, BrowserReadFailure? Failure, BrowserCapture? Capture = null);
internal sealed class BrowserReadException(BrowserReadFailureKind kind, string message) : InvalidOperationException(message)
{
    internal BrowserReadFailureKind Kind { get; } = kind;
}

/// <summary>Short native admission and storage leases, with fresh configuration and complete Bench fences.</summary>
internal sealed class BrowserOperationModule(CommandContext ctx, CurrentBenchResolver resolver,
    IWorkItemRepository repository, IAuthenticationProvider authentication, IConnectionBindingService bindings, TwigPaths paths)
{
    internal Task<BrowserOperationResult<T>> ExecuteAsync<T>(BrowserCapture? expected,
        Func<BrowserCapture, CancellationToken, Task<T>> action, CancellationToken ct = default,
        string? expectedBench = null, string? expectedBinding = null, string? expectedIdentity = null,
        bool initializeBench = false)
        => ExecuteCoreAsync(expected, action, null, ct, expectedBench, expectedBinding, expectedIdentity, initializeBench,
            expected?.ExplicitTarget);

    // Explicit, cache-only selection by saved name or id. It never falls back to, reads or mutates the shared
    // current-Bench pointer, and every revalidation re-resolves the captured identity rather than the pointer.
    internal Task<BrowserOperationResult<T>> ExecuteExplicitAsync<T>(string target,
        Func<BrowserCapture, CancellationToken, Task<T>> action, CancellationToken ct = default,
        string? expectedBench = null, string? expectedBinding = null, string? expectedIdentity = null)
        => ExecuteCoreAsync(null, action, null, ct, expectedBench, expectedBinding, expectedIdentity, false, target);

    // Native workflows supply the exact committed arrangement. Never adopt whatever
    // Bench happens to be current after a mutation or relabel a concurrent change.
    internal Task<BrowserOperationResult<T>> ChangeBenchAsync<T>(BrowserCapture expected,
        Func<BrowserCapture, CancellationToken, Task<T>> action, Func<T, BrowserCapture> committed,
        CancellationToken ct = default)
        => ExecuteCoreAsync(expected, action, committed, ct, null, null, null, false);

    private async Task<BrowserOperationResult<T>> ExecuteCoreAsync<T>(BrowserCapture? expected,
        Func<BrowserCapture, CancellationToken, Task<T>> action, Func<T, BrowserCapture>? committed, CancellationToken ct,
        string? expectedBench, string? expectedBinding, string? expectedIdentity, bool initializeBench,
        string? explicitTarget = null)
    {
        IDisposable? admission = null;
        IDisposable? operation = null;
        BrowserCapture? capture = expected;
        BrowserCapture? admitted = null;
        ResolvedConnectionBinding? origin = null;
        try
        {
            try
            {
                admission = await ConnectionOperationAdmission.AcquireAsync(authentication, ct);
                ct.ThrowIfCancellationRequested();
                if (admission is null) throw new BrowserReadException(BrowserReadFailureKind.Unavailable, "Browser requires an admitted native connection.");
                origin = (admission as IAdmittedConnectionOperation)?.GetOrigin(bindings, ctx.Config, paths);
                operation = origin is not null && repository is SqliteWorkItemRepository native
                    ? native.AcquireAdmittedOperation(origin) : repository.AcquireOperation();
                origin ??= await bindings.ResolveAsync(ctx.Config, paths, ct);
                BrowserOriginGuard.EnsureExpected(origin, expectedBinding, expectedIdentity);
            }
            catch (InvalidOperationException ex) when (ex is not BrowserReadException && ex is not ConnectionBindingResolutionException { IsUnavailable: true })
            {
                throw Changed(ex.Message);
            }
            if (capture is not null && !capture.Matches(origin)) throw Changed();
            var bench = explicitTarget is not null
                ? (await resolver.ResolveExplicitAsync(explicitTarget, expectedBench, origin.Identity.AccountName, ct)).Stored
                : initializeBench && capture is null && expectedBench is null
                    ? await resolver.ResolveStoredAsync(ct)
                    : await resolver.TryResolveStoredAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (bench is null)
            {
                if (capture is not null || expectedBench is not null) throw Changed();
                await EnsureOriginAsync(origin, ct);
                throw new BrowserReadException(BrowserReadFailureKind.Unavailable, "No cached Bench is available. Open the Bench before requesting detail.");
            }
            if (expectedBench is not null && expectedBench != bench.Id.ToString(CultureInfo.InvariantCulture)) throw Changed();
            if (capture is not null && !capture.Matches(bench)) throw Changed();
            capture ??= new(bench.Id.ToString(CultureInfo.InvariantCulture), bench.Name, origin, BenchQueryRule.ContentsDigest(bench.Selectors),
                explicitTarget is null ? null : bench.Id.ToString(CultureInfo.InvariantCulture));
            admitted = capture;
            if (committed is not null) await EnsureCurrentAsync(capture, ct);
            var value = await action(capture, ct);
            if (committed is not null)
            {
                var next = committed(value);
                if (!BrowserCapture.SameOrigin(capture.Origin, next.Origin)) throw Changed();
                capture = next;
            }
            await EnsureCurrentAsync(capture, ct);
            ct.ThrowIfCancellationRequested();
            return new(value, null, capture);
        }
        catch (OperationCanceledException) { return Refused<T>(BrowserReadFailureKind.Canceled, "Browser operation canceled.", admitted); }
        catch (Exception ex)
        {
            if (ex is BrowserReadException { Kind: BrowserReadFailureKind.AuthorityInvalidated })
                return Refused<T>(BrowserReadFailureKind.AuthorityInvalidated, ex.Message, admitted);
            if (ct.IsCancellationRequested) return Refused<T>(BrowserReadFailureKind.Canceled, "Browser operation canceled.", admitted);
            try
            {
                if (capture is not null) await EnsureCurrentAsync(capture, ct);
                else if (origin is not null) await EnsureOriginAsync(origin, ct);
            }
            catch (OperationCanceledException) { return Refused<T>(BrowserReadFailureKind.Canceled, "Browser operation canceled.", admitted); }
            catch (BrowserReadException changed) when (changed.Kind == BrowserReadFailureKind.AuthorityInvalidated)
            { return Refused<T>(changed.Kind, changed.Message, admitted); }
            catch (Exception) { return Refused<T>(BrowserReadFailureKind.Unavailable, ex.Message, admitted); }
            return Refused<T>(ex is BrowserReadException refusal ? refusal.Kind : BrowserReadFailureKind.Unavailable, ex.Message, admitted);
        }
        finally
        {
            try { operation?.Dispose(); }
            finally { admission?.Dispose(); }
        }
    }

    internal async Task EnsureCurrentAsync(BrowserCapture capture, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        await EnsureOriginAsync(capture.Origin, ct);
        var bench = capture.ExplicitTarget is null ? await resolver.TryResolveStoredAsync(ct) : await TryResolveExplicitAsync(capture, ct);
        if (bench is null || !capture.Matches(bench))
            throw capture.ExplicitTarget is null ? Changed()
                : Changed("bench-target-changed: the requested Bench changed or was removed during the read.");
        ct.ThrowIfCancellationRequested();
    }

    /// <summary>The effective (default-rule normalized) Bench for an explicit capture, pinned to its identity.</summary>
    internal async Task<Domain.Aggregates.Bench> ResolveExplicitEffectiveAsync(BrowserCapture capture, CancellationToken ct = default)
    {
        var (effective, stored) = await resolver.ResolveExplicitAsync(capture.ExplicitTarget!, capture.BenchId,
            capture.Origin.Identity.AccountName, ct);
        if (!capture.Matches(stored))
            throw Changed("bench-target-changed: the requested Bench changed or was removed during the read.");
        return effective;
    }

    private async Task<Domain.Aggregates.Bench?> TryResolveExplicitAsync(BrowserCapture capture, CancellationToken ct)
    {
        try
        {
            return (await resolver.ResolveExplicitAsync(capture.ExplicitTarget!, capture.BenchId,
                capture.Origin.Identity.AccountName, ct)).Stored;
        }
        catch (InvalidOperationException) { return null; }
    }

    private async Task EnsureOriginAsync(ResolvedConnectionBinding captured, CancellationToken ct)
    {
        var configuration = await TwigConfiguration.LoadSplitAsync(paths, ct);
        ResolvedConnectionBinding current;
        try { current = await bindings.ResolveAsync(configuration, paths, ct); }
        catch (InvalidOperationException ex) when (ex is not ConnectionBindingResolutionException { IsUnavailable: true })
        { throw Changed(ex.Message); }
        if (!BrowserCapture.SameOrigin(captured, current)) throw Changed();
        ct.ThrowIfCancellationRequested();
    }

    private static BrowserOperationResult<T> Refused<T>(BrowserReadFailureKind kind, string message, BrowserCapture? capture)
        => new(default, new(kind, message), capture);
    private static BrowserReadException Changed(string? message = null)
        => new(BrowserReadFailureKind.AuthorityInvalidated, message ?? "The browser Bench, connection, identity or storage generation changed. Explicitly reconnect before retrying.");
}
