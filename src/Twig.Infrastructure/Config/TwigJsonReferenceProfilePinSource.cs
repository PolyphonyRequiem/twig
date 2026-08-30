using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Config;

/// <summary>
/// Reads the three-field reference-profile pin (T1 AB#732 §5.1) off the
/// checked-in <c>twig.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// A block that is present but hand-clipped — any of the three fields blank —
/// is reported as ABSENT rather than as a partial pin. The alternative would be
/// to compare the fields that happen to be filled, which is exactly the "subset
/// match" T1 §8.2 rejects: a pin that agrees on two of three axes and is silent
/// on the third asserts a coupling it has not established. Reporting absence
/// routes it to <c>twig-json-profile-block-missing</c>, whose recovery hint —
/// re-run <c>twig init</c> or hand-write the pin per T1 §5 — is the correct
/// advice for a truncated block as much as for a missing one.
/// </para>
/// </remarks>
internal sealed class TwigJsonReferenceProfilePinSource(TwigConfiguration config)
    : IReferenceProfilePinSource
{
    private readonly TwigConfiguration _config = config;

    public ReferenceProfilePin? GetPin()
    {
        var pin = _config.Profile;
        if (pin is null
            || string.IsNullOrWhiteSpace(pin.Identity)
            || string.IsNullOrWhiteSpace(pin.ProfileVersion)
            || string.IsNullOrWhiteSpace(pin.BaseProcessVersion))
        {
            return null;
        }

        return new ReferenceProfilePin(pin.Identity, pin.ProfileVersion, pin.BaseProcessVersion);
    }
}
