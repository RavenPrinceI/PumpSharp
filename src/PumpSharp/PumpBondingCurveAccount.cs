using PumpSharp.Chain;

namespace PumpSharp;

/// <summary>
/// The on-chain <c>BondingCurve</c> account, decoded from the layout in the
/// checked-in official IDL.
///
/// A <c>TradeEvent</c> already carries the reserves after each trade. The
/// account adds three things the events do not:
///
/// <list type="bullet">
/// <item>Data for a token first seen mid-life, with no <c>CreateEvent</c>:
/// the creator and <see cref="InitialVirtualQuoteReserves"/>, which curve
/// progress needs.</item>
/// <item>Current state for a token that has stopped trading. Its last event
/// may be old; the account is current as of the slot it was read at.</item>
/// <item>Fields no event carries: the creator fee rate, whether the creator
/// can still change it, and the accrued fees.</item>
/// </list>
///
/// Reading it costs an RPC call, so it is worth doing for a shortlist of
/// tokens, not for every token on the feed.
/// </summary>
public sealed record PumpBondingCurveAccount
{
    /// <summary>Virtual token-side reserves: the curve's pricing denominator.</summary>
    public required ulong VirtualTokenReserves { get; init; }

    /// <summary>Virtual quote-side reserves: the curve's pricing numerator.</summary>
    public required ulong VirtualQuoteReserves { get; init; }

    /// <summary>Tokens the curve still holds and can sell.</summary>
    public required ulong RealTokenReserves { get; init; }

    /// <summary>Quote units the curve holds and can pay out.</summary>
    public required ulong RealQuoteReserves { get; init; }

    /// <summary>The mint's total supply in raw token units.</summary>
    public required ulong TokenTotalSupply { get; init; }

    /// <summary>True once the curve has completed and migration is due.</summary>
    public required bool Complete { get; init; }

    /// <summary>The launch creator.</summary>
    public required Pubkey Creator { get; init; }

    /// <summary>
    /// Mayhem mode. Fee selection uses the actual mint supply instead of the
    /// ordinary curve's fixed supply; the reserve quote formula is unchanged.
    /// </summary>
    public required bool IsMayhemMode { get; init; }

    /// <summary>Cashback coin flag. Carried, not interpreted.</summary>
    public required bool IsCashbackCoin { get; init; }

    /// <summary>
    /// The quote mint, or the default address for SOL. Decides which basis
    /// <see cref="Reserves"/> reports, as it does on a trade event.
    /// </summary>
    public required Pubkey QuoteMint { get; init; }

    /// <summary>Creator fee in basis points.</summary>
    public required ulong CreatorFeeBasisPoints { get; init; }

    /// <summary>Whether the creator may still change the creator fee.</summary>
    public required bool CanEditCreatorFee { get; init; }

    /// <summary>Holder-reward flag. Carried, not interpreted.</summary>
    public required bool IsHolderReward { get; init; }

    /// <summary>Creator fees accrued on this curve, in quote units.</summary>
    public required ulong CreatorFee { get; init; }

    /// <summary>Protocol fees accrued on this curve, in quote units.</summary>
    public required ulong ProtocolFees { get; init; }

    /// <summary>Curve depth. Carried, not interpreted.</summary>
    public required byte Depth { get; init; }

    /// <summary>
    /// The virtual quote reserves the curve started with.
    ///
    /// Curve progress compares this with the current reserves, so without it a
    /// token first seen mid-life has no progress figure.
    /// </summary>
    public required ulong InitialVirtualQuoteReserves { get; init; }

    /// <summary>Base units sold after completion. Carried, not interpreted.</summary>
    public required ulong PostCompleteBaseOut { get; init; }

    /// <summary>Quote units taken in after completion. Carried, not interpreted.</summary>
    public required ulong PostCompleteQuoteIn { get; init; }

    /// <summary>
    /// The reserves in the form the quote maths takes, with the basis recorded.
    ///
    /// Basis selection matches <c>PumpTradeEvent.Reserves</c> (the default
    /// quote mint means SOL), so a price computed from the account and one
    /// computed from an event agree. The account has one quote pair rather than
    /// the event's two, so the basis only says which unit that pair is in.
    /// </summary>
    public PumpCurveReserves Reserves => new()
    {
        VirtualBaseUnits = VirtualTokenReserves,
        VirtualQuoteUnits = VirtualQuoteReserves,
        RealBaseUnits = RealTokenReserves,
        RealQuoteUnits = RealQuoteReserves,
        Basis = QuoteMint.IsDefault ? PumpReserveBasis.Sol : PumpReserveBasis.Quote,
    };

    /// <summary>
    /// Curve progress in basis points: the fraction of the initial real token
    /// reserve that has been sold.
    ///
    /// The initial reserve is a parameter because the account does not carry
    /// it; it comes from the program's <c>Global</c> account. Progress could be
    /// estimated from the virtual quote reserves instead, but that would be a
    /// second, different definition of the same figure. Asking for the missing
    /// input keeps one definition.
    ///
    /// Returns <c>null</c> when the initial reserve is not known, rather than
    /// reporting a progress of zero.
    /// </summary>
    public int? CurveProgressBasisPoints(ulong initialRealTokenReserves)
    {
        if (initialRealTokenReserves == 0)
        {
            return null;
        }

        var sold = initialRealTokenReserves > RealTokenReserves
            ? initialRealTokenReserves - RealTokenReserves
            : 0;

        var progress = (UInt128)sold * 10_000 / initialRealTokenReserves;
        return progress >= 10_000 ? 10_000 : (int)progress;
    }
}
