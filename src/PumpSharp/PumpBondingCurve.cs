namespace PumpSharp;

/// <summary>
/// Which way a fee is rounded to a whole base unit.
///
/// There is no default. Pump's SDK rounds fees in the protocol's favour on
/// some paths, so the caller must state the direction. That keeps every
/// rounding assumption visible at the call site.
/// </summary>
public enum PumpFeeRounding
{
    /// <summary>Round down: the trader keeps the fraction.</summary>
    Down = 0,

    /// <summary>Round up: the protocol keeps the fraction.</summary>
    Up = 1,
}

/// <summary>
/// One quoted trade against a bonding curve, in integer base units.
/// </summary>
public readonly record struct PumpCurveQuote
{
    /// <summary>What goes in: quote units for a buy, token units for a sell.</summary>
    public required ulong AmountInUnits { get; init; }

    /// <summary>What comes out: token units for a buy, quote units for a sell.</summary>
    public required ulong AmountOutUnits { get; init; }

    /// <summary>
    /// True when the curve could not deliver the full output and the quote was
    /// capped at what it holds.
    ///
    /// For a buy, this means the curve runs out of tokens. On chain that
    /// completes the curve and moves the token to PumpSwap, so a clamped buy is
    /// a lifecycle signal. It is reported rather than hidden because a caller
    /// sizing against the uncapped number would expect a fill it cannot get.
    /// </summary>
    public required bool ClampedToRealReserves { get; init; }

    /// <summary>
    /// True when the curve does not hold enough to settle this trade, so the
    /// chain would reject it.
    ///
    /// Only a sell sets this. The output beside it is the full curve price,
    /// which is what the SDK returns. There is no smaller payout on chain: the
    /// instruction fails. Treat this as "no trade", and treat the output as a
    /// price, not as proceeds.
    ///
    /// An earlier version clamped the output to the real reserves instead. That
    /// turned an impossible trade into a plausible small payout, and the SDK
    /// vectors caught it.
    /// </summary>
    public bool ExceedsRealReserves { get; init; }

    /// <summary>Which reserve pair this was priced from.</summary>
    public required PumpReserveBasis Basis { get; init; }

    /// <summary>
    /// The curve state this trade would leave behind, ignoring fees: the
    /// virtual reserves move by the traded amounts and the real reserves
    /// follow. Chaining quotes through this lets the constant-product invariant
    /// be checked across a sequence of trades.
    /// </summary>
    public required PumpCurveReserves ResultingReserves { get; init; }
}

/// <summary>
/// Constant-product quote maths for the Pump bonding curve, in integer base
/// units. There is no floating point. Intermediate products use
/// <see cref="UInt128"/> because two u64 reserves multiplied together
/// overflow u64 at realistic sizes.
///
/// The curve keeps the product of its virtual reserves constant. A buy of
/// <c>q</c> quote units returns <c>floor(q * base / (quote + q))</c> tokens,
/// a sell of <c>t</c> tokens returns <c>floor(t * quote / (base + t))</c>
/// quote units, and the output is capped at what the curve really holds.
/// The reserve fields come from the <c>BondingCurve</c> declaration in the
/// checked-in IDL.
///
/// These are core quotes on the net input. Fee resolution, fee composition
/// and the v3 synthetic migration are separate functions, each checked
/// against the SDK. A clamped buy describes the curve; it is not an
/// executable partial fill, and callers should refuse it unless they have a
/// partial-fill policy with a correct debit. <see cref="IsParityVerified"/>
/// covers the audited maths only, not execution behaviour or SDK routes this
/// library does not implement.
/// </summary>
public static class PumpBondingCurve
{
    /// <summary>Basis points are hundredths of a percent.</summary>
    public const ulong BasisPointsDenominator = 10_000;

    /// <summary>
    /// Extra decimal digits <see cref="TrySpotPrice(in PumpCurveReserves, int, out UInt128)"/>
    /// scales a price by.
    ///
    /// The unscaled price is too coarse. A typical launch has about 30 SOL
    /// against 1.07e15 token units, roughly 27.96 lamports per whole token, and
    /// flooring that to 27 is a 3% error. Nine extra digits push the truncation
    /// below any realistic trade size.
    /// </summary>
    public const int DefaultPriceScaleDecimals = 9;

    /// <summary>Largest token decimals accepted, above any SPL mint in practice.</summary>
    private const int MaxDecimals = 18;

