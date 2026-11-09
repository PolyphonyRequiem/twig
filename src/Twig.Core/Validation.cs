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
        errors.AddRange(ProposalSchemaErrors(proposal, schema, store));
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
        errors.AddRange(ProposalSchemaErrors(proposal, schema, store));
        if (!store.Proposals.Any(p => p.Digest == proposal.Digest)
            || !store.Journal.Any(j => j.Digest == proposal.Digest && j.Operation == operation && j.Status == OpStatus.Planned)
            || store.Journal.Any(j => j.Digest == proposal.Digest && j.Operation < operation && j.Status != OpStatus.Verified))
            errors.Add(new(ErrorCode.Conflict, "The retained operation is not ready for its first attempt."));
        ValidateOperation(proposal.Operations[operation], proposal, schema, store, capabilities, errors);
        return errors.ToImmutable();
    }

    private static IEnumerable<Error> ProposalSchemaErrors(Proposal proposal, ProcessSchema schema, StoreSnapshot store)
    {
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in proposal.Operations)
        {
            if (op.Type is not null) references.Add(op.Type);
            foreach (var key in op.Links.Select(link => link.Other).Prepend(op.Item)
                .Concat(op.Other is null ? [] : new[] { op.Other }).Distinct())
            {
                var resolved = store.Mappings.FirstOrDefault(mapping => mapping.Local == key)?.Remote ?? key;
                var observed = store.Items.FirstOrDefault(item => item.Key == resolved);
                var draft = store.Drafts.FirstOrDefault(draft => draft.Key == key);
                if (observed is not null) references.Add(observed.Type);
                if (draft is not null)
                {
                    references.Add(draft.Type);
                    if (draft.Baseline is not null) references.Add(draft.Baseline.Type);
                }
                if (observed is null && draft is null)
                    yield return new(ErrorCode.Unavailable, "An effect endpoint's exact type was not observed.");
            }
        }
        foreach (var reference in references)
            if (!schema.Types.Any(type => type.Reference == reference))
                yield return new(ErrorCode.Unavailable, $"Effect type '{reference}' is outside the discovered schema.");
        foreach (var error in SchemaErrors(schema, schema.Types.Where(type => references.Contains(type.Reference))))
            yield return error;
    }

    private static IEnumerable<Error> SchemaErrors(ProcessSchema schema, IEnumerable<ItemType>? relevantTypes = null)
    {
        if (!schema.Gaps.IsEmpty)
            yield return new(ErrorCode.Unavailable, "Schema discovery is incomplete.") { Evidence = schema.Gaps };
        if (schema.Types.IsEmpty || schema.Relations.Any(r => !Enum.IsDefined(r.Shape)))
            yield return new(ErrorCode.Unavailable, "Type or relation semantics are unavailable.");
        foreach (var type in relevantTypes ?? schema.Types)
        {
            if (!type.Gaps.IsEmpty)
                yield return new(ErrorCode.Unavailable, $"Type '{type.Reference}' discovery is incomplete.") { Evidence = type.Gaps };
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
                    || rule.Action is RuleAction.Supplied or RuleAction.Default && rule.Values.Length > 1
                    || rule.Action == RuleAction.Default && rule.Values.IsEmpty
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
        if (!op.Links.IsEmpty && op.Kind is not (OpKind.Create or OpKind.Batch))
            Fail(ErrorCode.InvalidInput, "Compound links belong only to an explicit create or native batch effect.");
        if (!local && op.ExpectedLocalVersion is not null)
            Fail(ErrorCode.InvalidInput, "Only a local relationship can carry a local edge precondition.");
        if ((!link && op.Kind != OpKind.Create && (op.Other is not null || op.Relation is not null || op.Origin != RelationshipOrigin.Provider))
            || (op.Kind is not (OpKind.Batch or OpKind.Create) && op.Fields.Count != 0)
            || (op.Kind is not (OpKind.Create or OpKind.Batch) && op.Type is not null)
            || (op.Kind is not (OpKind.Comment or OpKind.Batch) && op.Body is not null)
            || op.Kind == OpKind.LinkResource && op.Resource is null
            || op.Kind is not (OpKind.Create or OpKind.LinkResource) && op.Resource is not null)
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
            if (draft is null || draft.Type != op.Type || !draft.Changes.Equals(op.Fields)
                || draft.Parent != op.Other || draft.ParentRelation != op.Relation || draft.Resource != op.Resource
                || !draft.Links.SequenceEqual(op.Links))
                Fail(ErrorCode.Conflict, "Creation must bind the exact retained seed type, fields and creation-bound links.");
            if (op.Links.Distinct().Count() != op.Links.Length || op.Links.Any(edge => edge.Other == op.Item))
                Fail(ErrorCode.InvalidInput, "Creation link intent contains a duplicate or self edge.");
            foreach (var edge in op.Links)
            {
                if (edge.Remove) Fail(ErrorCode.InvalidInput, "Creation cannot remove an existing relationship.");
                var kind = schema.Relations.FirstOrDefault(kind => kind.Reference == edge.Relation);
                var endpoint = store.Mappings.FirstOrDefault(mapping => mapping.Local == edge.Other)?.Remote ?? edge.Other;
                var predecessor = proposal.Operations.TakeWhile(candidate => candidate != op)
                    .Any(candidate => candidate.Kind == OpKind.Create && candidate.Item == edge.Other);
                if (!capabilities.AtomicFieldBatches || kind is null || kind.Shape is not (RelationShape.Dependency or RelationShape.Association)
                    || !capabilities.NativeRelations.Contains(edge.Relation)
                    || !edge.Outgoing && !kind.Directed)
                    Fail(ErrorCode.Unsupported, "Creation links require discovered native dependency/association semantics and proven creation coupling.");
                if (!(endpoint.BelongsTo(proposal.Connection)
                    || endpoint.IsLocal && predecessor && store.Drafts.Any(draft => draft.Key == endpoint)))
                    Fail(ErrorCode.Unsupported, "A creation link endpoint requires an in-scope observation or preceding captured creation.");
                else if (!endpoint.IsLocal && !store.Items.Any(item => item.Key == endpoint && item.RelationshipsComplete))
                    Fail(ErrorCode.Unavailable, "The creation link endpoint was not completely observed.");
            }
            if (op.Origin != RelationshipOrigin.Provider || (op.Other is null) != (op.Relation is null))
                Fail(ErrorCode.InvalidInput, "Creation-bound parent intent requires its explicit native relation.");
            if (op.Other is not null)
            {
                var mappedParent = store.Mappings.FirstOrDefault(mapping => mapping.Local == op.Other);
                var parentKey = mappedParent?.Remote ?? op.Other;
                var earlierCreate = proposal.Operations.TakeWhile(candidate => candidate != op)
                    .Any(candidate => candidate.Kind == OpKind.Create && candidate.Item == op.Other);
                if (!capabilities.AtomicFieldBatches
                    || !(parentKey.BelongsTo(proposal.Connection) || parentKey.IsLocal && earlierCreate
                        && store.Drafts.Any(parent => parent.Key == parentKey))
                    || !schema.Relations.Any(relation => relation.Reference == op.Relation && relation.Shape == RelationShape.Hierarchy && relation.Directed)
                    || !capabilities.NativeRelations.Contains(op.Relation!))
                    Fail(ErrorCode.Unsupported, "Creation-bound parenting requires an in-scope observation or preceding captured creation and proven single-request native coupling.");
                else if (!parentKey.IsLocal && !store.Items.Any(item => item.Key == parentKey && item.RelationshipsComplete))
                    Fail(ErrorCode.Unavailable, "The creation-bound parent was not completely observed.");
            }
            if (op.Resource is not null && (!capabilities.AtomicFieldBatches
                || !schema.Relations.Any(relation => relation.Reference == op.Resource.Relation && relation.Shape == RelationShape.Resource)))
                Fail(ErrorCode.Unsupported, "Creation-bound resources require proven single-request native coupling and discovered resource semantics.");
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
        if (op.Kind == OpKind.Batch && !op.Links.IsEmpty)
        {
            if (!capabilities.AtomicFieldBatches || !capabilities.AtomicRelationshipBatches
                || op.Links.GroupBy(edge => (edge.Other, edge.Relation, edge.Outgoing)).Any(group => group.Count() > 1))
                Fail(ErrorCode.Unsupported, "Compound relationship deltas require atomic native coupling and unique directed effects.");
            foreach (var delta in op.Links)
            {
                var kind = schema.Relations.FirstOrDefault(value => value.Reference == delta.Relation);
                if (kind is null || kind.Shape == RelationShape.Resource || !delta.Outgoing && !kind.Directed
                    || !capabilities.NativeRelations.Contains(delta.Relation)
                    || !capabilities.Operations.Contains(delta.Remove ? OpKind.Unrelate : OpKind.Relate)
                    || delta.Other == op.Item || !delta.Other.BelongsTo(proposal.Connection))
                { Fail(ErrorCode.Unsupported, "Every compound delta needs native incident semantics and an exact in-scope endpoint."); continue; }
                if (new[] { op.Item, delta.Other }.Any(key => !store.Items.Any(item => item.Key == key && item.RelationshipsComplete)))
                    Fail(ErrorCode.Unavailable, "Every changed incident endpoint must be completely observed.");
                var exists = store.Relationships.Any(edge => edge.Origin == RelationshipOrigin.Provider && !edge.Deleted
                    && edge.Kind == delta.Relation && (delta.Outgoing ? edge.From == op.Item && edge.To == delta.Other
                        : edge.To == op.Item && edge.From == delta.Other)
                    || !kind.Directed && edge.Origin == RelationshipOrigin.Provider && !edge.Deleted
                        && edge.Kind == delta.Relation && edge.From == delta.Other && edge.To == op.Item);
                if (exists != delta.Remove) Fail(ErrorCode.Conflict, "The exact relationship add/remove baseline changed.");
                if (!delta.Remove && kind.Shape == RelationShape.Hierarchy)
                {
                    var child = delta.Outgoing ? delta.Other : op.Item;
                    if (store.Relationships.Any(edge => edge.Origin == RelationshipOrigin.Provider && !edge.Deleted
                        && edge.Kind == delta.Relation && edge.To == child
                        && !op.Links.Any(removal => removal.Remove && removal.Relation == edge.Kind
                            && (removal.Outgoing ? edge.From == op.Item && edge.To == removal.Other
                                : edge.To == op.Item && edge.From == removal.Other))))
                        Fail(ErrorCode.Unsupported, "A parent change requires explicit removal in the same atomic batch; no existing parent is removed or replaced implicitly.");
                }
            }
        }
        if (local && op.ExpectedRevision is not null)
            Fail(ErrorCode.InvalidInput, "A local relationship uses the store CAS, not a provider revision.");
        if (op.Kind == OpKind.LinkResource && op.Resource is { } resource)
        {
            if (!schema.Relations.Any(r => r.Reference == resource.Relation && r.Shape == RelationShape.Resource))
                Fail(ErrorCode.Unsupported, "The resource relation is not enabled in the discovered native schema.");
            var source = store.Items.FirstOrDefault(i => i.Key == op.Item);
            if (source?.ResourcesComplete != true)
                Fail(ErrorCode.Unavailable, "Complete native resource-link observations are required.");
            else if (source.Resources.Any(r => r.Relation == resource.Relation && r.Target == resource.Target))
                Fail(ErrorCode.Conflict, "The reviewed native resource link already exists.");
        }
        if (link)
        {
            if (schema.Relations.Any(r => r.Reference == op.Relation && r.Shape == RelationShape.Resource))
                Fail(ErrorCode.InvalidInput, "A resource locator cannot be represented as another work-item identity.");
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
                if (local && new[] { op.Item, op.Other }.Any(k => !EndpointObserved(k)))
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
        bool EndpointObserved(ItemKey key)
        {
            if (!key.IsLocal) return key.BelongsTo(proposal.Connection) && store.Items.Any(item => item.Key == key);
            if (store.Drafts.Any(draft => draft.Key == key && draft.Baseline is null)) return true;
            return store.Mappings.Any(mapping => mapping.Local == key && mapping.Remote.BelongsTo(proposal.Connection)
                && store.Items.Any(item => item.Key == mapping.Remote)
                && store.Proposals.Any(original => original.Digest == mapping.ProposalDigest
                    && original.Connection == proposal.Connection && mapping.Operation < original.Operations.Length
                    && original.Operations[mapping.Operation].Kind == OpKind.Create
                    && original.Operations[mapping.Operation].Item == key)
                && store.Journal.Any(row => row.Digest == mapping.ProposalDigest && row.Operation == mapping.Operation
                    && row.Status == OpStatus.Verified && row.Receipts.Any(receipt =>
                        receipt.Status == OpStatus.Verified && receipt.PublishedKey == mapping.Remote)));
        }

        if (op.Kind == OpKind.Comment && (op.Body is null || op.Body.Kind is not (FieldKind.Text or FieldKind.Html or FieldKind.Markdown) || op.Body.IsEmpty))
            Fail(ErrorCode.InvalidInput, "Comment must retain an explicit nonempty body and format.");
        if (op.Kind == OpKind.Batch && op.Body is not null
            && (op.Body.Kind is not (FieldKind.Text or FieldKind.Html or FieldKind.Markdown) || op.Body.IsEmpty || !capabilities.AtomicFieldBatches))
            Fail(ErrorCode.Unsupported, "A batch note requires a nonempty typed body and proven single-request atomic coupling.");
        if (op.Kind is OpKind.Batch or OpKind.Create)
        {
            if (op.Fields.Count == 0 && (op.Kind != OpKind.Batch || op.Type is null && op.Links.IsEmpty))
                Fail(ErrorCode.InvalidInput, "A field effect cannot be empty.");
            if (op.Kind == OpKind.Batch && !capabilities.AtomicFieldBatches
                && (op.Type is not null || op.Fields.Count > 1 && op.Concurrency != ConcurrencyMode.ObservedNonAtomic))
                Fail(ErrorCode.Unsupported, "Independent fields require explicit observed non-atomic intent; type changes require atomic support.");
            var source = store.Items.FirstOrDefault(i => i.Key == op.Item);
            var type = schema.Types.FirstOrDefault(t => t.Reference == (op.Type ?? source?.Type));
            if (type is null) Fail(ErrorCode.Unavailable, "The item's discovered type is unavailable.");
            else ValidateFields(op, source, type, capabilities.AtomicFieldBatches, Fail);
        }
    }

    private static void ValidateFields(Op op, WorkItem? source, ItemType type, bool atomicFields, Action<ErrorCode, string> fail)
    {
        var old = source?.Fields ?? new FieldValues(type.Fields.Select(f =>
            KeyValuePair.Create(f.Reference, FieldValue.Null)));
        var effective = old.Overlay(op.Fields);
        var evaluated = EvaluateRules(type, old, effective);
        if (!evaluated.IsSuccess) { fail(evaluated.Error.Code, evaluated.Error.Message); return; }
        effective = evaluated.Value.Effective;
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
        var active = evaluated.Value.Active;
        foreach (var rule in active)
        {
            effective.TryGetValue(rule.Field, out var value);
            var supplied = active.Any(r => r.Field == rule.Field && Supplies(r));
            switch (rule.Action)
            {
                case RuleAction.Required:
                    if (!supplied && (value is null || value.IsEmpty))
                        fail(ErrorCode.InvalidInput, $"Required field '{rule.Field}' has no effective value.");
                    if (!supplied && changedState && target?.Category == StateCategory.Completed)
                    {
                        var requiredBefore = RequiredBefore(type, rule.Field, old);
                        if (!requiredBefore.IsSuccess) fail(requiredBefore.Error.Code, requiredBefore.Error.Message);
                        var newGate = source is null || source.Type != type.Reference || !requiredBefore.IsSuccess || !requiredBefore.Value;
                        if (newGate && !op.Fields.ContainsKey(rule.Field))
                            fail(ErrorCode.InvalidInput, $"Close-gate field '{rule.Field}' must ride in the same batch as the transition.");
                        if (newGate && rule.Field != type.StateField && !atomicFields)
                            fail(ErrorCode.Unsupported, $"Close-gate field '{rule.Field}' must be applied atomically with the transition.");
                    }
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
                case RuleAction.Default:
                    if (rule.Values.Length > 1) fail(ErrorCode.Unavailable, $"Supplier for '{rule.Field}' is ambiguous.");
                    break;
            }
        }
    }

    public static Result<EditorEvaluation> EvaluateEditorFields(ProcessSchema schema, WorkItem source, FieldValues changes,
        bool atomicFields, string? typeOverride = null)
    {
        if (!source.Key.IsLocal && !source.Key.BelongsTo(schema.Connection))
            return Result<EditorEvaluation>.Fail(ErrorCode.Conflict, "Editor observations belong to another provider resource scope.");
        var type = schema.Types.FirstOrDefault(value => value.Reference == (typeOverride ?? source.Type));
        if (type is null) return Result<EditorEvaluation>.Fail(ErrorCode.Unavailable, "The exact editor type is unavailable.");
        var unavailable = SchemaErrors(schema, [type]).FirstOrDefault();
        if (unavailable is not null) return Result<EditorEvaluation>.Fail(unavailable);
        var baseline = source.Key.IsLocal ? null : source;
        var before = baseline?.Fields ?? new FieldValues(type.Fields.Select(field => KeyValuePair.Create(field.Reference, FieldValue.Null)));
        var effective = before.Overlay(changes);
        var evaluated = EvaluateRules(type, before, effective);
        if (!evaluated.IsSuccess) return Result<EditorEvaluation>.Fail(evaluated.Error);
        var active = evaluated.Value.Active;
        var errors = ImmutableArray.CreateBuilder<Error>();
        ValidateFields(new("editor", source.Key.IsLocal ? OpKind.Create : OpKind.Batch, source.Key, source.Revision,
            changes, source.Key.IsLocal ? type.Reference : typeOverride), baseline, type,
            atomicFields, (code, message) => errors.Add(new(code, message)));
        var fields = type.Fields.Select(field =>
        {
            var rules = active.Where(rule => rule.Field == field.Reference).ToArray();
            var allowed = rules.Where(rule => rule.Action == RuleAction.Allowed).Select(rule => rule.Values).ToArray();
            var supplied = rules.Any(Supplies);
            return new EvaluatedField(field, type.Hidden || field.ReadOnly || rules.Any(rule => rule.Action == RuleAction.ReadOnly),
                rules.Any(rule => rule.Action == RuleAction.Required) && !supplied,
                allowed.Length == 0 ? [] : allowed.Skip(1).Aggregate(allowed[0], (values, next) => values.Intersect(next).ToImmutableArray()),
                allowed.Length != 0);
        }).ToImmutableArray();
        before.TryGetValue(type.StateField, out var previous);
        var stateField = fields.Single(field => field.Definition.Reference == type.StateField);
        var states = type.States.Where(state => (state.Name == previous?.Text
            || !stateField.ReadOnly && (source.Key.IsLocal
                || type.Transitions.Any(transition => transition.From == previous?.Text && transition.To == state.Name)))
            && (!stateField.HasAllowedConstraint || stateField.Allowed.Contains(FieldValue.String(state.Name))))
            .Select(state => state.Name).ToImmutableArray();
        return Result<EditorEvaluation>.Ok(new(fields, states, errors.ToImmutable()));
    }

    private static Result<bool> RequiredBefore(ItemType type, string field, FieldValues before)
    {
        var required = false;
        var supplied = false;
        var evaluated = EvaluateRules(type, before, before);
        if (!evaluated.IsSuccess) return Result<bool>.Fail(evaluated.Error);
        foreach (var rule in evaluated.Value.Active.Where(rule => rule.Field == field))
        {
            required |= rule.Action == RuleAction.Required;
            supplied |= Supplies(rule);
        }
        return Result<bool>.Ok(required && !supplied);
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

    private static bool Supplies(FieldRule rule) => rule.Action is RuleAction.Supplied or RuleAction.Default
        && (rule.Values.IsEmpty || rule.Values.Any(value => !value.IsEmpty));

    private static Result<(FieldValues Effective, ImmutableArray<FieldRule> Active)> EvaluateRules(
        ItemType type, FieldValues before, FieldValues authored)
    {
        var active = ImmutableArray.CreateBuilder<FieldRule>();
        foreach (var rule in type.Rules.Where(rule => !rule.Disabled))
        {
            if (!Enum.IsDefined(rule.Action) || rule.Action == RuleAction.Unknown)
                return Result<(FieldValues, ImmutableArray<FieldRule>)>.Fail(ErrorCode.Unavailable, "An enabled schema rule cannot be understood.");
            var matches = MatchesConditions(rule.Conditions, before, authored);
            if (!matches.IsSuccess) return Result<(FieldValues, ImmutableArray<FieldRule>)>.Fail(matches.Error);
            if (!matches.Value) continue;
            if (rule.Action == RuleAction.Default && authored.TryGetValue(rule.Field, out var existing)
                && (!existing.IsEmpty || existing.Text is { Length: > 0 }))
                continue;
            active.Add(rule);
        }
        var effective = authored;
        foreach (var group in active.Where(rule => rule.Action is RuleAction.Supplied or RuleAction.Default)
            .GroupBy(rule => rule.Field, StringComparer.Ordinal))
        {
            var values = group.SelectMany(rule => rule.Values).Distinct().ToArray();
            if (values.Length > 1 || group.Any(rule => rule.Values.IsEmpty) && group.Count() > 1)
                return Result<(FieldValues, ImmutableArray<FieldRule>)>.Fail(ErrorCode.Unavailable,
                    "Enabled suppliers disagree or their combined effective value is unknown.");
            var known = values.Length == 1;
            authored.TryGetValue(group.Key, out var original);
            if (type.Rules.Where(rule => !rule.Disabled).SelectMany(rule => rule.Conditions)
                .Any(condition => condition.Field == group.Key && condition.Kind != ConditionKind.Was)
                && (!known || !Equals(original, values[0])))
                return Result<(FieldValues, ImmutableArray<FieldRule>)>.Fail(ErrorCode.Unavailable,
                    "Rule predicates depend on a provider-supplied value; chained rule evaluation is unavailable.");
            if (known) effective = effective.Set(group.Key, values[0]);
            else if (active.Any(rule => rule.Field == group.Key && rule.Action == RuleAction.Allowed))
                return Result<(FieldValues, ImmutableArray<FieldRule>)>.Fail(ErrorCode.Unavailable,
                    "An allowed-value constraint depends on an unobserved provider-supplied value.");
        }
        return Result<(FieldValues, ImmutableArray<FieldRule>)>.Ok((effective, active.ToImmutable()));
    }

    public static Result<ImmutableArray<string>> CloseGates(ProcessSchema schema, WorkItem source,
        FieldValues changes, string? typeOverride = null)
    {
        if (typeOverride is not null && typeOverride != source.Type)
            return Result<ImmutableArray<string>>.Fail(ErrorCode.Unsupported, "Close-gate pre-requiredness cannot be inferred across a type change.");
        var type = schema.Types.FirstOrDefault(t => t.Reference == (typeOverride ?? source.Type));
        if (type is null || !source.Fields.TryGetValue(type.StateField, out var previous))
            return Result<ImmutableArray<string>>.Fail(ErrorCode.Unavailable, "The type or pre-state needed to discover close gates is unobserved.");
        var unavailable = SchemaErrors(schema, [type]).FirstOrDefault();
        if (unavailable is not null) return Result<ImmutableArray<string>>.Fail(unavailable);
        var effective = source.Fields.Overlay(changes);
        if (!effective.TryGetValue(type.StateField, out var state)
            || !type.States.Any(s => s.Name == state.Text))
            return Result<ImmutableArray<string>>.Fail(ErrorCode.InvalidInput, "The proposed state was not discovered for this type.");
        if (Equals(previous, state) || !type.States.Any(s => s.Name == state.Text && s.Category == StateCategory.Completed))
            return Result<ImmutableArray<string>>.Ok([]);
        var evaluated = EvaluateRules(type, source.Fields, effective);
        if (!evaluated.IsSuccess) return Result<ImmutableArray<string>>.Fail(evaluated.Error);
        var active = evaluated.Value.Active;
        var gates = new List<string>();
        foreach (var field in active.Where(r => r.Action == RuleAction.Required && r.Field != type.StateField
            && !active.Any(s => s.Field == r.Field && Supplies(s)))
            .Select(r => r.Field).Distinct(StringComparer.Ordinal))
        {
            var before = RequiredBefore(type, field, source.Fields);
            if (!before.IsSuccess) return Result<ImmutableArray<string>>.Fail(before.Error);
            if (!before.Value) gates.Add(field);
        }
        return Result<ImmutableArray<string>>.Ok(gates.ToImmutableArray());
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
