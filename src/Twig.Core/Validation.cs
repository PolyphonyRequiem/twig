using System.Collections.Immutable;

namespace Twig.Core;

public static partial class Core
{
    public static ImmutableArray<Error> Validate(Proposal proposal, string confirmation, ProcessSchema schema,
        StoreSnapshot store, ProviderCapabilities capabilities)
    {
        var errors = ImmutableArray.CreateBuilder<Error>();
        if (!string.Equals(confirmation, proposal.Digest, StringComparison.Ordinal))
            errors.Add(new(ErrorCode.DigestMismatch, "Confirmation must equal the exact canonical proposal digest."));
        if (proposal.Connection != schema.Connection || proposal.SchemaFingerprint != schema.Fingerprint)
            errors.Add(new(ErrorCode.SchemaChanged, "The bound connection or discovered schema changed; preview a new proposal."));
        if (proposal.ExpectedStoreVersion != store.Version)
            errors.Add(new(ErrorCode.Conflict, "Local state changed after this proposal was constructed."));
        errors.AddRange(SchemaErrors(schema));
        foreach (var op in proposal.Operations)
            ValidateOperation(op, proposal, schema, store, capabilities, errors);
        return errors.ToImmutable();
    }

    public static ImmutableArray<Error> ValidateEffect(Proposal proposal, int operation, ProcessSchema schema,
        StoreSnapshot store, ProviderCapabilities capabilities)
    {
        var errors = ImmutableArray.CreateBuilder<Error>();
        if (operation < 0 || operation >= proposal.Operations.Length)
            return [new(ErrorCode.InvalidInput, "Operation ordinal is outside the retained proposal.")];
        if (proposal.Connection != schema.Connection || proposal.SchemaFingerprint != schema.Fingerprint)
            errors.Add(new(ErrorCode.SchemaChanged, "The apply-time schema differs from the confirmed intent."));
        errors.AddRange(SchemaErrors(schema));
        if (!store.Proposals.Any(p => p.Digest == proposal.Digest)
            || !store.Journal.Any(j => j.Digest == proposal.Digest && j.Operation == operation && j.Status == OpStatus.Planned)
            || store.Journal.Any(j => j.Digest == proposal.Digest && j.Operation < operation && j.Status != OpStatus.Verified))
            errors.Add(new(ErrorCode.Conflict, "The retained operation is not ready for its first attempt."));
        ValidateOperation(proposal.Operations[operation], proposal, schema, store, capabilities, errors);
        return errors.ToImmutable();
    }

    private static IEnumerable<Error> SchemaErrors(ProcessSchema schema)
    {
        if (!schema.Gaps.IsEmpty)
            yield return new(ErrorCode.Unavailable, "Schema discovery is incomplete.") { Evidence = schema.Gaps };
        if (schema.Types.IsEmpty || schema.Relations.Any(r => !Enum.IsDefined(r.Shape)))
            yield return new(ErrorCode.Unavailable, "Type or relation semantics are unavailable.");
        foreach (var type in schema.Types)
        {
            if (!type.Fields.Any(f => f.Reference == type.StateField)
                || type.States.IsEmpty || type.Fields.Any(f => !Enum.IsDefined(f.Kind) || f.Kind == FieldKind.Null)
                || type.States.Any(s => !Enum.IsDefined(s.Category) || s.Category == StateCategory.Unknown)
                || type.Transitions.Distinct().Count() != type.Transitions.Length
                || type.Transitions.Any(t => !type.States.Any(s => s.Name == t.From) || !type.States.Any(s => s.Name == t.To)))
                yield return new(ErrorCode.Unavailable, $"Type '{type.Reference}' has incomplete or inconsistent state/field metadata.");
            foreach (var rule in type.Rules.Where(r => !r.Disabled))
            {
                var target = type.Fields.FirstOrDefault(f => f.Reference == rule.Field);
                if (target is null || !Enum.IsDefined(rule.Action) || rule.Action == RuleAction.Unknown
                    || rule.Action == RuleAction.Supplied && rule.Values.Length > 1
                    || rule.Action == RuleAction.Allowed && rule.Values.IsEmpty
                    || rule.Values.Any(v => v.Kind != FieldKind.Null && v.Kind != target?.Kind)
                    || rule.Conditions.Any(c => !type.Fields.Any(f => f.Reference == c.Field)
                        || !Enum.IsDefined(c.Kind) || c.Kind == ConditionKind.Unknown
                        || c.Kind is ConditionKind.Equal or ConditionKind.NotEqual or ConditionKind.Was && c.Value is null))
                    yield return new(ErrorCode.Unavailable, $"Type '{type.Reference}' contains an unsupported enabled field rule.");
            }
        }
    }

