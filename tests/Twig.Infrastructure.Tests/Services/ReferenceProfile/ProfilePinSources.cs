using Twig.Domain.Interfaces;
using Twig.Domain.ValueObjects;

namespace Twig.Infrastructure.Tests.Services.ReferenceProfile;

/// <summary>
/// <see cref="IReferenceProfilePinSource"/> stubs for exercising the T1 §6.1
/// pin match.
/// </summary>
/// <remarks>
/// <see cref="Matching"/> carries the values the SHIPPED profile declares. A
/// test that invented its own would prove the comparison runs while saying
/// nothing about whether the released artifact and this repository's pin agree
/// — which is the property the pin exists to assert.
/// </remarks>
internal static class ProfilePinSources
{
    internal const string ShippedIdentity = "twig.reference-profile.hyperbright";
    internal const string ShippedProfileVersion = "1.0.0";
    internal const string ShippedBaseProcessVersion = "basic:2026-08-24:1";

    public static IReferenceProfilePinSource Matching() =>
        new Stub(new ReferenceProfilePin(
            ShippedIdentity, ShippedProfileVersion, ShippedBaseProcessVersion));

    public static IReferenceProfilePinSource Missing() => new Stub(null);

    public static IReferenceProfilePinSource Of(
        string identity, string profileVersion, string baseProcessVersion) =>
        new Stub(new ReferenceProfilePin(identity, profileVersion, baseProcessVersion));

    private sealed class Stub(ReferenceProfilePin? pin) : IReferenceProfilePinSource
    {
        public ReferenceProfilePin? GetPin() => pin;
    }
}
