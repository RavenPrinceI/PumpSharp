using PumpSharp.Chain;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// Decoding the <c>BondingCurve</c> account.
///
/// Checks that the layout read is the layout the IDL declares, field by
/// field and in order, and that every kind of mismatch is reported. This is
/// the same standard as <see cref="PumpEventDecoderTests"/>: the decoder
/// agrees with its IDL or declines. Whether the values mean what Pump says
/// is for the parity suites.
///
/// One difference from events: trailing bytes. An event payload is exactly a
/// serialised struct, so extra bytes are refused. An account's length is
/// its allocated space, which a program may oversize, so extra bytes are
/// counted instead. Too few bytes is refused in both.
/// </summary>
public class PumpAccountDecoderTests
{
    private static ulong BondingCurveDiscriminator =>
        PumpIdl.Pump.Accounts
            .First(entry => entry.Value == PumpAccountDecoder.BondingCurveAccountName)
            .Key;

    private static Pubkey Key(byte seed)
    {
        Span<byte> bytes = stackalloc byte[Pubkey.Length];
        bytes.Fill(seed);
        return new Pubkey(bytes);
    }

    /// <summary>
    /// Writes a <c>BondingCurve</c> in IDL field order, by hand. A writer
    /// generated from the reader's own schema would agree with the reader even
    /// if both were wrong.
    /// </summary>
    private static BorshTestWriter Curve(
        ulong virtualToken = 1_000_000_000_000,
        ulong virtualQuote = 30_000_000_000,
        ulong realToken = 800_000_000_000,
        ulong realQuote = 5_000_000_000,
        bool complete = false,
        Pubkey? creator = null,
        bool mayhem = false,
        Pubkey? quoteMint = null,
        ulong initialVirtualQuote = 30_000_000_000) =>
        new BorshTestWriter()
            .Discriminator(BondingCurveDiscriminator)
            .U64(virtualToken)
            .U64(virtualQuote)
            .U64(realToken)
            .U64(realQuote)
            .U64(1_000_000_000_000_000)   // token_total_supply
            .Bool(complete)
            .Key(creator ?? Key(0x7a))
            .Bool(mayhem)                  // is_mayhem_mode
            .Bool(false)                   // is_cashback_coin
            .Key(quoteMint ?? Pubkey.Default)
            .U64(100)                      // creator_fee_bps
            .Bool(true)                    // can_edit_creator_fee
            .Bool(false)                   // is_holder_reward
            .U64(12_345)                   // creator_fee
            .U64(67_890)                   // protocol_fees
            .U8(3)                         // depth
            .U64(initialVirtualQuote)
            .U64(0)                        // post_complete_base_out
            .U64(0);                       // post_complete_quote_in

    [Fact]
    public void A_well_formed_account_decodes_every_field()
    {
        var creator = Key(0x11);
        var bytes = Curve(creator: creator).ToArray();

        var failure = PumpAccountDecoder.DecodeBondingCurve(bytes, out var curve, out var trailing);

        Assert.Equal(PumpAccountDecodeFailure.None, failure);
        Assert.Equal(0, trailing);
        Assert.Equal(1_000_000_000_000ul, curve.VirtualTokenReserves);
        Assert.Equal(30_000_000_000ul, curve.VirtualQuoteReserves);
        Assert.Equal(800_000_000_000ul, curve.RealTokenReserves);
        Assert.Equal(5_000_000_000ul, curve.RealQuoteReserves);
        Assert.Equal(creator, curve.Creator);
        Assert.False(curve.Complete);
        Assert.False(curve.IsMayhemMode);
        Assert.Equal(100ul, curve.CreatorFeeBasisPoints);
        Assert.True(curve.CanEditCreatorFee);
        Assert.Equal(12_345ul, curve.CreatorFee);
        Assert.Equal(67_890ul, curve.ProtocolFees);
        Assert.Equal(3, curve.Depth);
        Assert.Equal(30_000_000_000ul, curve.InitialVirtualQuoteReserves);
    }

    [Fact]
    public void The_layout_is_exactly_as_long_as_the_idl_declares()
    {
        // 8 discriminator bytes plus 158 bytes of fields. If a field is added,
        // removed or resized without this number changing, the code and the IDL
        // no longer match.
        Assert.Equal(166, Curve().ToArray().Length);
    }

