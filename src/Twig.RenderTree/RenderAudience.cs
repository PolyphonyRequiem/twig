namespace Twig.RenderTree;

/// <summary>
/// Visibility hint for <see cref="DocumentField"/>. Tells renderers which
/// audience the field is intended for so machine-format renderers can skip
/// human-only presentation fields and the human renderer can skip
/// machine-only structured payloads.
/// </summary>
/// <remarks>
/// Symmetric to <see cref="RenderNode.Hint"/>, which encodes "human-only" at
/// the node level. <see cref="RenderAudience"/> encodes audience at the
/// document-field level, where named structured payloads exist alongside
/// human display lines under a single semantic tree.
/// </remarks>
public enum RenderAudience
{
    /// <summary>Visible to every renderer. The default.</summary>
    All = 0,

    /// <summary>Visible only to the human renderer; skipped by JSON / minimal / ids.</summary>
    HumanOnly = 1,

    /// <summary>Visible only to machine renderers (JSON / minimal / ids); skipped by the human renderer.</summary>
    MachineOnly = 2,
}
