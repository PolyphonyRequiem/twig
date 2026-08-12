namespace Twig.Domain.ValueObjects;

/// <summary>
/// One field as it belongs to ONE work item type, from the process-scoped per-type fields
/// route.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>This is not the project-wide field list.</b> The project-wide list
/// (<see cref="FieldDefinition"/>, <c>_apis/wit/fields</c>) is identical for every type in
/// the project — asking about two different types returns the same fields in the same
/// order. Presenting it as a type's field list is untrue about which fields belong to the
/// type, and is the founding correctness defect this shape exists to fix.
/// </para>
/// <para>
/// 🔴 <see cref="RequiredUnconditionally"/> is named for what it actually reports.
/// The per-type fields route carries only <b>unconditional</b> requiredness. A field made
/// mandatory by a rule — <i>when State = Done → makeRequired</i> — reads as
/// <c>false</c> here. A whole-process survey found 59 unconditionally-required fields
/// while the conditionally-required ones were invisible to this route entirely.
/// Conditional requiredness lives on the rules route
/// (<see cref="Twig.Domain.Interfaces.IProcessRuleProvider"/>) and a caller reporting
/// requiredness from this property alone is wrong in the silent direction. The merge is
/// a later ticket's work; the property name is the guard against doing it accidentally.
/// </para>
/// <para>
/// This route carries <b>no</b> <c>allowedValues</c> and no picklist reference at any
/// api-version, with or without <c>$expand=all</c>. A field that looks like a choice list
/// is not known to be constrained from here.
/// </para>
/// <para>
/// Evidence: branch <c>docs/process-descriptor-map</c>,
/// <c>wayfinder-process-descriptor/assets/0001-endpoint-findings.md</c>. Governing
/// ruling: <c>docs/specs/process-description.spec.md</c> Problem Statement,
/// Implementation Decisions 5 and 6.
/// </para>
/// </remarks>
/// <param name="ReferenceName">
/// The stable identity of the field (<c>System.Title</c>, <c>Custom.Foo</c>). Display
/// names lie across processes; this does not.
/// </param>
/// <param name="Name">The display name the web editor shows.</param>
/// <param name="Type">The data type (<c>string</c>, <c>integer</c>, <c>html</c>, …).</param>
/// <param name="DefaultValue">
/// The value the server pre-fills, or <c>null</c> when the field has no default. Most
/// fields have none: 19 of 628 rows carried one on the process surveyed.
/// </param>
/// <param name="RequiredUnconditionally">
/// Whether the field is required with no condition attached. See the remarks — this is
/// not the whole truth about requiredness.
/// </param>
/// <param name="Customization">
/// Whether the field is authored on this type or inherited from the parent process:
/// <c>custom</c>, <c>inherited</c>, or <c>system</c>, carried verbatim from the server.
/// </param>
/// <param name="IsLocked">Whether the field is locked against edits by the process.</param>
/// <param name="Description">The field's description, or empty when the server sends none.</param>
internal sealed record ProcessTypeField(
    string ReferenceName,
    string Name,
    string Type,
    string? DefaultValue,
    bool RequiredUnconditionally,
    string Customization,
    bool IsLocked,
    string Description);