    /// <summary>
    /// Whether this library's Pump quote maths has been checked against the
    /// official SDK vectors, as recorded in
    /// <see cref="PumpProtocolFingerprint.QuoteMathsParityVerified"/>. Callers
    /// that execute or simulate trades should refuse to price while it is false.
    /// </summary>
    public static bool IsParityVerified => PumpProtocolFingerprint.Current.QuoteMathsParityVerified;

    /// <summary>
    /// The curve invariant: virtual base times virtual quote.
    ///
    /// Consecutive trades on one mint must leave it constant up to the curve's
    /// rounding, so a recorded sequence of <c>TradeEvent</c>s can confirm the
    /// formula from observation alone. That confirms the formula the program
    /// uses. It does not confirm how fees and rounding are applied.
    /// </summary>
    public static UInt128 ConstantProduct(in PumpCurveReserves reserves) =>
        (UInt128)reserves.VirtualBaseUnits * reserves.VirtualQuoteUnits;

    /// <summary>
    /// Quotes a buy: quote units in, token units out.
    ///
    /// Returns false when the curve cannot be priced: a zero virtual reserve,
    /// an unknown basis, or negative signed quote reserves (see
    /// <see cref="PumpCurveReserves.HasNegativeSignedQuoteReserves"/>).
    /// </summary>
    public static bool TryQuoteBuy(in PumpCurveReserves reserves, ulong quoteInUnits, out PumpCurveQuote quote)
    {
        quote = default;

        if (!reserves.CanPrice)
        {
            return false;
        }

        // floor(in * base / (quote + in)). The product needs 128 bits. CanPrice
        // guarantees both virtual sides are non-zero, so the divisor is at least
        // one.
        var divisor = (UInt128)reserves.VirtualQuoteUnits + quoteInUnits;
        var baseOut = (UInt128)quoteInUnits * reserves.VirtualBaseUnits / divisor;

        // The curve can never pay out more than it prices against, so the virtual
        // bound holds before the real one is checked.
        if (baseOut > reserves.VirtualBaseUnits)
        {
            baseOut = reserves.VirtualBaseUnits;
        }

        var clamped = false;
        if (baseOut > reserves.RealBaseUnits)
        {
            baseOut = reserves.RealBaseUnits;
            clamped = true;
        }

        var baseOutUnits = (ulong)baseOut;

        quote = new PumpCurveQuote
        {
            AmountInUnits = quoteInUnits,
            AmountOutUnits = baseOutUnits,
            ClampedToRealReserves = clamped,
            Basis = reserves.Basis,
            ResultingReserves = reserves with
            {
                VirtualQuoteUnits = Saturate((UInt128)reserves.VirtualQuoteUnits + quoteInUnits),
                VirtualBaseUnits = reserves.VirtualBaseUnits - baseOutUnits,
                RealQuoteUnits = Saturate((UInt128)reserves.RealQuoteUnits + quoteInUnits),
                RealBaseUnits = reserves.RealBaseUnits - baseOutUnits,
            },
        };

        return true;
    }

    /// <summary>
    /// Quotes a sell: token units in, quote units out. Output is capped at the
    /// quote units the curve really holds.
    /// </summary>
    public static bool TryQuoteSell(in PumpCurveReserves reserves, ulong baseInUnits, out PumpCurveQuote quote)
    {
        quote = default;

        if (!reserves.CanPrice)
        {
            return false;
        }

        var divisor = (UInt128)reserves.VirtualBaseUnits + baseInUnits;
        var quoteOut = (UInt128)baseInUnits * reserves.VirtualQuoteUnits / divisor;

        if (quoteOut > reserves.VirtualQuoteUnits)
        {
            quoteOut = reserves.VirtualQuoteUnits;
        }

        // Not clamped to the real quote reserves. The SDK returns the full curve
        // price whatever the curve holds: on one late-curve state it returned
        // 77,851,642,118,744 against real reserves of 77,000,000,000. On chain
        // such a sell fails; it does not pay less. An earlier version clamped
        // here, which turned a rejected trade into a plausible small payout.
        //
        // The shortfall is reported instead, so the caller can refuse.
        var exceeds = quoteOut > reserves.RealQuoteUnits;
        var quoteOutUnits = Saturate(quoteOut);

        quote = new PumpCurveQuote
        {
            AmountInUnits = baseInUnits,
            AmountOutUnits = quoteOutUnits,
            ClampedToRealReserves = false,
            ExceedsRealReserves = exceeds,
            Basis = reserves.Basis,
            ResultingReserves = reserves with
            {
                VirtualBaseUnits = Saturate((UInt128)reserves.VirtualBaseUnits + baseInUnits),
                VirtualQuoteUnits = reserves.VirtualQuoteUnits >= quoteOutUnits
                    ? reserves.VirtualQuoteUnits - quoteOutUnits
                    : 0,
                RealBaseUnits = Saturate((UInt128)reserves.RealBaseUnits + baseInUnits),

                // Floored rather than wrapped. A trade this large does not happen, so the
                // resulting state is hypothetical, but wrapping to near u64 max would look
                // like a real value.
                RealQuoteUnits = reserves.RealQuoteUnits >= quoteOutUnits
                    ? reserves.RealQuoteUnits - quoteOutUnits
                    : 0,
            },
        };

        return true;
    }

