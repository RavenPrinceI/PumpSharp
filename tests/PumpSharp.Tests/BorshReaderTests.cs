using PumpSharp.Chain;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// The Borsh reader that every Pump decode uses.
///
/// Two properties matter most. Failure is sticky: after an underrun the
/// reader must not go on returning values from the wrong offset, because an
/// event built from a misaligned read looks real. And no length from the
/// payload is trusted, so a hostile <c>vec</c> count is refused, never
/// allocated for.
/// </summary>
public class BorshReaderTests
{
    [Fact]
    public void Primitives_are_read_little_endian_in_order()
    {
        var expectedKey = new Pubkey(Enumerable.Range(1, Pubkey.Length).Select(i => (byte)i).ToArray());

        var payload = new BorshTestWriter()
            .U8(0xAB)
            .U16(0x1234)
            .U32(0xDEADBEEF)
            .U64(0x0123456789ABCDEF)
            .I64(-42)
            .Bool(true)
            .Key(expectedKey)
            .ToArray();

        var reader = new BorshReader(payload);

        Assert.Equal((byte)0xAB, reader.ReadU8());
        Assert.Equal((ushort)0x1234, reader.ReadU16());
        Assert.Equal(0xDEADBEEFU, reader.ReadU32());
        Assert.Equal(0x0123456789ABCDEFUL, reader.ReadU64());
        Assert.Equal(-42L, reader.ReadI64());
        Assert.True(reader.ReadBool());
        Assert.Equal(expectedKey, reader.ReadPubkey());
        Assert.True(reader.ConsumedExactly);
    }

    [Fact]
    public void A_string_round_trips_including_non_ascii()
    {
        // Token names are set by the creator, so the decoder sees whatever bytes a
        // launch put there.
        const string Name = "pümp ✨ トークン";

        var reader = new BorshReader(new BorshTestWriter().Text(Name).ToArray());

        Assert.Equal(Name, reader.ReadString());
        Assert.True(reader.ConsumedExactly);
    }

    [Fact]
    public void Failure_is_sticky_and_freezes_the_position()
    {
        // One u64 present, two requested.
        var reader = new BorshReader(new BorshTestWriter().U64(7).ToArray());

        Assert.Equal(7UL, reader.ReadU64());
        Assert.False(reader.Failed);
        var positionBeforeUnderrun = reader.Position;

        Assert.Equal(0UL, reader.ReadU64());
        Assert.True(reader.Failed);
        Assert.Equal(positionBeforeUnderrun, reader.Position);

        // Every later read does nothing, even ones the remaining bytes could have
        // satisfied. Otherwise a decoder that ignored Failed would continue from
        // the wrong offset and produce a complete-looking event.
        Assert.Equal((byte)0, reader.ReadU8());
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.Equal(Pubkey.Default, reader.ReadPubkey());
        Assert.True(reader.Failed);
        Assert.Equal(positionBeforeUnderrun, reader.Position);
        Assert.False(reader.ConsumedExactly);
    }

    [Fact]
    public void A_bool_outside_zero_and_one_is_malformed()
    {
        // 0x7f where a bool belongs means the reader is misaligned. Treating it as
        // true would carry the error forward.
        var reader = new BorshReader(new BorshTestWriter().RawByte(0x7f).ToArray());

        Assert.False(reader.ReadBool());
        Assert.True(reader.Failed);
    }

    [Fact]
    public void A_string_length_past_the_end_is_refused()
    {
        // Claims 4 GB of text in a 4-byte payload.
        var payload = new BorshTestWriter().U32(uint.MaxValue).ToArray();

        var reader = new BorshReader(payload);
        Assert.Equal(string.Empty, reader.ReadString());
        Assert.True(reader.Failed);
    }

    [Fact]
    public void A_vector_count_that_would_overflow_is_refused()
    {
        // uint.MaxValue * 34 overflows a 32-bit byte count into a small number.
        // The reader multiplies in 64 bits to prevent that.
        var payload = new BorshTestWriter().VecPrefix(uint.MaxValue).U64(1).ToArray();

        var reader = new BorshReader(payload);
        reader.SkipVector(34);

        Assert.True(reader.Failed);
    }

    [Fact]
    public void A_vector_is_skipped_by_its_element_size()
    {
        var address = new Pubkey(new byte[Pubkey.Length]);

        var payload = new BorshTestWriter()
            .VecPrefix(2)
            .Shareholder(address, 100)
            .Shareholder(address, 200)
            .U64(0xFEEDFACE)
            .ToArray();

        var reader = new BorshReader(payload);
        reader.SkipVector(34);

        Assert.Equal(0xFEEDFACEUL, reader.ReadU64());
        Assert.True(reader.ConsumedExactly);
    }

    [Fact]
    public void An_empty_vector_consumes_only_its_count()
    {
        var payload = new BorshTestWriter().VecPrefix(0).U8(9).ToArray();

        var reader = new BorshReader(payload);
        reader.SkipVector(34);

        Assert.Equal((byte)9, reader.ReadU8());
        Assert.True(reader.ConsumedExactly);
    }

    [Fact]
    public void Trailing_bytes_are_visible_as_an_inexact_read()
    {
        var reader = new BorshReader(new BorshTestWriter().U64(1).U8(2).ToArray());

        Assert.Equal(1UL, reader.ReadU64());
        Assert.False(reader.Failed);
        Assert.False(reader.ConsumedExactly);
        Assert.Equal(1, reader.Remaining);
    }

    [Fact]
    public void Arbitrary_bytes_never_throw()
    {
        // Random input. This reader handles data from a public feed, so malformed
        // input must be refused, never thrown.
        var random = new Random(20261026);

        for (var i = 0; i < 5_000; i++)
        {
            var payload = new byte[random.Next(0, 96)];
            random.NextBytes(payload);

            var reader = new BorshReader(payload);

            // A realistic mix of types, not one type repeated.
            _ = reader.ReadU8();
            _ = reader.ReadU64();
            _ = reader.ReadBool();
            _ = reader.ReadPubkey();
            _ = reader.ReadString();
            reader.SkipVector(34);
            reader.SkipString();
            _ = reader.ReadI64();
            _ = reader.ReadU128();
        }
    }

    [Fact]
    public void An_element_size_of_zero_is_a_programming_error()
    {
        // A zero element size can only come from a layout mistake in this library,
        // so it throws instead of silently skipping nothing.
        // Written out instead of Assert.Throws because BorshReader is a ref struct
        // and cannot be captured by a lambda.
        var payload = new BorshTestWriter().VecPrefix(1).ToArray();

        try
        {
            var reader = new BorshReader(payload);
            reader.SkipVector(0);
            Assert.Fail("A zero element size should be refused.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }
}
