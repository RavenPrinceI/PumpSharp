using PumpSharp.Chain;

namespace PumpSharp;

/// <summary>
/// A decoded Pump <c>TradeEvent</c>: one bonding-curve buy or sell, with the
/// curve state it left behind.
///
/// A <c>readonly record struct</c> holding a subset of the IDL's fields.
/// Trades made up 72% of a feed measured at over a thousand notifications a
/// second, so decoding a trade must not allocate. The subset covers pricing
/// and common signals; the other fields are read and discarded:
///
/// <list type="bullet">
/// <item><c>fee_recipient</c>: routing, not a signal, and 32 bytes.</item>
/// <item>The <c>*_basis_points</c> fields: the charged amounts are kept
/// instead. The rates live in the fee-config account, which
/// <see cref="PumpAccountDecoder"/> reads.</item>
/// <item><c>track_volume</c>, <c>total_unclaimed_tokens</c>,
/// <c>total_claimed_tokens</c>, <c>current_sol_volume</c> and
/// <c>last_update_timestamp</c>: the volume accumulator, out of scope.</item>
/// <item><c>ix_name</c>: a string, which would allocate on every trade.</item>
/// <item><c>shareholders</c>: a variable-length vector; holder-reward
/// distribution is not modelled.</item>
/// <item><c>creator_fee_unclaimed</c>: a running balance, not part of this
/// trade.</item>
/// </list>
///
/// Borsh is positional, so later fields can only be reached by reading
/// through the earlier ones.
/// </summary>
public readonly record struct PumpTradeEvent
{
    /// <summary>The token traded.</summary>
    public required Pubkey Mint { get; init; }

    /// <summary>The trader.</summary>
    public required Pubkey User { get; init; }

    /// <summary>The creator credited for this trade's creator fee.</summary>
    public required Pubkey Creator { get; init; }

    /// <summary>The quote mint. All-zero means SOL-quoted.</summary>
    public required Pubkey QuoteMint { get; init; }

    /// <summary>True for a buy, false for a sell.</summary>
    public required bool IsBuy { get; init; }

    /// <summary>Whether the curve is in mayhem mode, which uses its own virtual parameters.</summary>
    public required bool MayhemMode { get; init; }

    /// <summary>Program-reported trade time, Unix seconds: market time, not host time.</summary>
    public required long TimestampUnixSeconds { get; init; }

    /// <summary>SOL moved, in lamports.</summary>
    public required ulong SolAmount { get; init; }

    /// <summary>Tokens moved, in raw token units.</summary>
    public required ulong TokenAmount { get; init; }

    /// <summary>Quote units moved, for a non-SOL quote mint.</summary>
    public required ulong QuoteAmount { get; init; }

    /// <summary>Virtual SOL reserves after the trade.</summary>
    public required ulong VirtualSolReserves { get; init; }

    /// <summary>Virtual token reserves after the trade.</summary>
    public required ulong VirtualTokenReserves { get; init; }

    /// <summary>Real SOL reserves after the trade.</summary>
    public required ulong RealSolReserves { get; init; }

    /// <summary>Real token reserves after the trade: how close the curve is to completing.</summary>
    public required ulong RealTokenReserves { get; init; }

    /// <summary>Virtual quote reserves after the trade, raw bits. See <see cref="PumpCurveReserves.HasNegativeSignedQuoteReserves"/>.</summary>
    public required ulong VirtualQuoteReserves { get; init; }

    /// <summary>Real quote reserves after the trade.</summary>
    public required ulong RealQuoteReserves { get; init; }

    /// <summary>Protocol fee charged, in quote units.</summary>
    public required ulong Fee { get; init; }

    /// <summary>Creator fee charged, in quote units.</summary>
    public required ulong CreatorFee { get; init; }

    /// <summary>Cashback credited, in quote units.</summary>
    public required ulong Cashback { get; init; }

    /// <summary>Buyback fee charged, in quote units.</summary>
    public required ulong BuybackFee { get; init; }

    /// <summary>Holder rewards charged, in quote units.</summary>
    public required ulong HolderRewards { get; init; }

    /// <summary>
    /// Quote-side transfer fee charged, in quote units.
    ///
    /// Pump added this field (<c>quote_transfer_fee</c>, the last field of
    /// <c>TradeEvent</c> in SDK 4.3.1) after 24 October 2026. Because payloads
    /// must be consumed exactly, a decoder on the older IDL refused every trade
    /// until it was added.
    ///
    /// It is summed into <see cref="TotalFees"/>. A quote mint with a transfer
    /// fee charges it on every transfer, so it is a real execution cost.
    /// </summary>
    public required ulong QuoteTransferFee { get; init; }

    /// <summary>Trade time as UTC. Derived; the stored value is the chain's.</summary>
    public DateTime TimestampUtc => DateTimeOffset.FromUnixTimeSeconds(TimestampUnixSeconds).UtcDateTime;

    /// <summary>
    /// Every fee component this event reports, summed: protocol, creator,
    /// cashback, buyback, holder rewards and <see cref="QuoteTransferFee"/>.
    ///
    /// Leaving any of them out would understate execution cost and make a
    /// strategy look more profitable than it is. When the protocol adds a fee
    /// field, add it here. The sum saturates instead of wrapping, because a
    /// wrapped total would be a small, plausible number.
    /// </summary>
    public ulong TotalFees
    {
        get
        {
            var total = (UInt128)Fee
                + CreatorFee
                + Cashback
                + BuybackFee
                + HolderRewards
                + QuoteTransferFee;

            return total > ulong.MaxValue ? ulong.MaxValue : (ulong)total;
        }
    }

    /// <summary>
    /// The curve state this trade left behind.
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
            RealQuoteUnits = RealSolReserves,
            Basis = PumpReserveBasis.Sol,
        }
        : new PumpCurveReserves
        {
            VirtualBaseUnits = VirtualTokenReserves,
            VirtualQuoteUnits = VirtualQuoteReserves,
            RealBaseUnits = RealTokenReserves,
            RealQuoteUnits = RealQuoteReserves,
            Basis = PumpReserveBasis.Quote,
        };
}
