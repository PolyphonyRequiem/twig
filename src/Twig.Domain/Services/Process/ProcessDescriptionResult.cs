using Twig.Domain.ValueObjects;

namespace Twig.Domain.Services.Process;

/// <summary>
/// The process could not be resolved: this project does not map to an ADO process.
/// </summary>
/// <remarks>
/// 🔴 A CONFIGURATION outcome, and the reason this is its own arm rather than sharing one with
/// <see cref="ProcessTypesUnfetchable"/>. The remedies differ: this one is fixed by pointing the
/// workspace at a project that has a process, the other by retrying or re-authenticating. Before
/// AB#244 both arrived as <c>null</c> and the command collapsed them into one message, so neither
/// a human nor a script could tell which of the two remedies applied.
/// </remarks>
internal sealed record ProcessIdentityUnresolved;

/// <summary>
/// The process resolved, but its work item type list could not be fetched.
/// </summary>
/// <remarks>
/// A TRANSIENT or AUTH outcome — the route did not answer. Deliberately not an empty document:
/// "could not ask" is not "has nothing", the same distinction the per-type <c>unfetched</c> list
/// keeps at the level below.
/// </remarks>
internal sealed record ProcessTypesUnfetchable;

/// <summary>
/// A caller named a type the process does not have.
/// </summary>
/// <remarks>
/// 🔴 Travels as a union arm rather than as a thrown exception (AB#244). It is an ordinary,
/// expected outcome of a caller-supplied name, not an exceptional one — and as an exception it
/// was invisible in the assembler's signature, so a caller could omit the <c>catch</c> and find
/// out at run time. A hard error still: rendering an empty document for a type that does not
/// exist would let a script bank a file saying "this process has nothing" when the truth is
/// "you asked for something that is not here".
/// </remarks>
internal sealed record ProcessDescriptionTypeNotFound(string TypeReferenceName);

/// <summary>The description was assembled.</summary>
internal sealed record ProcessDescriptionAssembled(ProcessDescription Description);

/// <summary>
/// The outcome of <see cref="ProcessDescriptionAssembler.AssembleAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// 🔴 A Tier 1 discriminated union per <c>docs/architecture/result-type-conventions.md</c>
/// ("2+ outcomes with different fields → Tier 1 Discriminated Union"), replacing a
/// null-plus-exception signature that encoded three outcomes through two mechanisms and named
/// only one of them in the type. That shape hit the document's own "Nullable fields as state
/// encoding" anti-pattern at the return position: two of the three outcomes were the SAME
/// <c>null</c>, so the type made them indistinguishable and every caller collapsed them.
/// </para>
/// <para>
/// Pattern-match the case (<c>result is ProcessDescriptionAssembled a</c>). Per the conventions
/// doc, switches over this union carry a <c>default</c> arm throwing
/// <see cref="System.Diagnostics.UnreachableException"/>.
/// </para>
/// </remarks>
internal union ProcessDescriptionResult(
    ProcessDescriptionAssembled,
    ProcessIdentityUnresolved,
    ProcessTypesUnfetchable,
    ProcessDescriptionTypeNotFound);