    /// <summary>
    /// The marginal price of one whole token, scaled by
    /// <see cref="DefaultPriceScaleDecimals"/> extra decimal digits.
    /// </summary>
    public static bool TrySpotPrice(in PumpCurveReserves reserves, int baseDecimals, out UInt128 scaledPrice) =>
        TrySpotPrice(reserves, baseDecimals, DefaultPriceScaleDecimals, out scaledPrice);

    /// <summary>
    /// The marginal price of one whole token in quote units, multiplied by
    /// <c>10^scaleDecimals</c> so that integer division keeps the significant
    /// digits.
    ///
    /// This is the instantaneous price (the ratio of the virtual reserves), not
    /// what a trade of any size pays; for that, use <see cref="TryQuoteBuy"/>.
    /// The difference is slippage. Using the spot price as an execution price
    /// makes a backtest look better than it is.
    /// </summary>
    public static bool TrySpotPrice(
        in PumpCurveReserves reserves,
        int baseDecimals,
        int scaleDecimals,
        out UInt128 scaledPrice)
    {
        scaledPrice = default;

        if (!reserves.CanPrice
            || baseDecimals is < 0 or > MaxDecimals
            || scaleDecimals is < 0 or > MaxDecimals)
        {
            return false;
        }

        if (!TryPowerOfTen(baseDecimals + scaleDecimals, out var scale))
        {
            return false;
        }

        scaledPrice = (UInt128)reserves.VirtualQuoteUnits * scale / reserves.VirtualBaseUnits;
        return true;
    }

    /// <summary>
    /// Total supply valued at the curve's spot price, in quote units.
    ///
    /// Floor division loses at most one lamport, which is negligible at this
    /// size.
    /// </summary>
    public static bool TryMarketCap(
        in PumpCurveReserves reserves,
        ulong totalSupplyBaseUnits,
        out ulong quoteUnits)
    {
        quoteUnits = 0;

        if (!reserves.CanPrice)
        {
            return false;
        }

        var value = (UInt128)totalSupplyBaseUnits * reserves.VirtualQuoteUnits / reserves.VirtualBaseUnits;
        if (value > ulong.MaxValue)
        {
            return false;
        }

        quoteUnits = (ulong)value;
        return true;
    }

    /// <summary>
    /// A fee in basis points of an amount, rounded in the stated direction.
    ///
    /// Not combined with the curve functions. Whether a fee is taken from the
    /// input before pricing or charged on top depends on the route, so the
    /// caller composes it explicitly.
    /// </summary>
    public static bool TryFee(
        ulong amountUnits,
        ulong basisPoints,
        PumpFeeRounding rounding,
        out ulong feeUnits)
    {
        feeUnits = 0;

        // A single fee component above 100% is not a fee. Refuse rather than
        // return a number larger than the trade.
        if (basisPoints > BasisPointsDenominator)
        {
            return false;
        }

        var numerator = (UInt128)amountUnits * basisPoints;
        var fee = rounding == PumpFeeRounding.Up
            ? (numerator + BasisPointsDenominator - 1) / BasisPointsDenominator
            : numerator / BasisPointsDenominator;

        // Cannot exceed amountUnits given the bound above, so this fits.
        feeUnits = (ulong)fee;
        return true;
    }

    /// <summary>Clamps to <see cref="ulong.MaxValue"/> rather than wrapping to a small number.</summary>
    private static ulong Saturate(UInt128 value) =>
        value > ulong.MaxValue ? ulong.MaxValue : (ulong)value;

    /// <summary>Ten to the power of <paramref name="exponent"/>, refusing anything that would not fit.</summary>
    private static bool TryPowerOfTen(int exponent, out UInt128 value)
    {
        value = UInt128.One;

        if (exponent is < 0 or > 38)
        {
            value = default;
            return false;
        }

        for (var i = 0; i < exponent; i++)
        {
            value *= 10;
        }

        return true;
    }
}
