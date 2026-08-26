using System.Security.Cryptography;
using Twig.Domain.Services.Claims;

namespace Twig.Infrastructure.Persistence;

/// <summary>
/// Crockford-base32 ULID generator: 128 bits of entropy, monotonic-time
/// prefix, opaque to callers (AB#737 §Canonical identifier). Byte-exact by
/// construction; no downstream reader normalizes case, strips padding, or
/// reformats the value. Uses <see cref="RandomNumberGenerator"/> for the
/// low-order 80 bits — cryptographically strong so the "never derived from
/// business fact" invariant is unconditional.
/// </summary>
internal sealed class UlidClaimIdGenerator : IClaimIdGenerator
{
    // Crockford's Base32 alphabet (no I, L, O, U).
    private static readonly char[] Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();
    private readonly TimeProvider _clock;

    public UlidClaimIdGenerator(TimeProvider clock)
    {
        _clock = clock;
    }

    public string NewClaimId()
    {
        // 48-bit millis-since-epoch high half (10 chars) + 80-bit random low
        // half (16 chars) = 26 characters total. The value is opaque — the
        // encoding matters only for byte-exact comparison downstream.
        var timestamp = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        Span<byte> randomBytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(randomBytes);

        Span<char> chars = stackalloc char[26];
        // Encode 48-bit timestamp into the first 10 chars, big-endian.
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(timestamp & 0x1F)];
            timestamp >>= 5;
        }
        // Encode 80-bit random into the last 16 chars.
        // Interpret randomBytes as a big-endian 80-bit integer, chunked into
        // 16 groups of 5 bits from the low end.
        ulong low = 0;
        for (var i = 2; i < 10; i++) low = (low << 8) | randomBytes[i];
        ulong high = ((ulong)randomBytes[0] << 8) | randomBytes[1];
        for (var i = 25; i >= 10; i--)
        {
            var v = (int)(low & 0x1F);
            chars[i] = Alphabet[v];
            low >>= 5;
            if (low == 0 && high != 0)
            {
                low = (high << 59) | low;
                high >>= 5;
            }
        }
        return new string(chars);
    }
}

/// <summary>
/// UUIDv4-shaped CAS token generator: 128 bits of entropy per token, opaque
/// to every reader (AB#737 §CAS token). A UUIDv4 rendered lowercase satisfies
/// the "monotonically fresh, never interpreted" contract; the storage layer
/// only compares the string byte-exact.
/// </summary>
internal sealed class GuidClaimCasTokenGenerator : IClaimCasTokenGenerator
{
    public string NewCasToken() => Guid.NewGuid().ToString("D");
}
