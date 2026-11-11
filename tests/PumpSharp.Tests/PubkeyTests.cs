using PumpSharp.Chain;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// The address type and its base58 codec.
///
/// The codec is implemented in this library (see <see cref="Base58"/>), so
/// the tests check that it agrees with known real addresses, that it
/// round-trips including leading zeroes, and that malformed input is
/// refused without throwing.
/// </summary>
public class PubkeyTests
{
    [Theory]
    [InlineData(PumpProgramIds.Pump)]
    [InlineData(PumpProgramIds.PumpSwap)]
    [InlineData("11111111111111111111111111111111")]
    [InlineData("So11111111111111111111111111111111111111112")]
    [InlineData("TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA")]
    public void A_real_address_round_trips(string base58)
    {
        Assert.True(Pubkey.TryParse(base58, out var pubkey));
        Assert.Equal(base58, pubkey.ToString());
    }

    [Fact]
    public void Arbitrary_bytes_round_trip_through_base58()
    {
        // Fixed seed, so a failure can be reproduced. A base58 bug would most
        // likely show up on one value in a few thousand.
        var random = new Random(20261026);
        var bytes = new byte[Pubkey.Length];

        for (var i = 0; i < 2_000; i++)
        {
            random.NextBytes(bytes);
            var original = new Pubkey(bytes);

            Assert.True(Pubkey.TryParse(original.ToString(), out var parsed));
            Assert.Equal(original, parsed);
        }
    }

    [Fact]
    public void A_leading_zero_byte_survives_the_round_trip()
    {
        // Leading zero bytes have no numeric value, so base58 writes each as a
        // literal '1'. Getting that wrong shortens the address. Leading zero bytes
        // are common: the system program's address is all zeroes.
        var bytes = new byte[Pubkey.Length];
        bytes[31] = 7;

        var pubkey = new Pubkey(bytes);
        var text = pubkey.ToString();

        Assert.StartsWith("1111111111111111111111111111111", text, StringComparison.Ordinal);
        Assert.True(Pubkey.TryParse(text, out var parsed));
        Assert.Equal(pubkey, parsed);
    }

    [Fact]
    public void The_all_zero_address_is_the_default()
    {
        var zeroes = new Pubkey(new byte[Pubkey.Length]);

        Assert.True(zeroes.IsDefault);
        Assert.True(Pubkey.Default.IsDefault);
        Assert.Equal(Pubkey.Default, zeroes);
    }

    [Fact]
    public void Byte_order_is_preserved_exactly()
    {
        // The four-word storage must not reorder bytes.
        var bytes = new byte[Pubkey.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i + 1);
        }

        Span<byte> written = stackalloc byte[Pubkey.Length];
        new Pubkey(bytes).WriteTo(written);

        Assert.True(written.SequenceEqual(bytes));
    }

    [Fact]
    public void One_differing_byte_makes_a_different_address()
    {
        var left = new byte[Pubkey.Length];
        var right = new byte[Pubkey.Length];
        right[17] = 1;

        Assert.NotEqual(new Pubkey(left), new Pubkey(right));
        Assert.True(new Pubkey(left) != new Pubkey(right));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0OIl")]
    [InlineData("not+valid/base58")]
    [InlineData("abc")]
    public void Malformed_text_is_refused_rather_than_throwing(string text) =>
        Assert.False(Pubkey.TryParse(text, out _));

    [Fact]
    public void A_signature_length_value_is_refused()
    {
        // 64 bytes of base58 is valid base58 but not an address. Accepting it
        // would let a signature be used where a mint belongs.
        var sixtyFour = new byte[64];
        new Random(1).NextBytes(sixtyFour);

        var text = Base58.Encode(sixtyFour);
        Assert.NotNull(text);
        Assert.False(Pubkey.TryParse(text, out _));
    }

    [Fact]
    public void Over_long_input_is_refused_before_any_work()
    {
        var hostile = new string('z', Base58.MaxLength + 1);

        Assert.False(Pubkey.TryParse(hostile, out _));
        Assert.False(Base58.TryDecode(hostile, stackalloc byte[Pubkey.Length], out _));
    }

    [Fact]
    public void Encoding_refuses_more_bytes_than_it_will_handle() =>
        Assert.Null(Base58.Encode(new byte[Base58.MaxLength + 1]));

    [Fact]
    public void Encoding_nothing_yields_nothing() =>
        Assert.Equal(string.Empty, Base58.Encode([]));

    [Fact]
    public void A_short_destination_is_refused_rather_than_overflowing()
    {
        Span<byte> tooSmall = stackalloc byte[8];
        Assert.False(Base58.TryDecode(PumpProgramIds.Pump, tooSmall, out _));
    }

    [Fact]
    public void Constructing_from_the_wrong_length_throws()
    {
        // The constructor throws instead of returning false. Its callers are
        // decoders that have already checked bounds, so a wrong length there is a
        // bug, not bad input.
        Assert.Throws<ArgumentException>(() => new Pubkey(new byte[31]));
        Assert.Throws<ArgumentException>(() => new Pubkey(new byte[33]));
    }

    // Ordering.
    //
    // Pubkey is comparable so collections keyed by address can be serialised
    // in a stable order, for example before hashing them. The order must be
    // the same on every run and machine, so it must depend only on the 32
    // bytes.

    [Fact]
    public void Ordering_agrees_with_comparing_the_thirty_two_bytes()
    {
        // Fixed seed, so a failure can be reproduced. An ordering bug would most
        // likely show up on one pair in a few thousand.
        var random = new Random(20261027);
        var leftBytes = new byte[Pubkey.Length];
        var rightBytes = new byte[Pubkey.Length];

        for (var i = 0; i < 2_000; i++)
        {
            random.NextBytes(leftBytes);
            random.NextBytes(rightBytes);

            var expected = Math.Sign(((ReadOnlySpan<byte>)leftBytes).SequenceCompareTo(rightBytes));
            var actual = Math.Sign(new Pubkey(leftBytes).CompareTo(new Pubkey(rightBytes)));

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Every_word_boundary_is_compared()
    {
        // A differing byte at each end of each word, so a comparison that skipped
        // one of the four words would report equality for a pair that differs.
        foreach (var position in (int[])[0, 7, 8, 15, 16, 23, 24, 31])
        {
            var lower = new byte[Pubkey.Length];
            var higher = new byte[Pubkey.Length];
            higher[position] = 0x01;

            Assert.True(new Pubkey(lower) < new Pubkey(higher));
            Assert.True(new Pubkey(higher) > new Pubkey(lower));
        }
    }

    [Fact]
    public void Ordering_is_consistent_with_equality()
    {
        Assert.True(Pubkey.TryParse(PumpProgramIds.Pump, out var pump));
        Assert.True(Pubkey.TryParse(PumpProgramIds.Pump, out var same));

        Assert.Equal(0, pump.CompareTo(same));
        Assert.True(pump <= same);
        Assert.True(pump >= same);
        Assert.False(pump < same);
        Assert.False(pump > same);

        // The default address is all zero bytes, so nothing can sort below it.
        Assert.True(Pubkey.Default <= pump);
    }
}
