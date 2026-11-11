using PumpSharp.Chain;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>Synthetic account bytes written by hand in checked-in IDL order.</summary>
internal static class PumpSwapAccountFixtures
{
    public static ulong Discriminator(string name) => PumpIdl.PumpSwap.Accounts.Single(x => x.Value == name).Key;

    public static BorshTestWriter FeeConfig(
        PumpSwapFees flat,
        IReadOnlyList<PumpSwapFeeTier> tiers,
        IReadOnlyList<PumpSwapFeeTier> stableTiers,
        PumpSwapFees exotic, byte bump = 254)
    {
        var writer = new BorshTestWriter().Discriminator(Discriminator("FeeConfig"))
            .U8(bump).Key(Pubkey.Default);
        Fees(writer, flat);
        Tiers(writer, tiers);
        Tiers(writer, stableTiers);
        return Fees(writer, exotic).PadTo(PumpAccountDecoder.FeeConfigPostExoticSize);
    }

    public static BorshTestWriter GlobalConfig(bool configurable = true, byte disableFlags = 0)
    {
        var writer = new BorshTestWriter().Discriminator(Discriminator("GlobalConfig"))
            .Key(Pubkey.Default).U64(21).U64(6).U8(disableFlags);
        Keys(writer, 8);
        writer.U64(8);
        Keys(writer, 3);
        writer.Bool(true); // mayhem_mode_enabled
        Keys(writer, 7);
        writer.Bool(true); // is_cashback_enabled
        Keys(writer, 8);
        writer.U64(9).Key(Pubkey.Default).Bool(true).Bool(configurable).U64(200);
        return writer;
    }

    public static BorshTestWriter Pool(
        Pubkey baseMint, Pubkey creator, Pubkey quoteMint, Pubkey coinCreator,
        ulong creatorFeeBps = 0, bool mayhem = false, Int128 virtualQuote = default,
        byte poolBump = 7, ushort index = 0, Pubkey? baseVault = null, Pubkey? quoteVault = null, Pubkey? lpMint = null) =>
        new BorshTestWriter().Discriminator(Discriminator("Pool"))
            .U8(poolBump).U16(index).Key(creator).Key(baseMint).Key(quoteMint)
            .Key(lpMint ?? default).Key(baseVault ?? default).Key(quoteVault ?? default).U64(500_000_000).Key(coinCreator)
            .Bool(mayhem).Bool(false).I128(virtualQuote).U64(creatorFeeBps)
            .Bool(true).Bool(false).U64(0).U64(0);

    public static BorshTestWriter Fees(BorshTestWriter writer, PumpSwapFees fees) =>
        writer.U64(fees.LpFeeBps).U64(fees.ProtocolFeeBps).U64(fees.CreatorFeeBps);

    private static void Tiers(BorshTestWriter writer, IReadOnlyList<PumpSwapFeeTier> tiers)
    {
        writer.U32((uint)tiers.Count);
        foreach (var tier in tiers)
        {
            writer.U128(tier.MarketCapThreshold);
            Fees(writer, tier.Fees);
        }
    }

    private static void Keys(BorshTestWriter writer, int count)
    {
        for (var i = 0; i < count; i++)
        {
            writer.Key(Pubkey.Default);
        }
    }
}
