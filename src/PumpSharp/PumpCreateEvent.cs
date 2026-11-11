using PumpSharp.Chain;

namespace PumpSharp;

/// <summary>
/// A decoded Pump <c>CreateEvent</c>: a token launch.
///
/// Holds every field the IDL declares, in declaration order. Borsh has no
/// field tags, so reaching the last field means reading all of them anyway.
///
/// A record class, unlike the <see cref="PumpTradeEvent"/> struct. It holds
/// three strings and so allocates anyway, launches are rare (about 46 a
/// minute in one measurement) and callers usually keep them.
/// </summary>
public sealed record PumpCreateEvent
{
    /// <summary>Token name, as set by the creator. Untrusted text: escape it before putting it in logs, prompts or HTML.</summary>
    public required string Name { get; init; }

    /// <summary>Token symbol, as the creator set it. Untrusted text.</summary>
    public required string Symbol { get; init; }

    /// <summary>Metadata URI, as set by the creator. Untrusted: it is an arbitrary URL, so validate it before fetching.</summary>
    public required string Uri { get; init; }

    /// <summary>The new token's mint.</summary>
    public required Pubkey Mint { get; init; }

    /// <summary>The bonding curve account holding this token's reserves.</summary>
    public required Pubkey BondingCurve { get; init; }

    /// <summary>Who signed the create transaction.</summary>
    public required Pubkey User { get; init; }

    /// <summary>The creator the curve credits fees to, which need not be the signer.</summary>
    public required Pubkey Creator { get; init; }

    /// <summary>
    /// Creation time reported by the program, Unix seconds. This is chain
    /// time, not the observer's clock.
    /// </summary>
    public required long TimestampUnixSeconds { get; init; }

    /// <summary>Opening virtual token reserves.</summary>
    public required ulong VirtualTokenReserves { get; init; }

    /// <summary>Opening virtual SOL reserves.</summary>
    public required ulong VirtualSolReserves { get; init; }

    /// <summary>Opening real token reserves: what the curve can actually sell.</summary>
    public required ulong RealTokenReserves { get; init; }

    /// <summary>Total supply in raw token units.</summary>
    public required ulong TokenTotalSupply { get; init; }

    /// <summary>Which token program holds the mint: SPL Token or Token-2022.</summary>
    public required Pubkey TokenProgram { get; init; }

    /// <summary>Launched in mayhem mode, which has its own virtual parameters.</summary>
    public required bool IsMayhemMode { get; init; }

    public required bool IsCashbackEnabled { get; init; }

    /// <summary>The quote mint. The all-zero address means the curve is SOL-quoted.</summary>
    public required Pubkey QuoteMint { get; init; }

    /// <summary>Opening virtual quote reserves, raw bits. See <see cref="PumpCurveReserves.HasNegativeSignedQuoteReserves"/>.</summary>
    public required ulong VirtualQuoteReserves { get; init; }

    /// <summary>Creator fee in basis points, as configured at launch.</summary>
    public required ulong CreatorFeeBasisPoints { get; init; }

    public required bool IsHolderReward { get; init; }

    /// <summary>Curve depth. Bounded by the global <c>max_curve_depth</c>.</summary>
    public required byte Depth { get; init; }

    /// <summary>Creation time as UTC. Derived; the stored value is the chain's.</summary>
    public DateTime TimestampUtc => DateTimeOffset.FromUnixTimeSeconds(TimestampUnixSeconds).UtcDateTime;

    /// <summary>
    /// The opening curve state, for an immediate opening price.
    ///
    /// Uses the quote fields when a non-SOL quote mint is named and the SOL
    /// fields otherwise, and records which on the result. See
    /// <see cref="PumpReserveBasis"/>.
    /// </summary>
    public PumpCurveReserves Reserves => QuoteMint.IsDefault
        ? new PumpCurveReserves
        {
            VirtualBaseUnits = VirtualTokenReserves,
            VirtualQuoteUnits = VirtualSolReserves,
            RealBaseUnits = RealTokenReserves,
            RealQuoteUnits = 0,
            Basis = PumpReserveBasis.Sol,
        }
        : new PumpCurveReserves
        {
            VirtualBaseUnits = VirtualTokenReserves,
            VirtualQuoteUnits = VirtualQuoteReserves,
            RealBaseUnits = RealTokenReserves,
            RealQuoteUnits = 0,
            Basis = PumpReserveBasis.Quote,
        };
}
