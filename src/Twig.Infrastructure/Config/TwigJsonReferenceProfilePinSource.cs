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
/// stays as a pin. Only a truly absent block returns <c>null</c>; preserving the
/// incomplete shape is what lets <see cref="Twig.Domain.Interfaces.IReferenceProfileProvider.ValidatePin"/>
/// distinguish malformed from absent and fail closed on the former.
/// </para>
/// </remarks>
internal sealed class TwigJsonReferenceProfilePinSource(TwigConfiguration config)
    : IReferenceProfilePinSource
{
    private readonly TwigConfiguration _config = config;

    public ReferenceProfilePin? GetPin()
    {
        var pin = _config.Profile;
        if (pin is null)
            return null;

        return new ReferenceProfilePin(pin.Identity, pin.ProfileVersion, pin.BaseProcessVersion);
    }
}
