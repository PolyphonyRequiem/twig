using System.Text.Json;
using System.Text.Json.Serialization;
using Twig.Domain.Aggregates;
using Twig.Domain.Projections;
using Twig.Domain.Services;
using Twig.Domain.Services.Plan;
using Twig.Domain.Services.Process;
using Twig.Domain.Services.Workspace;
using Twig.Domain.ValueObjects;
using Twig.Infrastructure.Serialization;
using Twig.Infrastructure.Plan;

namespace Twig.Infrastructure.Persistence;

/// <summary>One local observation, not a server metadata revision or mutation authority.</summary>
internal sealed record DraftCaptureEvidence(int Version, QualifiedWorkItemKey ItemKey,
    PlanOrigin Origin, string? StorageGeneration, string TypeName, int Revision,
    string ProcessFingerprint, string SnapshotFingerprint, FormLayout Layout, IReadOnlyList<ProcessRule>? Rules,
    [property: JsonConverter(typeof(DraftCaptureBenchJsonConverter))] Bench Bench, string? NormalizationAccount)
{
    public ExternalWorkItemIdentity? ExternalIdentity { get; init; }
}

internal sealed record DraftCaptureBench([property: JsonRequired] long Id, [property: JsonRequired] string Name,
    [property: JsonRequired] bool IsDefault, [property: JsonRequired] DraftCaptureSelector[] Selectors);
internal sealed record DraftCaptureSelector([property: JsonRequired] Twig.Domain.Enums.SelectorKind Kind,
    [property: JsonRequired] string Payload);

/// <summary>Receipt-local storage uses selector discriminators and opaque payloads, not query-only accessors.</summary>
internal sealed class DraftCaptureBenchJsonConverter : JsonConverter<Bench>
{
    public override Bench Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var stored = JsonSerializer.Deserialize(ref reader, TwigJsonContext.Default.DraftCaptureBench)
            ?? throw new JsonException("Native inspection Bench is null.");
        if (stored.Name is null || stored.Selectors is null ||
            stored.Selectors.Any(selector => selector is null || selector.Payload is null))
            throw new JsonException("Native inspection Bench identity or selectors are missing.");
        var selectors = stored.Selectors.Select(selector => new BenchSelector(selector.Kind, selector.Payload)).ToArray();
        foreach (var selector in selectors)
        {
            switch (selector.Kind)
            {
                case Twig.Domain.Enums.SelectorKind.Item:
                case Twig.Domain.Enums.SelectorKind.Subtree:
                    _ = selector.AsWorkItemId();
                    break;
                case Twig.Domain.Enums.SelectorKind.Query:
                    _ = BenchQueryRule.Parse(selector);
                    break;
                default:
                    throw new JsonException("Native inspection Bench selector kind is unknown.");
            }
        }
        return new Bench
        {
            Id = stored.Id, Name = stored.Name, IsDefault = stored.IsDefault,
            Selectors = selectors,
        };
    }

    public override void Write(Utf8JsonWriter writer, Bench value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, new DraftCaptureBench(value.Id, value.Name, value.IsDefault,
            value.Selectors.Select(selector => new DraftCaptureSelector(selector.Kind, selector.Payload)).ToArray()),
            TwigJsonContext.Default.DraftCaptureBench);
}

/// <summary>Purpose-specific mirror metadata; only explicit native inspection supplies evidence.</summary>
internal sealed class SqliteDraftCaptureEvidenceStore(SqliteCacheStore store)
{
    private static string Key(QualifiedWorkItemKey key)
        => "draft-served-evidence:" + PlanCanonicalizer.Canonicalize(
            JsonSerializer.SerializeToElement(key, TwigJsonContext.Default.QualifiedWorkItemKey)).Digest;