    private static void ValidateOperation(Op op, Proposal proposal, ProcessSchema schema, StoreSnapshot store,
        ProviderCapabilities capabilities, ImmutableArray<Error>.Builder errors)
    {
        void Fail(ErrorCode code, string message) => errors.Add(new(code, $"{op.Id}: {message}"));
        if (!Enum.IsDefined(op.Kind) || !Enum.IsDefined(op.Concurrency) || !Enum.IsDefined(op.Origin))
        {
            Fail(ErrorCode.Unsupported, "Unknown operation, concurrency mode or relationship origin.");
            return;
        }
        var link = op.Kind is OpKind.Relate or OpKind.Unrelate;
        var local = link && op.Origin == RelationshipOrigin.Local;
        if (!local && op.ExpectedLocalVersion is not null)
            Fail(ErrorCode.InvalidInput, "Only a local relationship can carry a local edge precondition.");
        if ((!link && (op.Other is not null || op.Relation is not null || op.Origin != RelationshipOrigin.Provider))
            || (op.Kind is not (OpKind.Batch or OpKind.Create) && op.Fields.Count != 0)
            || (op.Kind is not (OpKind.Create or OpKind.Batch) && op.Type is not null)
            || (op.Kind != OpKind.Comment && op.Body is not null))
            Fail(ErrorCode.InvalidInput, "Operation contains fields belonging to a different effect.");
        if (!local && !capabilities.Operations.Contains(op.Kind))
            Fail(ErrorCode.Unsupported, "Provider does not implement this operation.");
        if (op.Kind != OpKind.Create && !local && !op.Item.BelongsTo(proposal.Connection))
            Fail(ErrorCode.InvalidInput, "Mutation identity is outside the bound provider resource scope.");
        if (op.Kind == OpKind.Create)
        {
            if (!op.Item.IsLocal || op.ExpectedRevision is not null || string.IsNullOrWhiteSpace(op.Type))
                Fail(ErrorCode.InvalidInput, "Creation requires a durable local UUID, type and no synthetic provider revision.");
            if (op.Concurrency != ConcurrencyMode.ObservedNonAtomic)
                Fail(ErrorCode.Unsupported, "Creation has no provider revision CAS; that limitation must be explicit.");
            var draft = store.Drafts.FirstOrDefault(d => d.Key == op.Item);
            if (draft is null || draft.Type != op.Type || !draft.Changes.Equals(op.Fields))
                Fail(ErrorCode.Conflict, "Creation must bind the exact retained seed type and fields.");
            if (store.Mappings.Any(m => m.Local == op.Item) || store.Journal.Any(j =>
                j.Status != OpStatus.Planned && store.Proposals.Any(p => p.Digest == j.Digest
                    && j.Operation < p.Operations.Length && p.Operations[j.Operation].Kind == OpKind.Create
                    && p.Operations[j.Operation].Item == op.Item)))
                Fail(ErrorCode.Conflict, "This creation identity has already been attempted; reconcile without replay, even under a new digest.");
        }
        else if (!local)
        {
            if (op.ExpectedRevision is null || op.ExpectedRevision.Item != op.Item)
                Fail(ErrorCode.Conflict, "A provider mutation requires its exact target-bound opaque revision.");
            var observed = store.Items.FirstOrDefault(i => i.Key == op.Item);
            if (observed?.Revision is null || observed.Revision != op.ExpectedRevision)
                Fail(ErrorCode.Conflict, "The expected revision is not the observed item revision.");
            if (op.Concurrency == ConcurrencyMode.ExactRevision && !capabilities.ExactRevisions)
                Fail(ErrorCode.Unsupported, "Provider cannot enforce exact revisions; observed non-atomic intent must be explicit.");
        }
        if (local && op.ExpectedRevision is not null)
            Fail(ErrorCode.InvalidInput, "A local relationship uses the store CAS, not a provider revision.");
        if (link)
        {
            if (op.Other is null || string.IsNullOrWhiteSpace(op.Relation))
                Fail(ErrorCode.InvalidInput, "A relationship effect needs both endpoints and a discovered relation kind.");
            else
            {
                if (!schema.Relations.Any(r => r.Reference == op.Relation))
                    Fail(ErrorCode.Unsupported, "Unknown relationship kind.");
                if (!(local ? capabilities.LocalRelations : capabilities.NativeRelations).Contains(op.Relation))
                    Fail(ErrorCode.Unsupported, "Relationship transport/provenance was not explicitly declared.");
                if (!local && !op.Other.BelongsTo(proposal.Connection))
                    Fail(ErrorCode.Unsupported, "Native cross-scope relationships require a provider-specific capability, not implicit emulation.");
                if (local && new[] { op.Item, op.Other }.Any(k => !store.Items.Any(i => i.Key == k)
                    && !store.Drafts.Any(d => d.Key == k)))
                    Fail(ErrorCode.Unavailable, "Local relationship endpoint evidence is missing.");
                if (local)
                {
                    var symmetric = schema.Relations.Any(r => r.Reference == op.Relation && !r.Directed);
                    var edge = store.Relationships.FirstOrDefault(r => r.Origin == RelationshipOrigin.Local
                        && r.Kind == op.Relation && (r.From == op.Item && r.To == op.Other
                            || symmetric && r.To == op.Item && r.From == op.Other));
                    if (op.ExpectedLocalVersion != edge?.LocalVersion || op.ExpectedLocalVersion is < 1)
                        Fail(ErrorCode.Conflict, "The local edge's exact CAS version changed, including its tombstone.");
                    if (op.Kind == OpKind.Unrelate && (edge is null || edge.Deleted))
                        Fail(ErrorCode.Conflict, "The local relationship to remove is not present.");
                    if (op.Kind == OpKind.Relate && edge is { Deleted: false })
                        Fail(ErrorCode.Conflict, "The local relationship already exists.");
                }
            }
        }
        if (op.Kind == OpKind.Comment && (op.Body is null || op.Body.Kind is not (FieldKind.Text or FieldKind.Html or FieldKind.Markdown) || op.Body.IsEmpty))
            Fail(ErrorCode.InvalidInput, "Comment must retain an explicit nonempty body and format.");
        if (op.Kind is OpKind.Batch or OpKind.Create)
        {
            if (op.Fields.Count == 0) Fail(ErrorCode.InvalidInput, "A field effect cannot be empty.");
            if (op.Kind == OpKind.Batch && op.Fields.Count + (op.Type is null ? 0 : 1) > 1 && !capabilities.AtomicFieldBatches)
                Fail(ErrorCode.Unsupported, "Provider cannot apply this field batch atomically; expand independent effects before digesting.");
            var source = store.Items.FirstOrDefault(i => i.Key == op.Item);
            var type = schema.Types.FirstOrDefault(t => t.Reference == (op.Type ?? source?.Type));
            if (type is null) Fail(ErrorCode.Unavailable, "The item's discovered type is unavailable.");
            else ValidateFields(op, source, type, Fail);
        }
    }

