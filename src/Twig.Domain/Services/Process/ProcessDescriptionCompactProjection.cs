using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Process;

/// <summary>
/// Opt-in COMPACT projection of a <see cref="ProcessDescription"/>: a single-line JSON
/// envelope carrying only the sections a caller asked for, filtered to reference names they
/// named. Built from the same <see cref="ProcessDescription"/> the full descriptor uses —
/// no second fetch, no second evaluator, no second cache.
/// </summary>
/// <remarks>
/// <para>
/// The full descriptor is still the byte-stable promise (AB#241). This envelope is an
/// adapter for agent consumers that need one type's requiredness and rules — not the whole
/// process. It preserves the full route in every response so a caller can widen without
/// guessing, and it names its own contract version so a consumer can pin a shape without
/// pinning the full descriptor's shape.
/// </para>
/// <para>
/// This projection <b>NEVER</b> asserts <c>transitionAllowed</c>: admissibility depends on
/// a specific item's revision plus the rule engine's evaluation against that item, and this
/// projection has neither. Rules ride verbatim, tagged <c>evaluated: false</c>, and a
/// per-type <c>conditionsNotEvaluated</c> warning is emitted whenever any rule is carried —
/// Twig does not evaluate rule conditions here, and the response says so.
/// </para>
/// <para>
/// Truth-honesty signals from the full descriptor survive: <c>unfetched</c> parts,
/// <c>unresolvedValueConstraint</c> when a picklist source was unreadable,
/// <c>conditionalRequiredness</c> whenever requiredness carries conditions. A
/// <see cref="CompactRequest.FieldFilter"/> that matches nothing on any described type
/// emits <c>unknownFieldRef</c> rather than silently dropping.
/// </para>
/// </remarks>
internal static class ProcessDescriptionCompactProjection
{
    /// <summary>Compact envelope contract version. Independent of descriptor version.</summary>
    internal const string CompactContractVersion = "process-description-compact/1";

    /// <summary>Separator the CLI splits sections/fields on; kept here to avoid drift.</summary>
    internal const char FilterSeparator = ',';

    /// <summary>Sections a caller may opt in to.</summary>
    [Flags]
    internal enum CompactSections
    {
        None = 0,
        Fields = 1,
        Requirements = 2,
    }

    /// <summary>Parsed request shape the command hands this projection.</summary>
    /// <param name="Sections">Which sections to include; at least one must be set.</param>
    /// <param name="FieldFilter">Ordinal-case-insensitive reference-name filter; empty = unfiltered.</param>
    internal sealed record CompactRequest(
        CompactSections Sections,
        IReadOnlySet<string> FieldFilter);

    /// <summary>Attempted parse of the two flag strings.</summary>
    internal abstract record CompactParseResult
    {
        /// <summary>Successfully parsed request.</summary>
        internal sealed record Parsed(CompactRequest Request) : CompactParseResult;

        /// <summary>Section list contained a token that is not <c>fields</c> or <c>requirements</c>.</summary>
        internal sealed record UnknownSection(string Token) : CompactParseResult;

        /// <summary>Section list resolved to no sections — a compact request is meaningless without at least one.</summary>
        internal sealed record NoSections : CompactParseResult;
    }

    /// <summary>Parses the <c>--sections</c> / <c>--fields</c> flag values.</summary>
    /// <remarks>
    /// Section tokens are ordinal-case-insensitive. Field tokens are trimmed but kept in
    /// original casing; the set uses <see cref="StringComparer.OrdinalIgnoreCase"/> so
    /// <c>System.Title</c> and <c>system.title</c> match the same field.
    /// </remarks>
    internal static CompactParseResult Parse(string? sections, string? fields)
    {
        var kinds = CompactSections.None;
        foreach (var token in SplitTrim(sections))
        {
            if (token.Equals("fields", StringComparison.OrdinalIgnoreCase))
                kinds |= CompactSections.Fields;
            else if (token.Equals("requirements", StringComparison.OrdinalIgnoreCase))
                kinds |= CompactSections.Requirements;
            else
                return new CompactParseResult.UnknownSection(token);
        }

        if (kinds == CompactSections.None)
            return new CompactParseResult.NoSections();

        var filter = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in SplitTrim(fields))
            filter.Add(token);