    internal string ProcessFingerprint()
    {
        using var operation = store.AcquireOperation();
        using var command = store.GetConnection().CreateCommand();
        command.Transaction = store.ActiveTransaction;
        command.CommandText = """
            SELECT type_name, states_json, default_child_type, valid_child_types_json,
                   color_hex, icon_id, category_reference_names_json FROM process_types ORDER BY type_name;
            SELECT key, value FROM metadata
              WHERE key IN ('process_configuration_data','schema_version','binding_generation') ORDER BY key;
            """;
        using var reader = command.ExecuteReader();
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            do
            {
                writer.WriteStartArray();
                while (reader.Read())
                {
                    writer.WriteStartArray();
                    for (var i = 0; i < reader.FieldCount; i++)
                        writer.WriteStringValue(reader.IsDBNull(i) ? null : reader.GetString(i));
                    writer.WriteEndArray();
                }
                writer.WriteEndArray();
            } while (reader.NextResult());
            writer.WriteEndArray();
        }
        using var json = JsonDocument.Parse(buffer.ToArray());
        return PlanCanonicalizer.Canonicalize(json.RootElement).Digest;
    }

    internal void Invalidate(QualifiedWorkItemKey key)
    {
        using var operation = store.AcquireOperation();
        using var command = store.GetConnection().CreateCommand();
        command.Transaction = store.ActiveTransaction;
        command.CommandText = "DELETE FROM metadata WHERE key=@key;";
        command.Parameters.AddWithValue("@key", Key(key));
        command.ExecuteNonQuery();
    }

    internal async Task RecordAsync(DraftCaptureEvidence evidence, WorkItemSnapshot expected, CancellationToken ct,
        bool joinTransaction = false)
    {
        using var operation = store.AcquireOperation();
        if (joinTransaction ? store.ActiveTransaction is null : store.ActiveTransaction is not null)
            throw new InvalidOperationException("Inspection evidence transaction ownership is invalid.");
        using var ownedTransaction = joinTransaction ? null : store.GetConnection().BeginTransaction(deferred: false);
        var transaction = store.ActiveTransaction ?? ownedTransaction!;
        if (ownedTransaction is not null) store.ActiveTransaction = ownedTransaction;
        try
        {
            var current = await new SqliteWorkItemRepository(store, new WorkItemMapper())
                .GetSnapshotByIdAsync(evidence.ItemKey.WorkItemId, ct).ConfigureAwait(false);
            if (current is null || SqliteDraftProposalCaptureStore.SnapshotJson(current) != SqliteDraftProposalCaptureStore.SnapshotJson(expected)
                || current.IsDirty || current.IsSeed || ProcessFingerprint() != evidence.ProcessFingerprint
                || current.TypeName != evidence.TypeName || current.Revision != evidence.Revision
                || SnapshotFingerprint(current) != evidence.SnapshotFingerprint
                || ReadGeneration() != evidence.StorageGeneration || !Qualified(evidence.ItemKey, evidence.Origin))
                throw new InvalidOperationException("metadata-stale: native inspection baseline, process inputs or origin changed.");
            await ValidateBenchAsync(evidence, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            using var command = store.GetConnection().CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO metadata(key,value) VALUES(@key,@value);";
            command.Parameters.AddWithValue("@key", Key(evidence.ItemKey));
            command.Parameters.AddWithValue("@value", JsonSerializer.Serialize(evidence, TwigJsonContext.Default.DraftCaptureEvidence));
            command.ExecuteNonQuery();
            ct.ThrowIfCancellationRequested();
            ownedTransaction?.Commit();
        }
        finally { if (ownedTransaction is not null) store.ActiveTransaction = null; }
    }

    internal DraftCaptureEvidence Read(QualifiedWorkItemKey key)
    {
        using var operation = store.AcquireOperation();
        using var command = store.GetConnection().CreateCommand();
        command.Transaction = store.ActiveTransaction;
        command.CommandText = "SELECT value FROM metadata WHERE key=@key;";
        command.Parameters.AddWithValue("@key", Key(key));
        if (command.ExecuteScalar() is not string json)
            throw new InvalidOperationException("metadata-unavailable: explicitly inspect this draft's native served form first.");
        var evidence = JsonSerializer.Deserialize(json, TwigJsonContext.Default.DraftCaptureEvidence)
            ?? throw new InvalidOperationException("metadata-unavailable: native served evidence is null.");
        if (evidence.Version != 1 || evidence.ItemKey != key || evidence.Layout is null || evidence.Bench is null)
            throw new InvalidOperationException("metadata-unavailable: native served evidence version or identity is invalid.");
        return evidence;
    }

    internal static string Fingerprint(DraftCaptureEvidence evidence)
        => PlanCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(evidence,
            TwigJsonContext.Default.DraftCaptureEvidence)).Digest;

    internal static string SnapshotFingerprint(WorkItemSnapshot snapshot)
        => PlanCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(snapshot,
            TwigJsonContext.Default.WorkItemSnapshot)).Digest;

    internal async Task ValidateAsync(RecoverableWorkItemDraft draft, WorkItemSnapshot snapshot,
        PlanOrigin origin, string fingerprint, CancellationToken ct)
    {
        var evidence = Read(draft.ItemKey);
        if (Fingerprint(evidence) != fingerprint || evidence.Origin != origin
            || !Qualified(draft.ItemKey, origin) || evidence.TypeName != snapshot.TypeName
            || evidence.Revision != snapshot.Revision || evidence.StorageGeneration != ReadGeneration()
            || evidence.SnapshotFingerprint != SnapshotFingerprint(snapshot)
            || evidence.ProcessFingerprint != ProcessFingerprint()
            || string.IsNullOrWhiteSpace(evidence.Layout.ProcessId)
            || string.IsNullOrWhiteSpace(evidence.Layout.WorkItemTypeReferenceName))
            throw new InvalidOperationException("metadata-stale: qualified native observation or local schema identity changed.");
        await ValidateBenchAsync(evidence, ct).ConfigureAwait(false);
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var control in WorkItemDetailProjector.CarriedControls(WorkItemDetailProjector.Project(evidence.Layout, snapshot)))
            if (WorkItemDetailProjector.TryGetRawValue(snapshot, control.Id, out var raw))
                values.TryAdd(control.Id, raw);
        var processStore = new SqliteProcessTypeStore(store);
        // Do not use the memoized runtime provider, and do not let its legacy JSON
        // degradation turn corrupt process input into positive eligibility.
        await EnsureProcessJsonAsync(ct).ConfigureAwait(false);
        var configuration = new DynamicProcessConfigProvider(processStore).GetConfiguration();
        var parsedType = WorkItemType.Parse(snapshot.TypeName);
        if (!parsedType.IsSuccess) throw new InvalidOperationException("metadata-unavailable: native work item type is invalid.");
        var type = parsedType.Value;
        if (!configuration.TypeConfigs.TryGetValue(type, out var typeConfiguration))
            throw new InvalidOperationException("metadata-unavailable: process configuration does not contain this native type.");
        var capability = new EditCapability(new PendingChangeStoreSink(new SqlitePendingChangeStore(store)),
            type, false, configuration);
        var edits = draft.Changes.Select(change => new FieldEdit(change.FieldRef, change.BaselineValue, change.ProposedValue)).ToArray();
        foreach (var edit in edits)
        {
            if (!values.TryGetValue(edit.FieldRef, out var value))
                throw new InvalidOperationException("field-not-carried: field is not carried by the served form.");
            if (value != edit.PriorValue)
                throw new InvalidOperationException("field-baseline-mismatch: exact original field value changed.");
            if (capability.Validate(edit) is Rejected refused)
                throw new InvalidOperationException("capability-refused: " + refused.Reason);
        }
        var state = edits.FirstOrDefault(edit => edit.FieldRef.Equals("System.State", StringComparison.OrdinalIgnoreCase));
        if (state is not null)
        {
            if (state.ProposedValue is null) throw new InvalidOperationException("rules-refused: System.State cannot be null.");
            if (state.PriorValue is null || !typeConfiguration.States.Contains(state.PriorValue, StringComparer.OrdinalIgnoreCase)
                || !typeConfiguration.States.Contains(state.ProposedValue, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("state-unknown: current or proposed state is absent from the observed native type catalog.");
            var move = new StateMove(state.PriorValue ?? string.Empty, state.ProposedValue,
                edits.Where(edit => !ReferenceEquals(edit, state)).ToArray());
            if (capability.Validate(move) is Rejected refused)
                throw new InvalidOperationException("rules-refused: " + refused.Reason);
        }
        if (evidence.Rules is null) throw new InvalidOperationException("metadata-unavailable: native process rules were not observed.");
        var batch = new BatchOperation
        {
            Id = "local-observation", WorkItemId = snapshot.Id, ExpectedRevision = snapshot.Revision,
            Fields = edits.ToDictionary(edit => edit.FieldRef, edit => edit.ProposedValue, StringComparer.OrdinalIgnoreCase),
        };
        var gate = PlanProcessRuleGate.EvaluateObserved(batch, new WorkItemMapper().Map(snapshot), evidence.Rules);
        if (!gate.IsOk) throw new InvalidOperationException("rules-refused: " + gate.Message);
    }

    private Task EnsureProcessJsonAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var command = store.GetConnection().CreateCommand();
        command.Transaction = store.ActiveTransaction;
        command.CommandText = "SELECT states_json, valid_child_types_json, category_reference_names_json FROM process_types;";
        var count = 0;
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                count++;
                var states = JsonSerializer.Deserialize(reader.GetString(0), TwigJsonContext.Default.ListStateEntry);
                if (states is null || states.Count == 0) throw new InvalidOperationException("metadata-unavailable: native type states are empty.");
                for (var i = 1; i < 3; i++)
                    if (!reader.IsDBNull(i) && JsonSerializer.Deserialize(reader.GetString(i), TwigJsonContext.Default.ListString) is null)
                        throw new InvalidOperationException("metadata-unavailable: native process list is null.");
            }
        if (count == 0) throw new InvalidOperationException("metadata-unavailable: native process type catalog is empty.");
        command.CommandText = "SELECT value FROM metadata WHERE key='process_configuration_data';";
        if (command.ExecuteScalar() is string json && JsonSerializer.Deserialize(json, TwigJsonContext.Default.ProcessConfigurationData) is null)
            throw new InvalidOperationException("metadata-unavailable: native process configuration is null.");
        return Task.CompletedTask;
    }

    internal async Task ValidateBenchAsync(DraftCaptureEvidence evidence, CancellationToken ct)
    {
        var current = (await new SqliteBenchRepository(store).GetAllAsync(ct).ConfigureAwait(false))
            .SingleOrDefault(bench => bench.Id == evidence.Bench.Id);
        if (current is null || current.Name != evidence.Bench.Name || current.IsDefault != evidence.Bench.IsDefault
            || BenchQueryRule.ContentsDigest(current.Selectors) != BenchQueryRule.ContentsDigest(evidence.Bench.Selectors))
            throw new InvalidOperationException("bench-stale: inspected native Bench identity or selectors changed.");
        var effective = current;
        if (RequiresNormalization(current))
        {
            if (string.IsNullOrWhiteSpace(evidence.NormalizationAccount))
                throw new InvalidOperationException("bench-principal-unavailable: observed default self-filter identity is missing.");
            effective = CurrentBenchResolver.NormalizeDefaultSelfSprint(current,
                [BenchSelector.ForCurrentSprintCanonical(evidence.NormalizationAccount)]);
        }
        var membership = await new BenchEvaluator(new SqliteWorkItemRepository(store, new WorkItemMapper()),
            new SqliteIterationCalendar(store), new SqlitePendingChangeStore(store)).EvaluateAsync(effective, ct: ct).ConfigureAwait(false);
        if (!membership.AllIds.Contains(evidence.ItemKey.WorkItemId))
            throw new InvalidOperationException("bench-item-not-member: item is not in the inspected native Bench's local membership.");
    }

    internal static bool RequiresNormalization(Bench bench)
        => bench.IsDefault && bench.Selectors.Any(selector => selector.Kind == Twig.Domain.Enums.SelectorKind.Query
            && selector.QueryRule is BenchSelector.CurrentSprintRule or BenchQueryRule.Name);

    private string? ReadGeneration()
    {
        using var command = store.GetConnection().CreateCommand();
        command.Transaction = store.ActiveTransaction;
        command.CommandText = "SELECT value FROM metadata WHERE key='binding_generation';";
        return command.ExecuteScalar() as string;
    }

    private static bool Qualified(QualifiedWorkItemKey key, PlanOrigin origin)
        => key.WorkItemId > 0 && key.CheckoutRoot == origin.WorktreeRoot
            && key.ConnectionId == origin.ConnectionRef && key.BindingId == origin.BindingId;
}
