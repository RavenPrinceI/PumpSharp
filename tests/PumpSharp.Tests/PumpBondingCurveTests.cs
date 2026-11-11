using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// The constant-product quote maths.
///
/// These are not parity tests; those are in <see cref="PumpParityTests"/>.
/// These tests check two things:
///
/// <list type="bullet">
/// <item>Arithmetic: the code computes the stated formula, against values
/// worked out separately in exact integer arithmetic.</item>
/// <item>Invariants: the curve product never falls, a round trip never
/// returns more than it was given, and a quote never exceeds what the curve
/// holds. These hold for any constant-product curve, so they catch sign and
/// rounding errors without an external reference.</item>
/// </list>
///
/// Neither shows that the deployed program behaves the same way. The parity
/// vectors do that.
/// </summary>
public class PumpBondingCurveTests
{
    /// <summary>
    /// A curve with round reserves, so expected values stay readable: 30 SOL
    /// of virtual quote against 1e15 raw token units, with 8e14 of those
    /// actually held.
    /// </summary>
    private static PumpCurveReserves RoundCurve => new()
    {
        VirtualBaseUnits = 1_000_000_000_000_000,
        VirtualQuoteUnits = 30_000_000_000,
        RealBaseUnits = 800_000_000_000_000,
        RealQuoteUnits = 30_000_000_000,
        Basis = PumpReserveBasis.Sol,
    };

    /// <summary>The reserves a real Pump launch opens with.</summary>
    private static PumpCurveReserves RealisticCurve => RoundCurve with
    {
        VirtualBaseUnits = 1_073_000_000_000_000,
        RealBaseUnits = 793_100_000_000_000,
    };

    [Fact]
    public void A_buy_returns_the_constant_product_amount()
    {
        // floor(1e9 * 1e15 / (3e10 + 1e9)) = 32,258,064,516,129, worked out
        // separately in exact integer arithmetic.
        Assert.True(PumpBondingCurve.TryQuoteBuy(RoundCurve, 1_000_000_000, out var quote));

        Assert.Equal(32_258_064_516_129UL, quote.AmountOutUnits);
        Assert.Equal(1_000_000_000UL, quote.AmountInUnits);
        Assert.False(quote.ClampedToRealReserves);
        Assert.Equal(PumpReserveBasis.Sol, quote.Basis);
    }

    [Fact]
    public void A_buy_moves_the_reserves_the_way_the_trade_did()
    {
        Assert.True(PumpBondingCurve.TryQuoteBuy(RoundCurve, 1_000_000_000, out var quote));

        var after = quote.ResultingReserves;
        Assert.Equal(RoundCurve.VirtualQuoteUnits + 1_000_000_000, after.VirtualQuoteUnits);
        Assert.Equal(RoundCurve.VirtualBaseUnits - quote.AmountOutUnits, after.VirtualBaseUnits);
        Assert.Equal(RoundCurve.RealBaseUnits - quote.AmountOutUnits, after.RealBaseUnits);
    }

    [Fact]
    public void The_curve_product_never_falls_across_a_trade()
    {
        // The core invariant. Flooring the output lets the product rise by less
        // than one unit of the divisor. A formula error would push it down, which
        // would hand out free tokens.
        var reserves = RoundCurve;
        var before = PumpBondingCurve.ConstantProduct(reserves);

        for (var i = 0; i < 25; i++)
        {
            Assert.True(PumpBondingCurve.TryQuoteBuy(reserves, 250_000_000, out var quote));
            reserves = quote.ResultingReserves;

            var after = PumpBondingCurve.ConstantProduct(reserves);
            Assert.True(after >= before, $"The curve product fell at trade {i}.");
            before = after;
        }
    }

    [Fact]
    public void Buying_and_selling_back_never_returns_more_than_was_paid()
    {
        // No free money, before fees. If this fails, every backtest built on these
        // quotes looks better than it should. Fees only make a real round trip
        // worse, so the fee-free case is the strictest version.
        ulong[] amounts = [1, 1_000, 1_000_000, 1_000_000_000, 10_000_000_000];

        foreach (var amountIn in amounts)
        {
            Assert.True(PumpBondingCurve.TryQuoteBuy(RoundCurve, amountIn, out var bought));
            Assert.True(PumpBondingCurve.TryQuoteSell(bought.ResultingReserves, bought.AmountOutUnits, out var sold));

            Assert.True(
                sold.AmountOutUnits <= amountIn,
                $"Round trip of {amountIn} returned {sold.AmountOutUnits}.");
        }
    }

    [Fact]
    public void A_one_lamport_round_trip_loses_the_rounding_rather_than_gaining_it()
    {
        // 1 SOL in, 999,999,999 lamports out. The missing lamport comes from the
        // floor on the sell; the trader loses it, nothing creates it.
        Assert.True(PumpBondingCurve.TryQuoteBuy(RoundCurve, 1_000_000_000, out var bought));
        Assert.True(PumpBondingCurve.TryQuoteSell(bought.ResultingReserves, bought.AmountOutUnits, out var sold));

        Assert.Equal(999_999_999UL, sold.AmountOutUnits);
    }