    private static void ValidateFields(Op op, WorkItem? source, ItemType type, Action<ErrorCode, string> fail)
    {
        var old = source?.Fields ?? new FieldValues(type.Fields.Select(f =>
            KeyValuePair.Create(f.Reference, FieldValue.Null)));
        var effective = old.Overlay(op.Fields);
        if (type.Hidden) fail(ErrorCode.Unsupported, "The discovered type is disabled/hidden for authoring.");
        if (!type.Fields.Any(f => f.Reference == type.StateField)
            || type.States.IsEmpty || type.States.Any(s => !Enum.IsDefined(s.Category) || s.Category == StateCategory.Unknown)
            || type.Transitions.Any(t => !type.States.Any(s => s.Name == t.From) || !type.States.Any(s => s.Name == t.To)))
            fail(ErrorCode.Unavailable, "State schema is incomplete or inconsistent.");
        foreach (var (name, value) in op.Fields)
        {
            var definition = type.Fields.FirstOrDefault(f => f.Reference == name);
            if (definition is null) fail(ErrorCode.Unsupported, $"Field '{name}' is not declared for this type.");
            else if (definition.ReadOnly) fail(ErrorCode.InvalidInput, $"Field '{name}' is read-only.");
            else if (!Enum.IsDefined(definition.Kind) || value.Kind == FieldKind.Raw
                || value.Kind != FieldKind.Null && value.Kind != definition.Kind)
                fail(ErrorCode.InvalidInput, $"Field '{name}' has an incompatible or opaque write value.");
        }
        effective.TryGetValue(type.StateField, out var state);
        old.TryGetValue(type.StateField, out var previous);
        var changedState = !Equals(previous, state);
        var target = type.States.FirstOrDefault(s => s.Name == state?.Text);
        if (target is null) fail(ErrorCode.InvalidInput, "Effective state must be explicitly present and discovered.");
        if (source is not null && changedState && !type.Transitions.Any(t => t.From == previous?.Text && t.To == state?.Text))
            fail(ErrorCode.InvalidInput, "The requested state transition is not discovered as allowed.");
        var active = new List<FieldRule>();
        foreach (var rule in type.Rules.Where(r => !r.Disabled))
        {
            if (!Enum.IsDefined(rule.Action) || rule.Action == RuleAction.Unknown
                || !type.Fields.Any(f => f.Reference == rule.Field)
                || rule.Conditions.Any(c => !type.Fields.Any(f => f.Reference == c.Field)
                    || !Enum.IsDefined(c.Kind) || c.Kind == ConditionKind.Unknown
                    || c.Kind is ConditionKind.Equal or ConditionKind.NotEqual or ConditionKind.Was && c.Value is null))
            {
                fail(ErrorCode.Unavailable, "An enabled schema rule cannot be understood.");
                continue;
            }
            var applies = MatchesConditions(rule.Conditions, old, effective);
            if (!applies.IsSuccess) fail(applies.Error.Code, applies.Error.Message);
            else if (applies.Value) active.Add(rule);
        }
        foreach (var rule in active)
        {
            effective.TryGetValue(rule.Field, out var value);
            var supplied = active.Any(r => r.Field == rule.Field && r.Action == RuleAction.Supplied
                && (r.Values.IsEmpty || r.Values.Any(v => !v.IsEmpty)));
            switch (rule.Action)
            {
                case RuleAction.Required:
                    if (!supplied && (value is null || value.IsEmpty))
                        fail(ErrorCode.InvalidInput, $"Required field '{rule.Field}' has no effective value.");
                    if (!supplied && changedState && target?.Category == StateCategory.Completed && !op.Fields.ContainsKey(rule.Field))
                        fail(ErrorCode.InvalidInput, $"Close-gate field '{rule.Field}' must ride in the same batch as the transition.");
                    break;
                case RuleAction.ReadOnly:
                    if (op.Fields.ContainsKey(rule.Field)) fail(ErrorCode.InvalidInput, $"Rule makes '{rule.Field}' read-only.");
                    break;
                case RuleAction.Allowed:
                    if (rule.Values.IsEmpty) fail(ErrorCode.Unavailable, $"Allowed values for '{rule.Field}' are unavailable.");
                    else if (value is null) fail(ErrorCode.Unavailable, $"Value of '{rule.Field}' was not observed.");
                    else if (!rule.Values.Contains(value)) fail(ErrorCode.InvalidInput, $"Field '{rule.Field}' is outside its allowed values.");
                    break;
                case RuleAction.Supplied:
                    if (rule.Values.Length > 1) fail(ErrorCode.Unavailable, $"Supplier for '{rule.Field}' is ambiguous.");
                    break;
            }
        }
        if (active.Where(r => r.Action == RuleAction.Supplied && !r.Values.IsEmpty)
            .GroupBy(r => r.Field, StringComparer.Ordinal).Any(g => g.SelectMany(r => r.Values).Distinct().Count() > 1))
            fail(ErrorCode.Unavailable, "Enabled suppliers disagree about the effective field value.");
    }

