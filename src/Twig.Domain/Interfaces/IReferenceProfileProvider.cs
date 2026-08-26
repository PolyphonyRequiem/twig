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
    /// Runs the command-time compatibility checks (T1 §7.2) against a live
    /// process. Requires a working <see cref="IProcessConfigurationProvider"/>.
    /// Fails fast on the first mismatch with the named identifier on
    /// <see cref="Result.Error"/>.
    /// </summary>
    Result ValidateAgainstLiveProcess(IProcessConfigurationProvider liveProcess);

    /// <summary>
    /// Computes the T1 §7.3 live-process structural fingerprint from
    /// <paramref name="liveProcess"/>, using the profile's declared role order,
    /// declared type-name bindings, and the four link-kind rows. Twig core does
    /// not otherwise expose the raw ADO reference names as strings.
    /// </summary>
    string ComputeLiveFingerprint(IProcessConfigurationProvider liveProcess);
}