    [Fact]
    public void A_buy_larger_than_the_curve_holds_is_capped_and_says_so()
    {
        // On chain this completes the curve, so the cap is a lifecycle signal.
        Assert.True(PumpBondingCurve.TryQuoteBuy(RoundCurve, 500_000_000_000, out var quote));

        Assert.True(quote.ClampedToRealReserves);
        Assert.Equal(RoundCurve.RealBaseUnits, quote.AmountOutUnits);
    }

    [Fact]
    public void A_sell_the_curve_cannot_settle_reports_the_price_not_a_reduced_payout()
    {
        // The SDK does not clamp a sale to the quote units the curve holds,
        // because on chain such a sale fails rather than paying less. An earlier
        // version of this library clamped, which turned a rejected trade into a
        // plausible small payout. The quote now reports the curve price and sets a
        // flag, and the caller must refuse.
        var nearlyEmpty = RoundCurve with { RealQuoteUnits = 1_000 };

        Assert.True(PumpBondingCurve.TryQuoteSell(nearlyEmpty, 500_000_000_000_000, out var quote));

        Assert.True(quote.ExceedsRealReserves);
        Assert.False(quote.ClampedToRealReserves);
        Assert.True(
            quote.AmountOutUnits > 1_000,
            "the curve price should be reported in full, not reduced to what is held");
    }

    [Fact]
    public void A_sell_the_curve_can_settle_is_not_flagged()
    {
        // The other side of the flag: a trade the curve can pay must not be
        // flagged.
        var funded = RoundCurve with { RealQuoteUnits = ulong.MaxValue / 2 };

        Assert.True(PumpBondingCurve.TryQuoteSell(funded, 1_000_000, out var quote));

        Assert.False(quote.ExceedsRealReserves);
    }

    [Fact]
    public void A_zero_amount_quotes_zero_rather_than_failing()
    {
        Assert.True(PumpBondingCurve.TryQuoteBuy(RoundCurve, 0, out var quote));
        Assert.Equal(0UL, quote.AmountOutUnits);
    }

    [Theory]
    [InlineData(0UL, 30_000_000_000UL)]
    [InlineData(1_000_000_000_000_000UL, 0UL)]
    public void A_curve_with_a_zero_virtual_side_cannot_be_priced(ulong virtualBase, ulong virtualQuote)
    {
        var reserves = RoundCurve with
        {
            VirtualBaseUnits = virtualBase,
            VirtualQuoteUnits = virtualQuote,
        };

        Assert.False(reserves.CanPrice);
        Assert.False(PumpBondingCurve.TryQuoteBuy(reserves, 1_000_000, out _));
        Assert.False(PumpBondingCurve.TryQuoteSell(reserves, 1_000_000, out _));
        Assert.False(PumpBondingCurve.TrySpotPrice(reserves, 6, out _));
    }

    [Fact]
    public void Reserves_with_an_unknown_basis_cannot_be_priced()
    {
        var reserves = RoundCurve with { Basis = PumpReserveBasis.Unknown };

        Assert.False(reserves.CanPrice);
        Assert.False(PumpBondingCurve.TryQuoteBuy(reserves, 1_000_000, out _));
    }

    [Fact]
    public void Negative_signed_quote_reserves_are_refused_rather_than_priced()
    {
        // The IDL declares the field as u64, but Pump's docs say it may be
        // negative. A curve with the high bit set is refused rather than priced
        // from a reading that may be wrong.
        var reserves = RoundCurve with
        {
            Basis = PumpReserveBasis.Quote,
            VirtualQuoteUnits = unchecked((ulong)-1_000_000L),
        };

        Assert.True(reserves.HasNegativeSignedQuoteReserves);
        Assert.False(reserves.CanPrice);
        Assert.False(PumpBondingCurve.TryQuoteBuy(reserves, 1_000, out _));
        Assert.False(PumpBondingCurve.TryQuoteSell(reserves, 1_000, out _));
        Assert.False(PumpBondingCurve.TryMarketCap(reserves, 1_000_000, out _));
    }

    [Fact]
    public void The_spot_price_is_scaled_so_that_truncation_does_not_eat_it()
    {
        // Why DefaultPriceScaleDecimals exists. Unscaled, a realistic launch
        // prices at 27 lamports per whole token when the true figure is 27.959, a
        // 3% error. Both values are worked out separately in exact integer
        // arithmetic.
        Assert.True(PumpBondingCurve.TrySpotPrice(RealisticCurve, 6, 0, out var natural));
        Assert.Equal((UInt128)27, natural);

        Assert.True(PumpBondingCurve.TrySpotPrice(RealisticCurve, 6, out var scaled));
        Assert.Equal((UInt128)27_958_993_476, scaled);
    }

