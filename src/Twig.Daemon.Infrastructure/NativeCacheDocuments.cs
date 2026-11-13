using System.Text.Json.Serialization;
using System.Collections.Immutable;
using Bench.Domain;
using Twig.Core;
using Twig.Infrastructure.Services;

namespace Bench.Infrastructure;

internal sealed record NativeStampDocument(BindingDocument Binding, string PrincipalId, string AuthorityStamp)
{
    internal static NativeStampDocument From(NativeSourceStamp stamp) => new(
        BindingDocument.From(NativeSourceAdapter.Binding(stamp)), stamp.PrincipalId, stamp.AuthorityStamp);
    internal NativeSourceStamp ToNative()
    {
        var binding = Binding.ToDomain();
        var stamp = new NativeSourceStamp(binding.Connection, binding.StoreInstance, PrincipalId, AuthorityStamp);
        if (NativeSourceAdapter.Binding(stamp) != binding) throw new InvalidDataException("Native cache stamp mismatch.");
        return stamp;
    }
}
internal sealed record TypeDocument(string Reference, string StateField, FieldDef[] Fields, State[] States,
    Transition[] Transitions, RuleDocument[] Rules, bool Hidden, TypePresentation? Presentation = null)
{
    public string[] Gaps { get; init; } = [];
    internal static TypeDocument From(ItemType type) => new(type.Reference, type.StateField, type.Fields.ToArray(),
        type.States.ToArray(), type.Transitions.ToArray(), type.Rules.Select(RuleDocument.From).ToArray(), type.Hidden, type.Presentation)
        { Gaps = type.Gaps.ToArray() };
    internal ItemType ToCore() => new(Reference, StateField, Fields, States, Transitions, Rules.Select(r => r.ToCore()), Hidden, Presentation)
        { Gaps = Gaps.ToImmutableArray() };
}
internal sealed record ConditionDocument(ConditionKind Kind, string Field, ValueDocument? Value)
{
    internal static ConditionDocument From(RuleCondition value) => new(value.Kind, value.Field,
        value.Value is null ? null : ValueDocument.From(value.Value));
    internal RuleCondition ToCore() => new(Kind, Field, Value?.ToCore());
}
internal sealed record RuleDocument(string Field, RuleAction Action, ConditionDocument[] Conditions,
    ValueDocument[] Values, bool Disabled)
{
    internal static RuleDocument From(FieldRule value) => new(value.Field, value.Action,
        value.Conditions.Select(ConditionDocument.From).ToArray(), value.Values.Select(ValueDocument.From).ToArray(), value.Disabled);
    internal FieldRule ToCore() => new(Field, Action, Conditions.Select(c => c.ToCore()), Values.Select(v => v.ToCore()), Disabled);
}
internal sealed record SchemaDocument(TypeDocument[] Types, RelationKind[] Relations, string[] Gaps, string Fingerprint, string? WebUrl = null,
    PlanningMetadata? Planning = null)
{
    internal static SchemaDocument From(ProcessSchema schema) => new(schema.Types.Select(TypeDocument.From).ToArray(),
        schema.Relations.ToArray(), schema.Gaps.ToArray(), schema.Fingerprint, schema.WebUrl, schema.Planning);
    internal ProcessSchema ToCore(Connection connection)
    {
        var schema = new ProcessSchema(connection, Types.Select(t => t.ToCore()), Relations, Gaps, WebUrl) { Planning = Planning };
        if (schema.Fingerprint != Fingerprint) throw new InvalidDataException("Native schema cache fingerprint mismatch.");
        return schema;
    }
}
internal sealed record NativeCacheDocument(int Version, NativeStampDocument Stamp, SchemaDocument Schema,
    DateTimeOffset OnlineObservedAt, bool Blocked);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, MaxDepth = 64,
    GenerationMode = JsonSourceGenerationMode.Metadata, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(NativeCacheDocument))]
internal sealed partial class NativeCacheJson : JsonSerializerContext;