    [Fact]
    public void Trailing_bytes_are_counted_rather_than_refused()
    {
        // An account's length is its allocated space, so an account with room for
        // a future field is normal and must still decode.
        var bytes = Curve().ToArray().Concat(new byte[24]).ToArray();

        var failure = PumpAccountDecoder.DecodeBondingCurve(bytes, out var curve, out var trailing);

        Assert.Equal(PumpAccountDecodeFailure.None, failure);
        Assert.Equal(24, trailing);
        Assert.Equal(1_000_000_000_000ul, curve.VirtualTokenReserves);
    }

    [Fact]
    public void A_short_account_is_truncated_not_decoded_from_its_prefix()
    {
        var bytes = Curve().ToArray()[..100];

        Assert.Equal(
            PumpAccountDecodeFailure.Truncated,
            PumpAccountDecoder.DecodeBondingCurve(bytes, out _, out _));
    }

    [Fact]
    public void An_empty_account_is_truncated()
    {
        Assert.Equal(
            PumpAccountDecodeFailure.Truncated,
            PumpAccountDecoder.DecodeBondingCurve([], out _, out _));
    }

    [Fact]
    public void An_undeclared_discriminator_is_the_drift_signal_not_a_decode_error()
    {
        var bytes = Curve().ToArray();
        bytes[0] ^= 0xff;

        Assert.Equal(
            PumpAccountDecodeFailure.UnknownDiscriminator,
            PumpAccountDecoder.DecodeBondingCurve(bytes, out _, out _));
    }

    [Fact]
    public void A_different_declared_account_is_refused_by_name()
    {
        var other = PumpIdl.Pump.Accounts
            .First(entry => entry.Value != PumpAccountDecoder.BondingCurveAccountName)
            .Key;

        var bytes = new BorshTestWriter().Discriminator(other).U64(1).ToArray();

        Assert.Equal(
            PumpAccountDecodeFailure.WrongAccount,
            PumpAccountDecoder.DecodeBondingCurve(bytes, out _, out _));
    }

    [Fact]
    public void A_bool_outside_zero_and_one_is_malformed_rather_than_coerced()
    {
        var bytes = Curve().ToArray();

        // `complete` sits immediately after five u64 fields.
        bytes[8 + (5 * 8)] = 0x7f;

        Assert.Equal(
            PumpAccountDecodeFailure.MalformedValue,
            PumpAccountDecoder.DecodeBondingCurve(bytes, out _, out _));
    }

    [Fact]
    public void An_oversized_account_is_refused_before_it_is_decoded()
    {
        var bytes = new byte[PumpAccountDecoder.MaxAccountBytes + 1];

        Assert.Equal(
            PumpAccountDecodeFailure.AccountTooLarge,
            PumpAccountDecoder.DecodeBondingCurve(bytes, out _, out _));
    }

    [Fact]
    public void The_reserve_basis_follows_the_quote_mint_exactly_as_a_trade_event_does()
    {
        // A price from the account and a price from an event describe the same
        // curve, so they must use the same basis.
        PumpAccountDecoder.DecodeBondingCurve(Curve().ToArray(), out var sol, out _);
        Assert.Equal(PumpReserveBasis.Sol, sol.Reserves.Basis);

        PumpAccountDecoder.DecodeBondingCurve(
            Curve(quoteMint: Key(0x5c)).ToArray(),
            out var quote,
            out _);

        Assert.Equal(PumpReserveBasis.Quote, quote.Reserves.Basis);
    }

    [Fact]
    public void Curve_progress_is_unknown_without_an_initial_reserve()
    {
        PumpAccountDecoder.DecodeBondingCurve(Curve().ToArray(), out var curve, out _);

        // The account does not carry the initial real token reserve, so without it
        // the caller gets null, not a number.
        Assert.Null(curve.CurveProgressBasisPoints(0));
    }

    [Theory]
    [InlineData(1_000_000_000_000ul, 800_000_000_000ul, 2_000)]
    [InlineData(1_000_000_000_000ul, 1_000_000_000_000ul, 0)]
    [InlineData(1_000_000_000_000ul, 0ul, 10_000)]
    public void Curve_progress_matches_the_accumulator_definition(
        ulong initial,
        ulong remaining,
        int expectedBasisPoints)
    {
        PumpAccountDecoder.DecodeBondingCurve(
            Curve(realToken: remaining).ToArray(),
            out var curve,
            out _);

        Assert.Equal(expectedBasisPoints, curve.CurveProgressBasisPoints(initial));
    }
}
