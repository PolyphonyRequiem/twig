using System.Collections.Immutable;
using Twig.Core;
using Twig.Commands;
using Twig.Infrastructure.Services;
using Twig.Store.Sqlite;
using Step = Twig.Store.Sqlite.WorkspacePublication.Step;

namespace Twig.Server;

internal sealed partial class ReleasedBenchDisplay : IOwnerBenchPublication
{
    private readonly ViewSessions? sessions;
    private readonly Dictionary<Guid, ReleasedBenchStore.Dependency> pending;
    private readonly Dictionary<Guid, (long Version, string Hash)> heads;
    internal Func<string, long, Task>? StepBoundary { get; set; }

    private bool Dirty(DisplayPartition scope) => pending.Values.Any(d => d.Benches.Contains(scope.Bench));

    internal void RecoverWorkspace(string path)
    {
        // The caller supplies its pinned fixture location, never a path from a manifest.
        if (pending.Count == 0) return;
        var file = new FileInfo(path);
        if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || file.Directory!.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || file.Directory.Parent!.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Pending publication requires the original physical workspace.");
        foreach (var dep in pending.Values.ToArray())
        {
            var proof = ReleasedBenchStore.Proof(path, dep.Step);
            if (proof is { } applied) Finish(dep.Step, applied);
        }
        if (pending.Count != 0) throw new InvalidDataException("Unresolved publication evidence prevents new owner admission.");
    }

    public IDisposable TrackWorkspace(string root, NativeSourceStamp source, UserBenchStore definitions)
        => new WriterLease(store.KeepWal(), new Producer(this, root, source, definitions).Enter());

    private sealed class WriterLease(IDisposable wal, IDisposable scope) : IDisposable
    {
        public void Dispose()
        {
            scope.Dispose();
            wal.Dispose();
        }
    }

    private sealed class Producer(ReleasedBenchDisplay display, string root, NativeSourceStamp source,
        UserBenchStore definitions) : WorkspacePublication
    {
        private readonly string path = Path.GetFullPath(Path.Combine(root, ".twig", "core.db"));

        internal override async Task Begin(string database, Step step, HashSet<ItemKey>? footprint, CancellationToken ct)
        {
            if (!string.Equals(path, database, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || step.Instance != source.StoreInstance || step.Connection != Persistence.ConnectionText(source.Connection))
                throw new InvalidDataException("The writer does not own this publication origin.");
            var catalog = await definitions.ReadAsync(ct);
            if (!catalog.IsSuccess) throw new InvalidDataException("The affected Bench inventory is unavailable.");
            var selectors = catalog.Value.Benches.Where(b => b.Connections.Contains(source.Connection))
                .ToDictionary(b => b.Id, b => b.Selectors.Where(s => s.Connection == source.Connection).ToArray());
            var benches = selectors.Where(pair => footprint is null || pair.Value.Any(s =>
                s.Kind != SelectorKind.Pin || footprint.Contains(s.Item!))).Select(pair => pair.Key).ToHashSet();
            lock (display.publication)
            {
                DisplayPartition[] affected;
                lock (display.release)
                {
                    affected = display.partitionRequests.Keys.Where(p => p.Connection == source.Connection
                        && (p.Origin is null || p.Origin.StoreInstance == step.Instance)
                        && (!selectors.ContainsKey(p.Bench) || benches.Contains(p.Bench))).ToArray();
                    benches.UnionWith(affected.Select(p => p.Bench));
                }
                var dep = new ReleasedBenchStore.Dependency(step, benches);
                display.store.Start(dep);
                lock (display.release)
                {
                    display.pending.Add(step.Id, dep);
                    display.ChangeObservations(source.Connection);
                    foreach (var scope in display.partitionRequests.Keys.Where(p => benches.Contains(p.Bench)
                        && p.Connection == source.Connection && (p.Origin is null || p.Origin.StoreInstance == step.Instance)).ToArray())
                        display.Withhold(scope);
                }
            }
            if (display.StepBoundary is { } stop) await stop("manifest", step.After);
        }

        internal override async Task Committed(Step step)
        {
            if (display.StepBoundary is { } stop) await stop("domain", step.After);
            var changed = display.Finish(step, true);
            if (display.StepBoundary is { } after) await after("settled", step.After);
            if (changed.Count != 0) await display.ProjectWorkspace(root, source, definitions, changed);
        }
    }

    private HashSet<Guid> Finish(Step step, bool applied)
    {
        lock (publication)
        {
            store.Finish(step.Id, applied);
            lock (release)
            {
                var benches = pending[step.Id].Benches;
                pending.Remove(step.Id);
                if (applied && heads.GetValueOrDefault(step.Instance).Version < step.After)
                    heads[step.Instance] = (step.After, step.Hash);
                return benches;
            }
        }
    }

    internal async Task ProjectWorkspace(string root, NativeSourceStamp source, UserBenchStore definitions, HashSet<Guid>? affected = null)
    {
        if (sessions is null) return;
        var catalog = await definitions.ReadAsync();
        if (!catalog.IsSuccess) throw new InvalidDataException("Cannot read local projection inputs.");
        var owners = sessions.StandaloneDisplayOwners(catalog.Value.InstanceId);
        if (owners.Length == 0) return;
        var opened = await SqliteStore.OpenAsync(Path.Combine(root, ".twig", "core.db"), expectedInstance: source.StoreInstance);
        if (!opened.IsSuccess) throw new InvalidDataException("The local projection source changed.");
        var state = await opened.Value.ReadAsync(source.Connection, default);
        var rules = await definitions.ReadViewRulesAsync(root, source.Connection);
        if (!state.IsSuccess || !rules.IsSuccess) throw new InvalidDataException("Local intent or exclusion evidence is unavailable.");
        if (!rules.Value.Exclusions.IsEmpty) return; // No approximate evaluation of exclusions.
        var observation = new NativeSourceObservation(source, state.Value, clock.GetUtcNow(), null, null);
        var hash = WorkspacePublication.Hash(Persistence.Serialize(state.Value));
        foreach (var bench in catalog.Value.Benches.Where(b => b.Connections.Contains(source.Connection)
            && (affected is null || affected.Contains(b.Id))))
        {
            var selectors = bench.Selectors.Where(s => s.Connection == source.Connection).ToArray();
            if (selectors.Length == 0 || selectors.Any(s => s.Kind != SelectorKind.Pin)) continue;
            var pins = selectors.Select(s => s.Item!).ToHashSet();
            var admitted = state.Value.RetainedItems.Where(d => pins.Contains(d.Key)).Select(d => d.Key).ToImmutableArray();
            foreach (var owner in owners)
            {
                var commit = PrepareLocal(new(owner, catalog.Value.InstanceId, bench.Id, source.StoreInstance, source, source.Connection),
                    observation, admitted, hash, catalog.Value.Revision);
                await definitions.PublishIfCurrent(root, source.Connection, catalog.Value, rules.Value.Revision, commit, default);
            }
        }
    }
}
