using PumpSharp.Chain;

namespace PumpSharp;

/// <summary>
/// A bonding curve's migration to a PumpSwap pool, decoded from
/// <c>CompletePumpAmmMigrationEvent</c>.
///
/// After this event the curve is no longer where the token trades, so a
/// position priced from <c>BondingCurve</c> reserves is priced from a market
/// that no longer exists.
///
/// <see cref="Pool"/> is where prices come from afterwards, and this event
/// is the only place the pool address appears without deriving it.
/// <see cref="MintAmount"/> and <see cref="SolAmount"/> are the liquidity
/// the curve handed over, which become the pool's opening reserves.
/// <see cref="PoolMigrationFee"/> is taken out of that liquidity; leaving it
/// out would understate the cost of holding through a migration.
/// </summary>
public sealed record PumpMigrationEvent
{
    /// <summary>Who signed the migration transaction.</summary>
    public required Pubkey User { get; init; }

    /// <summary>The token that migrated.</summary>
    public required Pubkey Mint { get; init; }

    /// <summary>Base-token units moved from the curve into the pool.</summary>
    public required ulong MintAmount { get; init; }

    /// <summary>
    /// Quote units moved from the curve into the pool.
    ///
    /// The IDL calls it <c>sol_amount</c>, a name from before quote mints
    /// existed. It is denominated in <see cref="QuoteMint"/>, which is not
    /// always SOL. The protocol's name is kept so the decoder matches the IDL.
    /// </summary>
    public required ulong SolAmount { get; init; }

    /// <summary>
    /// What the migration itself cost, in quote units. It comes out of the
    /// liquidity that seeds the pool.
    /// </summary>
    public required ulong PoolMigrationFee { get; init; }

    /// <summary>The curve account this token is leaving.</summary>
    public required Pubkey BondingCurve { get; init; }

    /// <summary>Chain time, Unix seconds.</summary>
    public required long Timestamp { get; init; }

    /// <summary>
    /// The PumpSwap pool this token now trades on.
    ///
    /// After this event, prices come from this pool's account state. The
    /// migration's liquidity amounts are not a substitute for reading that
    /// state and resolving its fees.
    /// </summary>
    public required Pubkey Pool { get; init; }

    /// <summary>What the pool is quoted in.</summary>
    public required Pubkey QuoteMint { get; init; }
}
