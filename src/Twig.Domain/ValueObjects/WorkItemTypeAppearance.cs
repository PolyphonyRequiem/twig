namespace Twig.Domain.ValueObjects;

/// <summary>
/// Appearance metadata (color and icon) for an Azure DevOps work item type.
/// Declared as <c>sealed record</c> (reference type) rather than <c>readonly record struct</c>
/// because instances are stored in collections and <see cref="IconId"/> is nullable — using a
/// reference type avoids unnecessary boxing and keeps null semantics straightforward.
/// <para><see cref="Color"/> is nullable because the ADO API may omit the color field
/// for disabled or custom work item types. <see cref="IIterationService.GetWorkItemTypeAppearancesAsync"/>
/// guarantees non-null <see cref="Color"/> in all returned instances (null-color entries are filtered out),
/// but the property remains nullable to allow direct construction with unknown color in other contexts.</para>
/// </summary>
public sealed record WorkItemTypeAppearance(string Name, string? Color, string? IconId);
