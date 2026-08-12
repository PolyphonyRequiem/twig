namespace Twig.Domain.ValueObjects;

/// <summary>
/// One state of a work item type, from the process-scoped per-type states route.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="StateEntry"/>, which is the cached project-scoped shape twig's
/// local store keeps for state navigation. This one is process-scoped, carries
/// <see cref="Customization"/> (authored-vs-inherited, which the cached shape does not),
/// and is never written to the local store — a description may describe a FOREIGN process
/// and ingesting it would poison a store scoped to the workspace's own project.
/// </para>
/// <para>
/// Evidence: probed live 2026-08-11 against
/// <c>_apis/work/processes/{id}/workItemTypes/{ref}/states</c>, which answers at GA
/// <c>7.1</c>. See <c>AdoApiVersions.ProcessWorkItemTypeStates</c>.
/// </para>
/// </remarks>
/// <param name="Name">The state's display name, e.g. <c>To do</c>.</param>
/// <param name="StateCategory">
/// The category the state belongs to — <c>Proposed</c>, <c>InProgress</c>,
/// <c>Resolved</c>, <c>Completed</c>, <c>Removed</c>. Carried verbatim.
/// </param>
/// <param name="Order">
/// The server's ordering hint. Carried because it is how the web editor lays states out;
/// it is NOT relied on for the document's ordering, which sorts explicitly.
/// </param>
/// <param name="Color">The state's hex colour, or empty when the server sends none.</param>
/// <param name="Customization">
/// Whether the state is authored on this process or inherited: <c>custom</c>,
/// <c>inherited</c>, or <c>system</c>, verbatim.
/// </param>
/// <param name="IsHidden">Whether the process has hidden an inherited state.</param>
internal sealed record ProcessTypeState(
    string Name,
    string StateCategory,
    int Order,
    string Color,
    string Customization,
    bool IsHidden);

/// <summary>
/// One allowed state transition on a work item type: you may move FROM one state TO
/// another.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>Transitions are NOT derivable from the state list.</b> The obvious shortcut — assume
/// every state reaches every other — is wrong: probed live across 20 types in this org, 4
/// of them are not fully connected, and one of those cannot reach a state it declares.
/// Deriving would report transitions that do not exist, which is the same silent-lie class
/// this feature exists to prevent.
/// </para>
/// <para>
/// 🔴 <b>They are also not available on the modern process API at any version.</b>
/// <c>…/processes/{id}/workItemTypes/{ref}/transitions</c> and <c>/stateTransitions</c>
/// return an HTML 404 (no such controller) at <c>7.1</c>, <c>7.1-preview.1</c> and
/// <c>7.1-preview.2</c>, and the process type list carries no transitions under any
/// <c>$expand</c>. The only source is the classic project-scoped
/// <c>_apis/wit/workitemtypes?$expand=all</c> route, which does return
/// <c>referenceName</c>, so type identity stays reference-name-keyed as the design
/// requires. Probed live 2026-08-11.
/// </para>
/// </remarks>
/// <param name="FromState">
/// The state being left. <b>Empty string means the INITIAL transition</b> — what state a
/// newly created work item enters. The server expresses it as an empty key and the
/// distinction is real, so it is carried rather than dropped.
/// </param>
/// <param name="ToState">The state being entered.</param>
internal sealed record ProcessTypeTransition(string FromState, string ToState);
