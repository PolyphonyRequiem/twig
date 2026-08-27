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
    internal static readonly char[] Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();
    private readonly TimeProvider _clock;

    public UlidClaimIdGenerator(TimeProvider clock)
    {
        _clock = clock;
    }

    public string NewClaimId()
    {
        var timestamp = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        Span<byte> randomBytes = stackalloc byte[10];
        RandomNumberGenerator.Fill(randomBytes);
        return Encode(timestamp, randomBytes);
    }

    /// <summary>Test seam: encode a caller-supplied 48-bit timestamp + 80-bit
    /// random payload. Ensures every random bit round-trips into an output
    /// character — the mutation tests flip one bit at a time and observe
    /// exactly one character changes.</summary>
    internal static string Encode(long timestamp, ReadOnlySpan<byte> randomBytes)
    {
        if (randomBytes.Length != 10)
            throw new ArgumentException("random payload must be exactly 10 bytes (80 bits).", nameof(randomBytes));

        Span<char> chars = stackalloc char[26];
        // Encode 48-bit timestamp into the first 10 chars, big-endian.
        var ts = timestamp;
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(ts & 0x1F)];
            ts >>= 5;
        }

        // Treat the 80-bit random payload as a single big-endian integer split
        // across a 16-bit `high` and a 64-bit `low`. Emit 16 base32 chars from
        // LSB → MSB, shifting the whole 80-bit register right by 5 bits each
        // iteration — the earlier implementation only carried `high` into
        // `low` when `low` happened to zero out, which for typical random
        // payloads left the top 16 bits stuck in `high` and produced a
        // deterministic trailing-zero character in the low-order slot.
        ulong low = 0;
        for (var i = 2; i < 10; i++) low = (low << 8) | randomBytes[i];
        ulong high = ((ulong)randomBytes[0] << 8) | randomBytes[1];
        for (var i = 25; i >= 10; i--)
        {
            chars[i] = Alphabet[(int)(low & 0x1F)];
            // Right-shift the 80-bit register by 5 bits, carrying the low
            // 5 bits of `high` into the top of `low`. `low` has 64 bits →
            // after (low >> 5) bit 58 is the highest occupied bit; place
            // `high`'s low 5 bits at bits 63..59 with `<< 59`.
            low = (low >> 5) | (high << 59);
            high >>= 5;
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
