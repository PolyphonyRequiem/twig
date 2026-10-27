namespace Twig.Infrastructure.Persistence;

internal sealed record PreservationTableDefinition(
    string Schema,
    string Table,
    string[] Columns);

internal sealed record PreservationRowSet(
    string ClassName,
    PreservationTableDefinition Table,
    string? Predicate = null);

internal static class PreservationInventory
{
    internal const string MirrorSchema = "main";
    internal const string DurableSchema = SqliteCacheStore.DurableSchema;

    private static readonly PreservationTableDefinition[] Tables =
    [
        new(MirrorSchema, "metadata", ["key", "value"]),
        new(MirrorSchema, "work_items",
            ["id", "type", "title", "state", "parent_id", "assigned_to", "assigned_to_unique_name",
             "iteration_path", "area_path", "revision", "is_seed", "seed_created_at", "staged_identity",
             "fields_json", "is_dirty", "last_synced_at"]),
        new(MirrorSchema, "process_types",
            ["type_name", "states_json", "default_child_type", "valid_child_types_json", "color_hex",
             "icon_id", "category_reference_names_json", "last_synced_at"]),
        new(MirrorSchema, "context", ["key", "value"]),
        new(MirrorSchema, "iteration_calendar", ["path", "start_date", "end_date"]),
        new(MirrorSchema, "field_definitions",
            ["ref_name", "display_name", "data_type", "is_read_only", "is_identity", "last_synced_at"]),
        new(MirrorSchema, "work_item_links", ["source_id", "target_id", "link_type"]),
        new(MirrorSchema, "work_item_link_verifications", ["source_id", "verified_at"]),
        new(MirrorSchema, "navigation_history", ["id", "work_item_id", "visited_at"]),

        new(DurableSchema, "pending_changes",
            ["id", "work_item_id", "change_type", "field_name", "old_value", "new_value", "created_at"]),
        new(DurableSchema, "publish_id_map", ["old_id", "new_id", "published_at", "staged_identity"]),
        new(DurableSchema, "seed_links", ["source_id", "target_id", "link_type", "created_at"]),
        new(DurableSchema, "staged_identities", ["staged_identity", "alias", "created_at", "retired_at"]),
        new(DurableSchema, "publish_intents",
            ["staged_identity", "title", "type_name", "recorded_at", "published_id", "completed_at"]),
        new(DurableSchema, "benches", ["id", "name", "is_default", "created_at"]),
        new(DurableSchema, "bench_selectors",
            ["id", "bench_id", "selector_kind", "selector_payload", "created_at"]),
        new(DurableSchema, "current_bench", ["id", "bench_id", "switched_at"]),
        new(DurableSchema, "proposal_journals",
            ["digest", "schema_version", "organization", "project", "source_path", "canonical_json",
             "state", "previewed_at", "confirmed_at", "completed_at", "error", "authorization_mode",
             "authorizer_identity", "rationale", "review_model_json", "authorized_at",
             "last_previewed_at", "origin_json", "draft_capture_json", "execution_admission_json"]),
        new(DurableSchema, "proposal_operations",
            ["digest", "ordinal", "op_id", "kind", "state", "request_json", "started_at",
             "applied_at", "verified_at", "result_json", "error", "warning"]),
        new(DurableSchema, "proposal_receipts",
            ["digest", "op_id", "receipt_id", "kind", "publish_identity",
             "publish_intent_recorded_at", "receipt_json", "created_at"]),
        new(DurableSchema, "local_work_items",
            ["local_id", "revision", "fields_json", "created_at", "updated_at", "deleted_at"]),
        new(DurableSchema, "external_identities",
            ["provider_kind", "resource_scope", "native_id", "local_id", "provider_token", "observed_at"]),
        new(DurableSchema, "local_relationships",
            ["source_local_id", "target_local_id", "kind", "revision", "created_at", "updated_at",
             "deleted_at", "storage", "publication"]),
        new(DurableSchema, "recoverable_work_item_drafts",
            ["checkout_root", "connection_id", "binding_id", "work_item_id", "generation", "draft_json",
             "updated_at"]),
        new(DurableSchema, "seed_work_items",
            ["local_id", "id", "type", "title", "state", "parent_id", "assigned_to", "assigned_to_unique_name",
             "iteration_path", "area_path", "revision", "is_seed", "seed_created_at", "staged_identity",
             "fields_json", "is_dirty", "last_synced_at"]),
        new(DurableSchema, "seed_local_identities", ["staged_identity", "local_id"]),
        new(DurableSchema, "workspace_context", ["key", "value"]),
        new(DurableSchema, "workspace_navigation_history", ["id", "work_item_id", "visited_at"]),
        new(DurableSchema, "preservation_migrations",
            ["intent_id", "slot", "source_generation", "source_digest", "state", "coverage_version", "backup_directory"]),
    ];

    private static readonly HashSet<string> ContextKeys = new(StringComparer.Ordinal)
    {
        "active_work_item_id",
        "area_tree_fetched_at",
        "area_tree_json",
        "last_refreshed_at",
        "nav_history_cursor",
        "workspace_mode",
    };

    private static readonly HashSet<string> MetadataKeys = new(StringComparer.Ordinal)
    {
        "binding_generation",
        "process_configuration_data",
        "schema_version",
    };

    internal static IReadOnlyList<PreservationTableDefinition> AllTables => Tables;

    internal static IReadOnlyList<PreservationRowSet> AllRowSets
    {
        get
        {
            var sets = new List<PreservationRowSet>(Tables.Length + 3);
            foreach (var table in Tables)
            {
                if (table.Schema == MirrorSchema && table.Table == "work_items")
                {
                    sets.Add(new PreservationRowSet("mirror.work-items.seeds", table, "is_seed = 1 OR id < 0"));
                    sets.Add(new PreservationRowSet(
                        "mirror.work-items.pending-edits", table, "is_seed = 0 AND id > 0 AND is_dirty = 1"));
                    sets.Add(new PreservationRowSet(
                        "mirror.work-items.remote-snapshot", table, "is_seed = 0 AND id > 0 AND is_dirty = 0"));
                    sets.Add(new PreservationRowSet(
                        "mirror.work-items.unclassified", table,
                        "NOT (is_seed = 1 OR id < 0 OR (is_seed = 0 AND id > 0 AND (is_dirty = 0 OR is_dirty = 1)))"));
                    continue;
                }

                sets.Add(new PreservationRowSet($"{table.Schema}.{table.Table}", table));
            }
            sets.Add(new PreservationRowSet("mirror.navigation-sequence",
                new(MirrorSchema, "sqlite_sequence", ["name", "seq"]), "name = 'navigation_history'"));
            sets.Add(new PreservationRowSet("pending.workspace-navigation-sequence",
                new(DurableSchema, "sqlite_sequence", ["name", "seq"]), "name = 'workspace_navigation_history'"));
            sets.Add(new PreservationRowSet("pending.preservation-migration-history",
                Tables.Single(table => table.Schema == DurableSchema && table.Table == "preservation_migrations"),
                "slot IS NULL"));

            return sets;
        }
    }

    internal static bool IsKnownContextKey(string key) => ContextKeys.Contains(key);

    internal static bool IsKnownMetadataKey(string key)
        => MetadataKeys.Contains(key) || key.StartsWith("draft-served-evidence:", StringComparison.Ordinal);
}