        return new CompactParseResult.Parsed(new CompactRequest(kinds, filter));
    }

    /// <summary>Serialize a description as a compact envelope, returning the full JSON as a string.</summary>
    internal static string Serialize(ProcessDescription description, CompactRequest request)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(request);

        var buffer = new ArrayBufferWriter<byte>();
        var options = new JsonWriterOptions { Indented = false, SkipValidation = false };
        using (var writer = new Utf8JsonWriter(buffer, options))
        {
            WriteEnvelope(writer, description, request);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteEnvelope(
        Utf8JsonWriter writer,
        ProcessDescription description,
        CompactRequest request)
    {
        writer.WriteStartObject();
        writer.WriteString("contractVersion", CompactContractVersion);

        writer.WritePropertyName("connection");
        writer.WriteStartObject();
        writer.WriteString("organization", description.Header.Organization);
        writer.WriteString("project", description.Header.ProjectName);
        writer.WriteEndObject();

        writer.WritePropertyName("process");
        writer.WriteStartObject();
        writer.WriteString("id", description.Header.ProcessId);
        writer.WriteString("name", description.Header.ProcessName);
        writer.WriteEndObject();

        writer.WriteString(
            "capturedAt",
            description.Header.CapturedAtUtc.ToUniversalTime()
                .ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("descriptorVersion", description.Header.DescriptorVersion);

        writer.WritePropertyName("route");
        writer.WriteStartObject();
        writer.WriteString("full", BuildFullRoute(description));
        writer.WriteString(
            "refresh",
            "re-run this command; capturedAt reflects the latest fetch");
        writer.WriteEndObject();

        var warnings = new List<CompactWarning>();
        var scope = new ScopeAccumulator();
        var matchedRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fullyRead = true;

        // Truth-honesty signals collected up-front so the field filter cannot mask them.
        foreach (var type in description.Types)
        {
            if (type.Unfetched.Count > 0)
            {
                fullyRead = false;
                foreach (var subject in type.Unfetched)
                {
                    warnings.Add(new CompactWarning(
                        "unfetched",
                        $"{type.ReferenceName}#{subject}",
                        $"The '{subject}' route did not answer for type '{type.ReferenceName}'."));
                }
            }

            CollectFieldWarnings(type, warnings);

            if (type.Rules.Count > 0)
            {
                warnings.Add(new CompactWarning(
                    "conditionsNotEvaluated",
                    type.ReferenceName,
                    "Rule conditions are carried verbatim and NOT evaluated here; a caller "
                    + "must evaluate them against a specific item before treating any rule "
                    + "as applying or not applying."));
            }
        }

        writer.WritePropertyName("types");
        writer.WriteStartArray();
        foreach (var type in description.Types)
            WriteType(writer, type, request, matchedRefs, scope);
        writer.WriteEndArray();

        foreach (var requested in request.FieldFilter)
        {
            if (!matchedRefs.Contains(requested))
            {
                warnings.Add(new CompactWarning(
                    "unknownFieldRef",
                    requested,
                    "No described type carries this reference name; filter matched nothing."));
            }
        }

        writer.WritePropertyName("completeness");
        writer.WriteStartObject();
        writer.WriteBoolean("fullyRead", fullyRead);
        writer.WriteNumber("typeCount", description.Types.Count);
        writer.WriteNumber("projectedFieldCount", scope.FieldRowsEmitted);
        writer.WriteNumber("projectedRequirementRowCount", scope.RequirementRowsEmitted);
        writer.WriteString("sectionCoverage", scope.AnyPartial ? "partial" : "complete");
        writer.WriteEndObject();

        writer.WritePropertyName("sections");
        writer.WriteStartArray();
        if (request.Sections.HasFlag(CompactSections.Fields))
            writer.WriteStringValue("fields");
        if (request.Sections.HasFlag(CompactSections.Requirements))
            writer.WriteStringValue("requirements");
        writer.WriteEndArray();

        writer.WritePropertyName("warnings");
        writer.WriteStartArray();
        foreach (var w in warnings)
        {
            writer.WriteStartObject();
            writer.WriteString("code", w.Code);
            writer.WriteString("subject", w.Subject);
            writer.WriteString("detail", w.Detail);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>
    /// Builds <c>route.full</c>. Uses the actual named type when the projection narrowed to
    /// exactly one — a caller can copy the string and get the full descriptor. Falls back
    /// to a placeholder when the projection carries multiple types (defensive; the CLI
    /// normally enforces one).
    /// </summary>
    private static string BuildFullRoute(ProcessDescription description)
        => description.Types.Count == 1
            ? $"twig process description {description.Types[0].ReferenceName} -o json"
            : "twig process description <type> -o json";

    private static void CollectFieldWarnings(
        ProcessDescriptionType type,
        List<CompactWarning> warnings)
    {
        foreach (var field in type.Fields)
        {
            if (field.Requiredness.Kind == FieldRequirednessKind.Conditional
                && field.Requiredness.Conditions.Count > 0)
            {
                warnings.Add(new CompactWarning(
                    "conditionalRequiredness",
                    $"{type.ReferenceName}#{field.ReferenceName}",
                    "This field's requiredness depends on rule conditions; not evaluated here."));
            }

            if (field.ValueConstraint.Kind == FieldValueConstraintKind.Unknown)
            {
                warnings.Add(new CompactWarning(
                    "unresolvedValueConstraint",
                    $"{type.ReferenceName}#{field.ReferenceName}",
                    "The picklist source for this field was unreadable; response does not "
                    + "assert whether the field is list-backed."));
            }
        }
    }

    private static void WriteType(
        Utf8JsonWriter writer,
        ProcessDescriptionType type,
        CompactRequest request,
        HashSet<string> matchedRefs,
        ScopeAccumulator scope)
    {
        writer.WriteStartObject();
        writer.WriteString("referenceName", type.ReferenceName);
        writer.WriteString("name", type.Name);
        writer.WriteString("customization", type.Customization);
        if (type.Inherits is null)
            writer.WriteNull("inherits");
        else
            writer.WriteString("inherits", type.Inherits);
        writer.WriteBoolean("isDisabled", type.IsDisabled);

        if (request.Sections.HasFlag(CompactSections.Fields))
            WriteFieldsSection(writer, type, request.FieldFilter, matchedRefs, scope);

        if (request.Sections.HasFlag(CompactSections.Requirements))
            WriteRequirementsSection(writer, type, request.FieldFilter, matchedRefs, scope);

        writer.WriteEndObject();
    }

    private static void WriteFieldsSection(
        Utf8JsonWriter writer,
        ProcessDescriptionType type,
        IReadOnlySet<string> filter,
        HashSet<string> matchedRefs,
        ScopeAccumulator scope)
    {
        var filtered = filter.Count > 0;
        var partial = false;
        var emitted = 0;

        writer.WritePropertyName("fields");
        writer.WriteStartArray();
        foreach (var field in type.Fields)
        {
            if (filtered && !filter.Contains(field.ReferenceName))
            {
                partial = true;
                continue;
            }

            matchedRefs.Add(field.ReferenceName);
            emitted++;
            WriteField(writer, field);
        }
        writer.WriteEndArray();

        var coveragePartial = partial || TypeUnfetchTouchesFields(type);
        writer.WritePropertyName("fieldsScope");
        writer.WriteStartObject();
        writer.WriteString("coverage", coveragePartial ? "partial" : "complete");
        writer.WriteNumber("emitted", emitted);
        writer.WriteNumber("candidateTotal", type.Fields.Count);
        writer.WriteEndObject();

        scope.RegisterFields(emitted, coveragePartial);
    }

    private static void WriteRequirementsSection(
        Utf8JsonWriter writer,
        ProcessDescriptionType type,
        IReadOnlySet<string> filter,
        HashSet<string> matchedRefs,
        ScopeAccumulator scope)
    {
        var filtered = filter.Count > 0;
        var partial = false;
        var reqCount = 0;

        // States are frozen process facts (name + category) — the caller uses them to
        // reason about which state a rule condition targets. Transitions are deliberately
        // omitted: admissibility is item-scoped.
        writer.WritePropertyName("states");
        writer.WriteStartArray();
        foreach (var state in type.States)
        {
            writer.WriteStartObject();
            writer.WriteString("name", state.Name);
            writer.WriteString("category", state.StateCategory);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("requirements");
        writer.WriteStartArray();
        foreach (var field in type.Fields)
        {
            if (field.Requiredness.Kind == FieldRequirednessKind.Never)
                continue;
            if (filtered && !filter.Contains(field.ReferenceName))
            {
                partial = true;
                continue;
            }

            matchedRefs.Add(field.ReferenceName);
            reqCount++;
            WriteRequirement(writer, field, type.Rules);
        }

        // Rules ride verbatim regardless of --fields — a caller looking at requirements
        // needs the whole rule set for the type to reason about supplier actions and gates.
        foreach (var rule in type.Rules)
            WriteRule(writer, rule);
        writer.WriteEndArray();

        var coveragePartial = partial || TypeUnfetchTouchesRequirements(type);
        writer.WritePropertyName("requirementsScope");
        writer.WriteStartObject();
        writer.WriteString("coverage", coveragePartial ? "partial" : "complete");
        writer.WriteNumber("fieldRowsEmitted", reqCount);
        writer.WriteNumber("ruleRowsEmitted", type.Rules.Count);
        writer.WriteNumber("stateRowsEmitted", type.States.Count);
        writer.WriteEndObject();

        scope.RegisterRequirements(reqCount, coveragePartial);
    }

    private static void WriteField(Utf8JsonWriter writer, ProcessDescriptionField field)
    {
        writer.WriteStartObject();
        writer.WriteString("ref", field.ReferenceName);
        writer.WriteString("name", field.Name);
        writer.WriteString("type", field.Type);
        writer.WriteString("required", RequirednessToken(field.Requiredness.Kind));

        if (field.Requiredness.Kind == FieldRequirednessKind.Conditional
            && field.Requiredness.Conditions.Count > 0)
            writer.WriteString("requiredWhen", DescribeConditions(field.Requiredness));
        else
            writer.WriteNull("requiredWhen");

        writer.WriteString("valueConstraint", ValueConstraintToken(field.ValueConstraint.Kind));

        if (field.ValueConstraint.Values.Count == 0)
            writer.WriteNull("allowedValues");
        else
        {
            writer.WritePropertyName("allowedValues");
            writer.WriteStartArray();
            foreach (var v in field.ValueConstraint.Values)
                writer.WriteStringValue(v);
            writer.WriteEndArray();
        }

        // The fields route reports the server's default. It does NOT report rule-supplied
        // defaults or item-level defaults, so a null here does not license a caller claim.
        // The requirements section is where supplier gets answered.
        if (field.DefaultValue is null)
            writer.WriteNull("default");
        else
            writer.WriteString("default", field.DefaultValue);

        writer.WriteString("customization", field.Customization);
        writer.WriteBoolean("isLocked", field.IsLocked);
        writer.WriteEndObject();
    }

    private static void WriteRequirement(
        Utf8JsonWriter writer,
        ProcessDescriptionField field,
        IReadOnlyList<ProcessDescriptionRule> rules)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "field");
        writer.WriteString("ref", field.ReferenceName);
        writer.WriteString("required", RequirednessToken(field.Requiredness.Kind));

        if (field.Requiredness.Kind == FieldRequirednessKind.Conditional
            && field.Requiredness.Conditions.Count > 0)
            writer.WriteString("requiredWhen", DescribeConditions(field.Requiredness));
        else
            writer.WriteNull("requiredWhen");

        // Supplier is a three-state answer: "server" when a server default is known;
        // otherwise "unknown". "caller" is never claimed — rule actions (AB#803) or
        // item-level defaults can supply the value, and this process-scoped route cannot
        // evaluate them. The consumer decides after evaluating supplierActions against a
        // specific item.
        if (field.DefaultValue is null)
        {
            writer.WriteString("supplier", "unknown");
            writer.WriteNull("serverDefault");
        }
        else
        {
            writer.WriteString("supplier", "server");
            writer.WriteString("serverDefault", field.DefaultValue);
        }

        // Raw rule actions whose target is THIS field, verbatim. No supplier-vs-gate
        // classification of actionType — Twig does not evaluate rules here, and inventing a
        // known-supplier list would recreate the exact defect Main flagged in review.
        writer.WritePropertyName("supplierActions");
        writer.WriteStartArray();
        foreach (var rule in rules)
        {
            foreach (var action in rule.Actions)
            {
                if (!string.Equals(
                        action.TargetField,
                        field.ReferenceName,
                        StringComparison.OrdinalIgnoreCase))
                    continue;

                writer.WriteStartObject();
                writer.WriteString("rule", rule.Name);
                writer.WriteString("actionType", action.ActionType);
                if (action.Value is null)
                    writer.WriteNull("value");
                else
                    writer.WriteString("value", action.Value);
                writer.WriteEndObject();
            }
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    private static void WriteRule(Utf8JsonWriter writer, ProcessDescriptionRule rule)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", "rule");
        writer.WriteString("name", rule.Name);
        writer.WriteString("customization", RuleCustomizationToken(rule.Customization));
        writer.WriteBoolean("isDisabled", rule.IsDisabled);
        // Twig does not evaluate rule conditions here (no item, no local rule engine); this
        // flag lets a consumer refuse to treat rule conditions as either satisfied or
        // unsatisfied purely from the compact envelope.
        writer.WriteBoolean("evaluated", false);

        writer.WritePropertyName("conditions");
        writer.WriteStartArray();
        foreach (var c in rule.Conditions)
        {
            writer.WriteStartObject();
            writer.WriteString("conditionType", c.ConditionType);
            writer.WriteString("field", c.Field);
            if (c.Value is null)
                writer.WriteNull("value");
            else
                writer.WriteString("value", c.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WritePropertyName("actions");
        writer.WriteStartArray();
        foreach (var a in rule.Actions)
        {
            writer.WriteStartObject();
            writer.WriteString("actionType", a.ActionType);
            writer.WriteString("targetField", a.TargetField);
            if (a.Value is null)
                writer.WriteNull("value");
            else
                writer.WriteString("value", a.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
    }

    /// <summary>Whether this type's <see cref="ProcessDescriptionType.Unfetched"/> touches the fields view.</summary>
    private static bool TypeUnfetchTouchesFields(ProcessDescriptionType type)
    {
        foreach (var s in type.Unfetched)
            if (s is "fields" or "picklists" or "rules")
                return true;
        return false;
    }

    /// <summary>Whether this type's <see cref="ProcessDescriptionType.Unfetched"/> touches the requirements view.</summary>
    private static bool TypeUnfetchTouchesRequirements(ProcessDescriptionType type)
    {
        foreach (var s in type.Unfetched)
            if (s is "fields" or "rules" or "states")
                return true;
        return false;
    }

    private static string RequirednessToken(FieldRequirednessKind kind) => kind switch
    {
        FieldRequirednessKind.Always => "always",
        FieldRequirednessKind.Conditional => "conditional",
        FieldRequirednessKind.Never => "never",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled requiredness."),
    };

    private static string ValueConstraintToken(FieldValueConstraintKind kind) => kind switch
    {
        FieldValueConstraintKind.ListConstrained => "list",
        FieldValueConstraintKind.ListSuggested => "suggested",
        FieldValueConstraintKind.Unconstrained => "unconstrained",
        FieldValueConstraintKind.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unhandled constraint."),
    };

    private static string RuleCustomizationToken(RuleCustomization customization) => customization.Kind switch
    {
        RuleCustomizationKind.Custom => "custom",
        RuleCustomizationKind.Inherited => "inherited",
        RuleCustomizationKind.System => "system",
        RuleCustomizationKind.Unknown =>
            customization.Token.Length == 0 ? "unknown" : $"unknown:{customization.Token}",
        _ => throw new ArgumentOutOfRangeException(
            nameof(customization), customization.Kind, "Unhandled customization."),
    };

    private static string DescribeConditions(FieldRequiredness r)
        => string.Join(" OR ", r.Conditions.Select(static c =>
            string.Join(" AND ", c.Clauses.Select(static cl =>
                cl.Value is null
                    ? $"{cl.ConditionType} {cl.Field}"
                    : $"{cl.ConditionType} {cl.Field} = {cl.Value}"))));

    private static IEnumerable<string> SplitTrim(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            yield break;

        foreach (var raw in csv.Split(FilterSeparator))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length > 0)
                yield return trimmed;
        }
    }

    /// <summary>One warning row in the compact envelope.</summary>
    private sealed record CompactWarning(string Code, string Subject, string Detail);

    /// <summary>Rolls per-section coverage up to a single envelope-level answer.</summary>
    private sealed class ScopeAccumulator
    {
        public int FieldRowsEmitted { get; private set; }
        public int RequirementRowsEmitted { get; private set; }
        public bool AnyPartial { get; private set; }

        public void RegisterFields(int emitted, bool partial)
        {
            FieldRowsEmitted += emitted;
            AnyPartial |= partial;
        }

        public void RegisterRequirements(int emitted, bool partial)
        {
            RequirementRowsEmitted += emitted;
            AnyPartial |= partial;
        }
    }
}