    private static Result<bool> MatchesConditions(IEnumerable<RuleCondition> conditions, FieldValues before, FieldValues after)
    {
        var unknown = false;
        foreach (var condition in conditions)
        {
            var hadBefore = before.TryGetValue(condition.Field, out var old);
            var hasAfter = after.TryGetValue(condition.Field, out var value);
            bool? matches = condition.Kind switch
            {
                ConditionKind.Equal when hasAfter && condition.Value is not null => Equals(value, condition.Value),
                ConditionKind.NotEqual when hasAfter && condition.Value is not null => !Equals(value, condition.Value),
                ConditionKind.Was when hadBefore && condition.Value is not null => Equals(old, condition.Value),
                ConditionKind.Changed when hadBefore && hasAfter => !Equals(old, value),
                ConditionKind.NotChanged when hadBefore && hasAfter => Equals(old, value),
                _ => null,
            };
            if (matches == false) return Result<bool>.Ok(false);
            if (matches is null) unknown = true;
        }
        return unknown ? Result<bool>.Fail(ErrorCode.Unavailable, "Rule predicates or their pre/post observations are unknown.")
            : Result<bool>.Ok(true);
    }

    public static Result<MergeResult> Merge(Draft draft, WorkItem remote)
    {
        if (draft.Key != remote.Key || draft.Baseline is null || remote.Revision is null)
            return Result<MergeResult>.Fail(ErrorCode.InvalidInput, "Merge needs the same remote identity and its retained base.");
        var merged = remote.Fields;
        var conflicts = ImmutableArray.CreateBuilder<string>();
        foreach (var (field, intent) in draft.Changes)
        {
            var hadBase = draft.Baseline.Fields.TryGetValue(field, out var baseline);
            var observed = remote.Fields.TryGetValue(field, out var current);
            if (Equals(intent, current)) continue;
            if (!hadBase || !observed) { conflicts.Add(field); continue; }
            if (Equals(intent, baseline)) continue;
            if (Equals(current, baseline)) merged = merged.Set(field, intent);
            else conflicts.Add(field);
        }
        return Result<MergeResult>.Ok(new(merged, conflicts.ToImmutable()));
    }