    [Fact]
    public void The_spot_price_of_a_round_curve_is_exact()
    {
        // 3e10 quote units over 1e15 base units, at 6 token decimals, is 30
        // lamports per whole token with nothing to truncate.
        Assert.True(PumpBondingCurve.TrySpotPrice(RoundCurve, 6, 0, out var price));
        Assert.Equal((UInt128)30, price);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(19)]
    public void Implausible_decimals_are_refused(int decimals)
    {
        Assert.False(PumpBondingCurve.TrySpotPrice(RoundCurve, decimals, 0, out _));
        Assert.False(PumpBondingCurve.TrySpotPrice(RoundCurve, 6, decimals, out _));
    }

    [Fact]
    public void Market_cap_values_the_supply_at_the_spot_price()
    {
        Assert.True(PumpBondingCurve.TryMarketCap(RoundCurve, 1_000_000_000_000_000, out var cap));

        // 1e15 base units at 3e10/1e15 per unit: 30 SOL.
        Assert.Equal(30_000_000_000UL, cap);
    }

    [Fact]
    public void A_market_cap_that_would_not_fit_is_refused()
    {
        // A refusal, not a wrapped number, because a wrapped market cap would look
        // small and plausible. long.MaxValue rather than ulong.MaxValue so the
        // overflow check is what refuses, not the negative-reserves guard.
        var lopsided = RoundCurve with
        {
            VirtualBaseUnits = 1,
            VirtualQuoteUnits = (ulong)long.MaxValue,
        };

        Assert.False(PumpBondingCurve.TryMarketCap(lopsided, ulong.MaxValue, out _));
    }

    [Theory]
    [InlineData(1_000_000UL, 100UL, 10_000UL)]
    [InlineData(1_000_000_000UL, 95UL, 9_500_000UL)]
    [InlineData(0UL, 100UL, 0UL)]
    [InlineData(1_000UL, 0UL, 0UL)]
    [InlineData(1_000UL, 10_000UL, 1_000UL)]
    public void A_fee_is_basis_points_of_the_amount(ulong amount, ulong basisPoints, ulong expected)
    {
        Assert.True(PumpBondingCurve.TryFee(amount, basisPoints, PumpFeeRounding.Down, out var fee));
        Assert.Equal(expected, fee);
    }

    [Fact]
    public void Fee_rounding_direction_changes_the_result_by_one()
    {
        // 1 basis point of 12,345 is 1.2345. Rounding direction is a required
        // argument, so every call site states its assumption.
        Assert.True(PumpBondingCurve.TryFee(12_345, 1, PumpFeeRounding.Down, out var down));
        Assert.True(PumpBondingCurve.TryFee(12_345, 1, PumpFeeRounding.Up, out var up));

        Assert.Equal(1UL, down);
        Assert.Equal(2UL, up);
    }

    [Fact]
    public void An_exact_fee_rounds_the_same_way_in_both_directions()
    {
        Assert.True(PumpBondingCurve.TryFee(10_000, 100, PumpFeeRounding.Down, out var down));
        Assert.True(PumpBondingCurve.TryFee(10_000, 100, PumpFeeRounding.Up, out var up));

        Assert.Equal(down, up);
    }

    [Fact]
    public void A_fee_above_one_hundred_percent_is_refused() =>
        Assert.False(PumpBondingCurve.TryFee(1_000, PumpBondingCurve.BasisPointsDenominator + 1, PumpFeeRounding.Down, out _));

    [Fact]
    public void A_fee_on_the_largest_amount_does_not_overflow()
    {
        Assert.True(PumpBondingCurve.TryFee(ulong.MaxValue, PumpBondingCurve.BasisPointsDenominator, PumpFeeRounding.Up, out var fee));
        Assert.Equal(ulong.MaxValue, fee);
    }

    [Fact]
    public void The_largest_possible_reserves_do_not_overflow_a_quote()
    {
        // Two u64 reserves multiplied need 128 bits. This fails if the widening
        // is ever lost.
        var huge = new PumpCurveReserves
        {
            VirtualBaseUnits = ulong.MaxValue / 2,
            VirtualQuoteUnits = ulong.MaxValue / 2,
            RealBaseUnits = ulong.MaxValue / 2,
            RealQuoteUnits = ulong.MaxValue / 2,
            Basis = PumpReserveBasis.Sol,
        };

        Assert.True(PumpBondingCurve.TryQuoteBuy(huge, ulong.MaxValue / 2, out var quote));
        Assert.True(quote.AmountOutUnits <= huge.VirtualBaseUnits);
        Assert.True(PumpBondingCurve.ConstantProduct(huge) > ulong.MaxValue);
    }
}
