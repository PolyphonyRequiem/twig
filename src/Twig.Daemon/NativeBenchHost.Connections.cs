using Bench.Infrastructure;
using Twig.Core;
using static Bench.Server.BenchHost;
using NativeBench = Twig.Core.Bench;

namespace Bench.Server;

internal sealed class NativeConnectionRequest(ConnectionRequestDto value, ViewSession session)
{
    internal ConnectionRequestDto Value { get; set; } = value;
    internal Guid Profile { get; } = session.ProfileGeneration;
    internal long Generation { get; } = session.Agent.Generation;
    internal NativePanelContext? Anchor { get; } = session.NativeContext;
    internal NativePanelContext? Population { get; } = session.PopulationContext;
    internal NativePanelContext[] Sources { get; } = Capture(session);
    internal CancellationTokenSource Lifetime { get; } = new(TimeSpan.FromTicks(Math.Max(0, (value.ExpiresAt - DateTimeOffset.UtcNow).Ticks)));

    private static NativePanelContext[] Capture(ViewSession session)
    {
        lock (session) return session.DisplayContexts.Values.ToArray();
    }
}

public static partial class NativeBenchHost
{
    private static NativeConnectionRequest PrepareConnectionRequest(ViewSession session, NativeBench bench,
        string provider, string? repository)
    {
        if (provider is not ("twig" or "github") || provider == "twig" && repository is not null
            || provider == "github" && (repository is null || !System.Text.RegularExpressions.Regex.IsMatch(repository,
                @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}\z",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant) || repository.Split('/')[1] is "." or ".."))
            throw new HttpFailure(400, "InvalidInput", "Choose Twig, or GitHub with an explicit owner/repository on github.com. Storage is managed privately and GitHub uses the signed-in CLI account.");
        repository = repository?.ToLowerInvariant();
        lock (session.Agent)
        lock (session)
        {
            if (session.Agent.ConnectionRequest is { } pending
                && pending.Value.State is "Requested" or "SettingUp" or "Ready" or "Failed"
                && !pending.Lifetime.IsCancellationRequested && pending.Value.ExpiresAt > DateTimeOffset.UtcNow)
            {
                if (pending.Value.BenchId == bench.Id && pending.Value.BenchRevision == bench.Revision
                    && pending.Value.Provider == provider && pending.Value.Repository == repository)
                    return pending;
                throw new HttpFailure(409, "ConnectionRequestPending", "Review or dismiss the existing connection request before requesting another.");
            }
            if (session.DisplayContexts.Count >= 32)
                throw new HttpFailure(422, "Unsupported", "This view has reached its 32-source agent admission limit.");
            var request = new NativeConnectionRequest(new(Guid.NewGuid(), bench.Id, bench.Revision, bench.Name,
                provider, repository, "Requested", new[] { session.Expires,
                    DateTimeOffset.UtcNow.AddMinutes(15) }.Min()), session);
            session.Agent.ConnectionRequest = request;
            return request;
        }
    }

    private static NativeConnectionRequest PendingConnectionRequest(ViewSession session, Guid id)
    {
        lock (session.Agent)
        lock (session)
        {
            var request = session.Agent.ConnectionRequest;
            if (request is null || request.Value.RequestId != id || request.Value.ExpiresAt <= DateTimeOffset.UtcNow
                || request.Lifetime.IsCancellationRequested || request.Profile != session.ProfileGeneration
                || request.Generation != session.Agent.Generation || !ReferenceEquals(request.Anchor, session.NativeContext)
                || !ReferenceEquals(request.Population, session.PopulationContext)
                || request.Sources.Length != session.DisplayContexts.Count
                || request.Sources.Any(source => !session.DisplayContexts.TryGetValue(source.Stamp.Connection, out var current)
                    || !ReferenceEquals(source, current)))
                throw new HttpFailure(409, "ConnectionRequestChanged", "The connection request expired or its owner, permissions or source scope changed. Nothing was replayed.");
            return request;
        }
    }

    private static async Task<NativeBench> CheckConnectionRequestAsync(NativePanel panel, ViewSession session,
        NativeConnectionRequest request, CancellationToken ct)
    {
        _ = PendingConnectionRequest(session, request.Value.RequestId);
        var library = Require(await panel.ListAsync(ct));
        if (library.InstanceId != session.CatalogId
            || session.CurrentBenchId != request.Value.BenchId)
            throw new HttpFailure(409, "ConnectionRequestChanged", "The native store or selected Bench changed. Review a new connection request.");
        var definition = library.Benches.SingleOrDefault(bench => bench.Id == request.Value.BenchId);
        if (definition is null || definition.Revision != request.Value.BenchRevision)
            throw new HttpFailure(409, "BenchChanged", "The Bench changed after this request. Dismiss it and review a new request; existing rules were not replaced.");
        _ = PendingConnectionRequest(session, request.Value.RequestId);
        return definition;
    }
}
