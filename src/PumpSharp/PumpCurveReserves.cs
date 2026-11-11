namespace PumpSharp;

/// <summary>
/// Which pair of reserve fields a curve state was read from.
///
/// A <c>TradeEvent</c> declares <c>virtual_sol_reserves</c> and
/// <c>virtual_token_reserves</c> alongside <c>virtual_quote_reserves</c> and
/// <c>real_quote_reserves</c>, because Pump coins can be paired with quote
/// mints other than SOL. Choosing a pair is an interpretation, so the choice
/// travels with the numbers and can be reported or checked.
/// </summary>
public enum PumpReserveBasis
{
    /// <summary>No usable reserves were present.</summary>
    Unknown = 0,

    /// <summary>Read from the <c>*_sol_reserves</c> fields.</summary>
    Sol = 1,

    /// <summary>Read from the <c>*_quote_reserves</c> fields, for a non-SOL quote mint.</summary>
    Quote = 2,
}

/// <summary>
/// A bonding curve's reserves at one point in time, in integer base units:
/// lamports (or the quote mint's smallest unit) on the quote side and raw
/// token units on the base side. All quote maths is integer arithmetic.
///
/// Virtual reserves set the price. Real reserves are what the curve holds:
/// they limit what a trade can receive and show how close the token is to
/// completing.
/// </summary>
public readonly record struct PumpCurveReserves
{
    /// <summary>Virtual token-side reserves: the curve's pricing denominator.</summary>
    public required ulong VirtualBaseUnits { get; init; }

    /// <summary>Virtual quote-side reserves: the curve's pricing numerator.</summary>
    public required ulong VirtualQuoteUnits { get; init; }

    /// <summary>Tokens the curve still holds and can sell.</summary>
    public required ulong RealBaseUnits { get; init; }

    /// <summary>Quote units the curve holds and can pay out.</summary>
    public required ulong RealQuoteUnits { get; init; }

    /// <summary>Which reserve fields these came from.</summary>
    public required PumpReserveBasis Basis { get; init; }

    /// <summary>
    /// True when the virtual quote reserves, read as a signed 64-bit integer,
    /// are negative.
    ///
    /// The IDL declares <c>virtual_quote_reserves</c> as <c>u64</c>, but Pump's
    /// documentation (read 24 October 2026) says it may be negative. Which is
    /// right changes the sign of a price.
    ///
    /// The decoder keeps the raw bits, this flag reports the conflict, and
    /// <see cref="PumpBondingCurve"/> refuses to price a curve where it is set.
    /// </summary>
    public bool HasNegativeSignedQuoteReserves => (long)VirtualQuoteUnits < 0;

    /// <summary>True when both virtual sides are non-zero, so a price exists.</summary>
    public bool CanPrice =>
        Basis != PumpReserveBasis.Unknown
        && VirtualBaseUnits > 0
        && VirtualQuoteUnits > 0
        && !HasNegativeSignedQuoteReserves;
}
