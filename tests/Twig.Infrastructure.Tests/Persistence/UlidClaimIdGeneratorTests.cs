using Shouldly;
using Twig.Infrastructure.Persistence;
using Xunit;

namespace Twig.Infrastructure.Tests.Persistence;

/// <summary>
/// Behavioural tests for <see cref="UlidClaimIdGenerator"/>. AB#737
/// §Canonical identifier requires every one of the 80 random bits to
/// contribute to the encoded output; the earlier implementation carried
/// the high 16 bits only when the low 64 bits happened to zero, which
/// left the top slot deterministically stuck. These tests flip one random
/// bit at a time and observe the encoded low-half changes.
/// </summary>
public sealed class UlidClaimIdGeneratorTests
{
    [Fact]
    public void Encode_produces_26_char_output_over_the_crockford_base32_alphabet()
    {
        var random = new byte[10];
        for (var i = 0; i < 10; i++) random[i] = (byte)(0xAA ^ (i * 7));
        var id = UlidClaimIdGenerator.Encode(timestamp: 0x0123456789ABL, random);
        id.Length.ShouldBe(26);
        foreach (var c in id)
            UlidClaimIdGenerator.Alphabet.ShouldContain(c);
    }

    [Fact]
    public void Every_random_bit_affects_at_least_one_output_character()
    {
        var baseline = new byte[10];
        var baseId = UlidClaimIdGenerator.Encode(0, baseline);

        for (var byteIx = 0; byteIx < 10; byteIx++)
        {
            for (var bitIx = 0; bitIx < 8; bitIx++)
            {
                var mutated = (byte[])baseline.Clone();
                mutated[byteIx] ^= (byte)(1 << bitIx);
                var mutatedId = UlidClaimIdGenerator.Encode(0, mutated);
                mutatedId.ShouldNotBe(baseId,
                    $"flipping bit {bitIx} of byte {byteIx} must change the encoded id");
            }
        }
    }

    [Fact]
    public void The_top_random_bits_no_longer_produce_a_deterministic_trailing_char()
    {
        // Set only high bits of the top random byte — the earlier
        // implementation's carry bug would leave the low chars stuck at '0'.
        // With the corrected shift the top-bit change flips the char at
        // index 10 (the highest-order random slot).
        var lowSet = new byte[10];
        var highSet = new byte[10];
        highSet[0] = 0x80;

        var lowId = UlidClaimIdGenerator.Encode(0, lowSet);
        var highId = UlidClaimIdGenerator.Encode(0, highSet);

        lowId.ShouldNotBe(highId);
        // The differing character MUST be in the top random slot (index 10)
        // — not in the trailing slot (index 25) as the buggy carry did.
        highId[10].ShouldNotBe(lowId[10]);
    }

    [Fact]
    public void Two_calls_in_the_same_millisecond_yield_different_ids_because_of_the_random_low_half()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        var gen = new UlidClaimIdGenerator(clock);
        var a = gen.NewClaimId();
        var b = gen.NewClaimId();
        a.ShouldNotBe(b);
    }

    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
