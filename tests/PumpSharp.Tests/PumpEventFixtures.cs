using PumpSharp.Chain;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// Turns a typed trade back into the log line a feed would carry, so tests
/// can generate trades and run them through the real decode path.
///
/// Fields the decoder skips are written as zeroes and defaults. If the
/// decoder starts depending on one of them, tests fail instead of quietly
/// reading a zero.
/// </summary>
internal static class PumpEventFixtures
{
    private static ulong TradeDiscriminator => Discriminator("TradeEvent");

    /// <summary>Serialises a trade into a <c>Program data:</c> log line.</summary>
    public static string TradeLogLine(in PumpTradeEvent trade) =>
        TradeWriter(trade).ToLogLine();

    /// <summary>Serialises a trade into a raw Anchor payload.</summary>
    public static byte[] TradePayload(in PumpTradeEvent trade) =>
        TradeWriter(trade).ToArray();

    /// <summary>
    /// Serialises a trade with a chosen <c>ix_name</c>, the layout's one
    /// variable-length field before the end.
    ///
    /// Used by the drift diagnostics tests, which need to move the failure
    /// offset to check the difference between a stable and a varying offset.
    /// </summary>
    public static byte[] TradePayload(in PumpTradeEvent trade, string instructionName) =>
        TradeWriter(trade, instructionName).ToArray();

    /// <summary>
    /// A representative bonding-curve buy, shared so the many-field layout is
    /// written out once.
    /// </summary>
    public static PumpTradeEvent SampleTrade() => new()
    {
        // Built from byte fills so the fixture does not depend on other test
        // helpers.
        Mint = SampleKey(0x11),
        User = SampleKey(0x22),
        Creator = SampleKey(0x33),
        QuoteMint = Pubkey.Default,
        IsBuy = true,
        MayhemMode = false,
        TimestampUnixSeconds = 1_761_500_000,
        SolAmount = 1_000_000,
        TokenAmount = 1_000_000_000,
        QuoteAmount = 0,
        VirtualSolReserves = 30_000_000_000,
        VirtualTokenReserves = 1_073_000_000_000_000,
        RealSolReserves = 1_000_000,
        RealTokenReserves = 793_100_000_000_000,
        VirtualQuoteReserves = 0,
        RealQuoteReserves = 0,
        Fee = 0,
        CreatorFee = 0,
        Cashback = 0,
        BuybackFee = 0,
        HolderRewards = 0,
        QuoteTransferFee = 0,
    };

    /// <summary>
    /// A representative launch. The <c>uri</c> is a parameter so tests can tell
    /// "no URI" apart from an empty URI.
    /// </summary>
    public static PumpCreateEvent SampleLaunch(
        Pubkey? mint = null,
        Pubkey? creator = null,
        string uri = "https://example.invalid/fixture.json",
        Pubkey? quoteMint = null,
        bool mayhemMode = false) => new()
    {
        Name = "Fixture",
        Symbol = "FIX",
        Uri = uri,
        Mint = mint ?? SampleKey(0x40),
        BondingCurve = SampleKey(0xc0),
        User = creator ?? SampleKey(0x21),
        Creator = creator ?? SampleKey(0x21),
        TimestampUnixSeconds = 1_761_500_000,
        VirtualTokenReserves = 1_073_000_000_000_000,
        VirtualSolReserves = 30_000_000_000,
        RealTokenReserves = 793_100_000_000_000,
        TokenTotalSupply = 1_000_000_000_000_000,
        TokenProgram = SampleKey(0x06),
        IsMayhemMode = mayhemMode,
        IsCashbackEnabled = false,
        QuoteMint = quoteMint ?? Pubkey.Default,
        VirtualQuoteReserves = 0,
        CreatorFeeBasisPoints = 0,
        IsHolderReward = false,
        Depth = 0,
    };

    private static Pubkey SampleKey(byte seed)
    {
        Span<byte> bytes = stackalloc byte[Pubkey.Length];
        bytes.Fill(seed);
        return new Pubkey(bytes);
    }

