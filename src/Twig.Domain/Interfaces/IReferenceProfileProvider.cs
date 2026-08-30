using Twig.Domain.Common;
using Twig.Domain.ValueObjects;

namespace Twig.Domain.Interfaces;

/// <summary>
/// The T3 profile-lookup seam (AB#734). Single owning service behind which Twig
/// core discovers work-item-type identity, backlog-level assignment, state
/// category, link-kind meaning, and primary-scope eligibility.
/// </summary>
/// <remarks>
/// <para>
/// Shape is fixed by the T1 note (AB#732) §8.1. Every query on this interface
/// maps 1:1 to a T1 §3 field on the profile document.
/// </para>
/// <para>
/// Cardinality is one loaded profile per process. Providers cache the loaded
/// document and return the same aggregate on every call.
/// </para>
/// <para>
/// <b>What this seam replaces:</b> the scattered use of hardcoded WIT / state /
/// link-kind / field strings sprinkled through Twig core. Anything that asks
/// "what does the reference profile say?" resolves here.
/// </para>
/// <para>
/// <b>What this seam does NOT replace:</b>
/// <see cref="IProcessConfigurationProvider"/> — that answers a different
/// question ("what is the LIVE process shaped like right now?"). This provider
/// is the reference, that one is the live discovery. The two are compared
/// against each other by <see cref="ValidateAgainstLiveProcess"/>.
/// </para>
/// </remarks>
public interface IReferenceProfileProvider
{
    /// <summary>
    /// Loads and validates the embedded reference profile against the load-time
    /// checks enumerated in T1 §7.1 / §6.1 / §6.5 / §6.6.
    /// </summary>
    /// <returns>
    /// A <see cref="Result{ReferenceProfile}"/> whose <see cref="Result{T}.Error"/>
    /// on failure is one of the named identifiers in
    /// <see cref="ReferenceProfileErrors"/>. Repeated calls return the same
    /// cached result.
    /// </returns>
    Result<ReferenceProfile> Load();

    /// <summary>
    /// Runs the T1 §6.1 pin match: the repository's checked-in
    /// <c>twig.json.profile</c> block against the embedded profile. Load-time —
    /// no ADO call is made.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT folded into <see cref="Load"/>. <see cref="Load"/> asks
    /// "is the shipped blob intact?", this asks "does this repository agree with
    /// this binary?" — two different failures with two different repairs, and
    /// collapsing them would report a config drift as a corrupt install. Keeping
    /// them apart is also what lets <c>twig init</c> call <see cref="Load"/> to
    /// materialize a pin it could not yet have satisfied.
    /// </remarks>
    /// <returns>
    /// <c>Ok</c> when all three fields match byte-equal; otherwise a failure
    /// carrying <c>twig-json-profile-block-missing</c>,
    /// <c>profile-identity-unknown</c>, <c>profile-version-mismatch</c>, or
    /// <c>base-process-version-mismatch</c>.
    /// </returns>
    Result ValidatePin();

    /// <summary>
    /// Runs the command-time compatibility checks (T1 §7.2) against a live
    /// process. Requires a working <see cref="IProcessConfigurationProvider"/>.
    /// Fails fast on the first mismatch with the named identifier on
    /// <see cref="Result.Error"/>.
    /// </summary>
    /// <param name="liveProcess">The live process shape discovery.</param>
    /// <param name="liveBaseProcessRef">
    /// The live process's parent (base) process reference, compared byte-equal
    /// against the profile's <c>baseProcess.parentRef</c> per T1 §6.2.
    /// <para>
    /// 🔴 A required parameter, and that is a deliberate correction to T1 §8.1.
    /// That section declared this method taking only an
    /// <see cref="IProcessConfigurationProvider"/> while §6.2 required a
    /// parent-process comparison, on the stated premise that the value was
    /// "already reachable via <c>AdoProcessConfigurationResponse</c>". It is
    /// not: that DTO carries backlog categories only, nothing in the codebase
    /// reads <c>parentProcessTypeId</c>, and T1 §8.3 forbids adding the field
    /// to <see cref="Twig.Domain.Aggregates.ProcessConfiguration"/>. The result
    /// was that <c>base-process-parent-mismatch</c> was declared but
    /// unreachable by any code path (AB#735). Taking the reference as data from
    /// the caller that fetched it satisfies §6.2 without changing the process
    /// discovery aggregate, and being required rather than optional means a
    /// caller cannot silently skip the check.
    /// </para>
    /// </param>
    Result ValidateAgainstLiveProcess(IProcessConfigurationProvider liveProcess, string liveBaseProcessRef);

    /// <summary>
    /// Computes the T1 §7.3 live-process structural fingerprint from
    /// <paramref name="liveProcess"/>, using the profile's declared role order,
    /// declared type-name bindings, and the four link-kind rows. Twig core does
    /// not otherwise expose the raw ADO reference names as strings.
    /// </summary>
    /// <param name="liveProcess">The live process shape discovery.</param>
    /// <param name="liveBaseProcessRef">
    /// The live parent-process reference, which is the fingerprint's first
    /// canonical component per T1 §7.3. Supplying the live value rather than
    /// echoing the profile's own is what makes that component discriminating —
    /// echoing it put the same bytes on both sides of the comparison, so the
    /// backstop was structurally blind to the one dimension §6.2 covers.
    /// </param>
    string ComputeLiveFingerprint(IProcessConfigurationProvider liveProcess, string liveBaseProcessRef);
}