    public static ImmutableArray<Error> ValidateCommit(StoreSnapshot before, StoreSnapshot next)
    {
        var errors = ImmutableArray.CreateBuilder<Error>();
        if (next.Version != before.Version + 1)
            errors.Add(new(ErrorCode.Conflict, "A store commit must advance the local CAS version exactly once."));
        if (next.Mappings.Select(m => m.Local).Distinct().Count() != next.Mappings.Length
            || next.Mappings.Select(m => m.Remote).Distinct().Count() != next.Mappings.Length
            || before.Mappings.Any(m => !next.Mappings.Contains(m)))
            errors.Add(new(ErrorCode.Conflict, "Publish mappings are immutable, permanent and one-to-one."));
        if (before.Proposals.Any(p => !next.Proposals.Any(n => n.Digest == p.Digest)))
            errors.Add(new(ErrorCode.Conflict, "Retained proposal intent cannot disappear during a store commit."));
        var localEdges = next.Relationships.Where(r => r.Origin == RelationshipOrigin.Local).ToArray();
        if (localEdges.Select(r => (r.From, r.To, r.Kind)).Distinct().Count() != localEdges.Length)
            errors.Add(new(ErrorCode.Conflict, "Duplicate local relationship identities."));
        foreach (var edge in localEdges)
        {
            var prior = before.Relationships.FirstOrDefault(r => r.Origin == RelationshipOrigin.Local
                && r.From == edge.From && r.To == edge.To && r.Kind == edge.Kind);
            if (prior is null ? edge.LocalVersion != 1
                : edge != prior && edge.LocalVersion != prior.LocalVersion + 1)
                errors.Add(new(ErrorCode.Conflict, "Local relationship changes require a monotonic local CAS version."));
        }
        foreach (var edge in before.Relationships.Where(r => r.Origin == RelationshipOrigin.Local))
            if (!localEdges.Any(r => r.From == edge.From && r.To == edge.To && r.Kind == edge.Kind))
                errors.Add(new(ErrorCode.Conflict, "Local relationships must be retained as live edges or tombstones, never lost in mirror refresh."));
        if (next.Journal.Select(j => (j.Digest, j.Operation)).Distinct().Count() != next.Journal.Length)
            errors.Add(new(ErrorCode.Conflict, "Duplicate operation journal rows."));
        foreach (var prior in before.Journal)
        {
            var current = next.Journal.FirstOrDefault(j => j.Digest == prior.Digest && j.Operation == prior.Operation);
            if (current is null || current.Receipts.Length < prior.Receipts.Length
                || !current.Receipts.Take(prior.Receipts.Length).Zip(prior.Receipts, SameReceipt).All(same => same)
                || prior.Attempt != Guid.Empty && current.Attempt != prior.Attempt
                || prior.Status != current.Status && (prior.Status switch
                {
                    OpStatus.Planned => current.Status != OpStatus.Applying,
                    OpStatus.Applying => current.Receipts.Length != prior.Receipts.Length + 1,
                    OpStatus.Applied => current.Receipts.Length != prior.Receipts.Length + 1,
                    _ => true,
                }))
                errors.Add(new(ErrorCode.Conflict, "Journal evidence cannot be erased, rewritten or replayed."));
        }
        foreach (var proposal in next.Proposals)
        {
            var rows = next.Journal.Where(j => j.Digest == proposal.Digest).ToArray();
            if (rows.Length != proposal.Operations.Length
                || rows.Any(j => j.Operation < 0 || j.Operation >= proposal.Operations.Length))
                errors.Add(new(ErrorCode.Conflict, "Proposal import must atomically retain exactly one journal row per operation."));
        }
        foreach (var row in next.Journal)
        {
            if (!before.Journal.Any(j => j.Digest == row.Digest && j.Operation == row.Operation) && row.Status != OpStatus.Planned)
                errors.Add(new(ErrorCode.Conflict, "An operation must be durably imported as Planned before its attempt begins."));
            var proposal = next.Proposals.FirstOrDefault(p => p.Digest == row.Digest);
            if (proposal is null || row.Operation >= proposal.Operations.Length)
            {
                errors.Add(new(ErrorCode.Conflict, "A journal row must belong to retained intent."));
                continue;
            }
            if (row.Status != OpStatus.Planned && next.Journal.Any(j => j.Digest == row.Digest
                && j.Operation < row.Operation && j.Status != OpStatus.Verified))
                errors.Add(new(ErrorCode.Conflict, "Later operations cannot start before earlier operations verify."));
            var op = proposal.Operations[row.Operation];
            if (op.Kind == OpKind.Create && row.Status is OpStatus.Applied or OpStatus.Verified
                && !next.Mappings.Any(m => m.Local == op.Item && m.ProposalDigest == row.Digest
                    && m.Operation == row.Operation && row.Receipts.Any(r => r.PublishedKey == m.Remote)))
                errors.Add(new(ErrorCode.Conflict, "Creation acknowledgement, publish mapping and journal must commit together."));
        }
        foreach (var mapping in next.Mappings)
            if (!next.Journal.Any(j => j.Digest == mapping.ProposalDigest && j.Operation == mapping.Operation
                && j.Receipts.Any(r => r.Status == OpStatus.Applied)
                && next.Proposals.Any(p => p.Digest == j.Digest && j.Operation < p.Operations.Length
                    && p.Operations[j.Operation].Kind == OpKind.Create && p.Operations[j.Operation].Item == mapping.Local
                    && mapping.Remote.BelongsTo(p.Connection))
                && j.Receipts.Any(r => r.PublishedKey == mapping.Remote)))
                errors.Add(new(ErrorCode.Conflict, "A mapping needs the original create's attributable acknowledgement."));
        var attemptedCreates = next.Journal.Where(j => j.Status != OpStatus.Planned)
            .Select(j => (Row: j, Op: next.Proposals.FirstOrDefault(p => p.Digest == j.Digest)?.Operations.ElementAtOrDefault(j.Operation)))
            .Where(x => x.Op?.Kind == OpKind.Create).ToArray();
        if (attemptedCreates.GroupBy(x => x.Op!.Item).Any(g => g.Count() > 1))
            errors.Add(new(ErrorCode.Conflict, "A durable creation UUID cannot begin another attempt under a replacement digest."));
        return errors.ToImmutable();
    }

    private static bool SameReceipt(Receipt left, Receipt right)
        => left.Digest == right.Digest && left.Operation == right.Operation && left.Attempt == right.Attempt
            && left.Status == right.Status && left.Revision == right.Revision && left.PublishedKey == right.PublishedKey
            && left.Evidence.SequenceEqual(right.Evidence, StringComparer.Ordinal)
            && (left.Error is null ? right.Error is null
                : right.Error is not null && left.Error.Code == right.Error.Code && left.Error.Message == right.Error.Message
                    && left.Error.Evidence.SequenceEqual(right.Error.Evidence, StringComparer.Ordinal));
}