    /// <summary>Serialises a launch into a raw Anchor payload.</summary>
    public static byte[] CreatePayload(PumpCreateEvent launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        return new BorshTestWriter()
            .Discriminator(Discriminator("CreateEvent"))
            .Text(launch.Name)                          // name
            .Text(launch.Symbol)                        // symbol
            .Text(launch.Uri)                           // uri
            .Key(launch.Mint)                           // mint
            .Key(launch.BondingCurve)                   // bonding_curve
            .Key(launch.User)                           // user
            .Key(launch.Creator)                        // creator
            .I64(launch.TimestampUnixSeconds)           // timestamp
            .U64(launch.VirtualTokenReserves)           // virtual_token_reserves
            .U64(launch.VirtualSolReserves)             // virtual_sol_reserves
            .U64(launch.RealTokenReserves)              // real_token_reserves
            .U64(launch.TokenTotalSupply)               // token_total_supply
            .Key(launch.TokenProgram)                   // token_program
            .Bool(launch.IsMayhemMode)                  // is_mayhem_mode
            .Bool(launch.IsCashbackEnabled)             // is_cashback_enabled
            .Key(launch.QuoteMint)                      // quote_mint
            .U64(launch.VirtualQuoteReserves)           // virtual_quote_reserves
            .U64(launch.CreatorFeeBasisPoints)          // creator_fee_bps
            .Bool(launch.IsHolderReward)                // is_holder_reward
            .U8(launch.Depth)                           // depth
            .ToArray();
    }

    /// <summary>
    /// A payload holding only a declared discriminator.
    ///
    /// For events this library recognises but does not decode, such as a fee
    /// collection. The body is left out because nothing reads it, and writing
    /// one would suggest its layout had been checked.
    /// </summary>
    public static byte[] KnownEventPayload(string eventName) =>
        new BorshTestWriter().Discriminator(Discriminator(eventName)).ToArray();

    public static byte[] CompletionPayload(Pubkey mint, long timestamp = 1_761_500_000, Pubkey quoteMint = default) =>
        new BorshTestWriter().Discriminator(Discriminator("CompleteEvent"))
            .Key(default).Key(mint).Key(default).I64(timestamp).Key(quoteMint).ToArray();

    public static byte[] MigrationPayload(
        Pubkey mint, Pubkey pool, long timestamp = 1_761_500_001, Pubkey quoteMint = default) =>
        new BorshTestWriter().Discriminator(Discriminator("CompletePumpAmmMigrationEvent"))
            .Key(default).Key(mint).U64(206_900_000_000_000).U64(85_000_000_000).U64(15_000_000)
            .Key(default).I64(timestamp).Key(pool).Key(quoteMint).ToArray();

    private static ulong Discriminator(string eventName) =>
        PumpIdl.Pump.Events.Single(entry => entry.Value == eventName).Key;

    private static BorshTestWriter TradeWriter(
        in PumpTradeEvent trade,
        string instructionName = "") =>
        new BorshTestWriter()
            .Discriminator(TradeDiscriminator)
            .Key(trade.Mint)                        // mint
            .U64(trade.SolAmount)                   // sol_amount
            .U64(trade.TokenAmount)                 // token_amount
            .Bool(trade.IsBuy)                      // is_buy
            .Key(trade.User)                        // user
            .I64(trade.TimestampUnixSeconds)        // timestamp
            .U64(trade.VirtualSolReserves)          // virtual_sol_reserves
            .U64(trade.VirtualTokenReserves)        // virtual_token_reserves
            .U64(trade.RealSolReserves)             // real_sol_reserves
            .U64(trade.RealTokenReserves)           // real_token_reserves
            .Key(Pubkey.Default)                    // fee_recipient
            .U64(0)                                 // fee_basis_points
            .U64(trade.Fee)                         // fee
            .Key(trade.Creator)                     // creator
            .U64(0)                                 // creator_fee_basis_points
            .U64(trade.CreatorFee)                  // creator_fee
            .Bool(false)                            // track_volume
            .U64(0)                                 // total_unclaimed_tokens
            .U64(0)                                 // total_claimed_tokens
            .U64(0)                                 // current_sol_volume
            .I64(0)                                 // last_update_timestamp
            .Text(instructionName)                  // ix_name
            .Bool(trade.MayhemMode)                 // mayhem_mode
            .U64(0)                                 // cashback_fee_basis_points
            .U64(trade.Cashback)                    // cashback
            .U64(0)                                 // buyback_fee_basis_points
            .U64(trade.BuybackFee)                  // buyback_fee
            .VecPrefix(0)                           // shareholders
            .Key(trade.QuoteMint)                   // quote_mint
            .U64(trade.QuoteAmount)                 // quote_amount
            .U64(trade.VirtualQuoteReserves)        // virtual_quote_reserves
            .U64(trade.RealQuoteReserves)           // real_quote_reserves
            .U64(0)                                 // holder_rewards_bps
            .U64(trade.HolderRewards)               // holder_rewards
            .U64(0)                                 // creator_fee_unclaimed
            .U64(trade.QuoteTransferFee);           // quote_transfer_fee
}
