using PumpSharp.Chain;

namespace PumpSharp;

/// <summary>
/// A PumpSwap <c>Pool</c> account, as declared in the checked-in
/// <c>pump_amm.json</c>.
///
/// A token that completes its bonding curve migrates to a pool, and after
/// <c>CompletePumpAmmMigrationEvent</c> the curve is no longer where it
/// trades. Prices must then come from the pool's state, which this type
/// holds. It does no pricing itself; see <see cref="PumpSwapQuote"/>.
///
/// <see cref="VirtualQuoteReserves"/> is a signed 128-bit field and can be
/// negative, even on ordinary pools, because waiting fees offset it. Reading
/// it as unsigned would turn a small negative value into a huge positive
/// one.
///
/// A pool is not always quoted in SOL. <see cref="QuoteMint"/> selects the
/// fee schedule, so pools in different quote mints pay different fees.
/// </summary>
public sealed record PumpSwapPoolAccount
{
    /// <summary>PDA bump for this pool.</summary>
    public required byte PoolBump { get; init; }

    /// <summary>Pool index, which distinguishes several pools over one pair.</summary>
    public required ushort Index { get; init; }

    /// <summary>Who created the pool. For a migrated pump token this is the migration authority, not the token's creator.</summary>
    public required Pubkey Creator { get; init; }

    /// <summary>The token being traded.</summary>
    public required Pubkey BaseMint { get; init; }

    /// <summary>What the pool is quoted in. Selects the fee schedule.</summary>
    public required Pubkey QuoteMint { get; init; }

    /// <summary>The pool's LP mint.</summary>
    public required Pubkey LpMint { get; init; }

    /// <summary>Token account holding the pool's base reserves.</summary>
    public required Pubkey PoolBaseTokenAccount { get; init; }

    /// <summary>Token account holding the pool's quote reserves.</summary>
    public required Pubkey PoolQuoteTokenAccount { get; init; }

    /// <summary>Circulating LP supply, excluding burns and lock-ups, as the IDL notes.</summary>
    public required ulong LpSupply { get; init; }

    /// <summary>The creator the pool credits creator fees to. The default key means no creator fee is charged, whatever the schedule says.</summary>
    public required Pubkey CoinCreator { get; init; }

    /// <summary>Mayhem-mode pool: fee tiers use the program's fixed total supply rather than the live mint supply.</summary>
    public required bool IsMayhemMode { get; init; }

    /// <summary>Cashback-enabled pool.</summary>
    public required bool IsCashbackCoin { get; init; }

    /// <summary>
    /// Virtual quote reserves, signed: boost reserves minus accrued protocol
    /// and creator fees, so ordinary pools can hold negative values.
    ///
    /// A boost pool prices on real plus virtual reserves.
    /// </summary>
    public required Int128 VirtualQuoteReserves { get; init; }

    /// <summary>Per-pool creator fee rate. Overrides the schedule's rate while the global switch is on and this is non-zero.</summary>
    public required ulong CreatorFeeBps { get; init; }

    /// <summary>Whether the creator fee can still be changed.</summary>
    public required bool CanEditCreatorFee { get; init; }

    /// <summary>Holder-reward pool.</summary>
    public required bool IsHolderReward { get; init; }

    /// <summary>Protocol fees accrued and not yet swept.</summary>
    public required ulong ProtocolFees { get; init; }

    /// <summary>Creator fees accrued and not yet claimed.</summary>
    public required ulong CreatorFees { get; init; }

    /// <summary>Whether this is a boost pool. On ordinary pools, waiting fees
    /// offset the virtual reserves. The SDK's
    /// VIRTUAL_QUOTE_RESERVES_FEE_ADJUSTMENT.md defines a boost as virtual plus
    /// protocol fees plus creator fees being non-zero. Comparing for equality
    /// avoids adding an untrusted i128 near its overflow limit.</summary>
    public bool HasBoostReserves => VirtualQuoteReserves != -((Int128)ProtocolFees + CreatorFees);
}
