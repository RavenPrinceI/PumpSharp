using PumpSharp.Chain;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// Decoding the PumpSwap <c>Pool</c> account and the migration event that
/// moves a token onto it.
///
/// Same standard as <see cref="PumpAccountDecoderTests"/>: the layout read
/// matches the IDL field by field and in order, and every kind of mismatch
/// is reported.
///
/// Pricing is tested in <see cref="PumpSwapParityTests"/>, not here.
/// </summary>
public class PumpSwapPoolDecoderTests
{
    private static ulong PoolDiscriminator =>
        PumpIdl.PumpSwap.Accounts
            .First(entry => entry.Value == PumpAccountDecoder.PoolAccountName)
            .Key;

    private static ulong MigrationDiscriminator =>
        PumpIdl.Pump.Events
            .First(entry => entry.Value == "CompletePumpAmmMigrationEvent")
            .Key;

    private static Pubkey Key(byte seed)
    {
        Span<byte> bytes = stackalloc byte[Pubkey.Length];
        bytes.Fill(seed);
        return new Pubkey(bytes);
    }

    /// <summary>
    /// Writes a <c>Pool</c> in IDL field order, by hand. A writer generated from
    /// the reader's own schema would agree with the reader even if both were
    /// wrong.
    /// </summary>
    private static BorshTestWriter Pool(
        Pubkey? coinCreator = null,
        Pubkey? quoteMint = null,
        bool mayhem = false,
        Int128? virtualQuote = null,
        ulong creatorFeeBps = 100) =>
        new BorshTestWriter()
            .Discriminator(PoolDiscriminator)
            .U8(7)                              // pool_bump
            .U16(1)                             // index
            .Key(Key(0x21))                     // creator
            .Key(Key(0x22))                     // base_mint
            .Key(quoteMint ?? Pubkey.Default)   // quote_mint
            .Key(Key(0x24))                     // lp_mint
            .Key(Key(0x25))                     // pool_base_token_account
            .Key(Key(0x26))                     // pool_quote_token_account
            .U64(500_000_000)                   // lp_supply
            .Key(coinCreator ?? Pubkey.Default) // coin_creator
            .Bool(mayhem)                       // is_mayhem_mode
            .Bool(false)                        // is_cashback_coin
            .I128(virtualQuote ?? Int128.Zero)  // virtual_quote_reserves
            .U64(creatorFeeBps)                 // creator_fee_bps
            .Bool(true)                         // can_edit_creator_fee
            .Bool(false)                        // is_holder_reward
            .U64(4_242)                         // protocol_fees
            .U64(8_484);                        // creator_fees

    [Fact]
    public void A_well_formed_pool_decodes_every_field()
    {
        var coinCreator = Key(0x31);
        var bytes = Pool(coinCreator: coinCreator).ToArray();

        var failure = PumpAccountDecoder.DecodePool(bytes, out var pool, out var trailing);

        Assert.Equal(PumpAccountDecodeFailure.None, failure);
        Assert.Equal(0, trailing);
        Assert.Equal(7, pool.PoolBump);
        Assert.Equal(1, pool.Index);
        Assert.Equal(Key(0x21), pool.Creator);
        Assert.Equal(Key(0x22), pool.BaseMint);
        Assert.Equal(Pubkey.Default, pool.QuoteMint);
        Assert.Equal(Key(0x24), pool.LpMint);
        Assert.Equal(Key(0x25), pool.PoolBaseTokenAccount);
        Assert.Equal(Key(0x26), pool.PoolQuoteTokenAccount);
        Assert.Equal(500_000_000ul, pool.LpSupply);
        Assert.Equal(coinCreator, pool.CoinCreator);
        Assert.False(pool.IsMayhemMode);
        Assert.False(pool.IsCashbackCoin);
        Assert.Equal(Int128.Zero, pool.VirtualQuoteReserves);
        Assert.Equal(100ul, pool.CreatorFeeBps);
        Assert.True(pool.CanEditCreatorFee);
        Assert.False(pool.IsHolderReward);
        Assert.Equal(4_242ul, pool.ProtocolFees);
        Assert.Equal(8_484ul, pool.CreatorFees);
    }

    [Fact]
    public void The_layout_is_exactly_as_long_as_the_idl_declares()
    {
        // 8 discriminator bytes plus a 279-byte body. If a field is added,
        // removed or resized without this number changing, the code and the IDL
        // no longer match.
        Assert.Equal(287, Pool().ToArray().Length);
    }

    [Fact]
    public void Virtual_quote_reserves_keep_their_sign()
    {
        // Pump's docs say virtual_quote_reserves can be negative. Read as an
        // unsigned 128-bit field, -5 SOL becomes about 3.4e38, and a pool priced
        // from that would quote the token at almost zero.
        var bytes = Pool(virtualQuote: -5_000_000_000).ToArray();

        var failure = PumpAccountDecoder.DecodePool(bytes, out var pool, out _);

        Assert.Equal(PumpAccountDecodeFailure.None, failure);
        Assert.Equal((Int128)(-5_000_000_000), pool.VirtualQuoteReserves);
        Assert.True(pool.VirtualQuoteReserves < Int128.Zero);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 5_000_000_000)]
    [InlineData(false, -1)]
    public void Mayhem_mode_and_signed_boost_state_remain_explicit(
        bool mayhem,
        long virtualQuote)
    {
        // Quotes need the decoded mode and the signed virtual reserve, not an
        // assumed ordinary-pool default.
        var bytes = Pool(mayhem: mayhem, virtualQuote: virtualQuote).ToArray();

        Assert.Equal(PumpAccountDecodeFailure.None,
            PumpAccountDecoder.DecodePool(bytes, out var pool, out _));

        Assert.Equal(mayhem, pool.IsMayhemMode);
        Assert.Equal((Int128)virtualQuote, pool.VirtualQuoteReserves);
    }

    [Fact]
    public void Trailing_bytes_are_counted_rather_than_refused()
    {
        // An account's length is its allocated space, and a program may allocate
        // extra room for fields added later.
        var bytes = Pool().ToArray().Concat(new byte[24]).ToArray();

        var failure = PumpAccountDecoder.DecodePool(bytes, out var pool, out var trailing);

        Assert.Equal(PumpAccountDecodeFailure.None, failure);
        Assert.Equal(24, trailing);
        Assert.Equal(7, pool.PoolBump);
    }

    [Fact]
    public void A_short_pool_is_truncated_not_decoded_from_its_prefix()
    {
        var full = Pool().ToArray();

        // One byte short of the i128, the field a prefix read is most likely to
        // get wrong without failing.
        Assert.Equal(
            PumpAccountDecodeFailure.Truncated,
            PumpAccountDecoder.DecodePool(full.AsSpan(0, full.Length - 9), out _, out _));

        Assert.Equal(
            PumpAccountDecodeFailure.Truncated,
            PumpAccountDecoder.DecodePool(full.AsSpan(0, 8), out _, out _));

        Assert.Equal(
            PumpAccountDecodeFailure.Truncated,
            PumpAccountDecoder.DecodePool([], out _, out _));
    }

    [Fact]
    public void A_bonding_curve_account_is_refused_by_name_rather_than_read_as_a_pool()
    {
        // Anchor derives account discriminators from sha256("account:" + Name),
        // so both IDLs agree on BondingCurve. Passing one here must be refused by
        // name, because the two layouts are unrelated.
        var curve = new BorshTestWriter()
            .Discriminator(PumpIdl.PumpSwap.Accounts
                .First(entry => entry.Value == PumpAccountDecoder.BondingCurveAccountName)
                .Key)
            .U64(1)
            .ToArray();

        Assert.Equal(
            PumpAccountDecodeFailure.WrongAccount,
            PumpAccountDecoder.DecodePool(curve, out _, out _));
    }

    [Fact]
    public void An_undeclared_discriminator_is_the_drift_signal_not_a_decode_error()
    {
        var bytes = new BorshTestWriter().Discriminator(0xDEADBEEFDEADBEEF).U64(1).ToArray();

        Assert.Equal(
            PumpAccountDecodeFailure.UnknownDiscriminator,
            PumpAccountDecoder.DecodePool(bytes, out _, out _));
    }

    // The migration event.

    private static BorshTestWriter Migration(
        Pubkey? pool = null,
        ulong mintAmount = 206_900_000_000_000,
        ulong solAmount = 85_000_000_000,
        ulong migrationFee = 15_000_000) =>
        new BorshTestWriter()
            .Discriminator(MigrationDiscriminator)
            .Key(Key(0x41))                 // user
            .Key(Key(0x42))                 // mint
            .U64(mintAmount)                // mint_amount
            .U64(solAmount)                 // sol_amount
            .U64(migrationFee)              // pool_migration_fee
            .Key(Key(0x45))                 // bonding_curve
            .I64(1_800_000_000)             // timestamp
            .Key(pool ?? Key(0x47))         // pool
            .Key(Pubkey.Default);           // quote_mint

    [Fact]
    public void A_migration_event_decodes_every_field()
    {
        var pool = Key(0x51);
        var bytes = Migration(pool: pool).ToArray();

        var failure = PumpEventDecoder.DecodeMigration(bytes, out var migration, out var consumed);

        Assert.Equal(PumpDecodeFailure.None, failure);
        var decoded = migration
            ?? throw new InvalidOperationException("A successful decode returned no event.");

        Assert.Equal(Key(0x41), decoded.User);
        Assert.Equal(Key(0x42), decoded.Mint);
        Assert.Equal(206_900_000_000_000ul, decoded.MintAmount);
        Assert.Equal(85_000_000_000ul, decoded.SolAmount);
        Assert.Equal(15_000_000ul, decoded.PoolMigrationFee);
        Assert.Equal(Key(0x45), decoded.BondingCurve);
        Assert.Equal(1_800_000_000L, decoded.Timestamp);
        Assert.Equal(pool, decoded.Pool);
        Assert.Equal(Pubkey.Default, decoded.QuoteMint);
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void A_migration_event_reaches_the_dispatch_as_a_typed_event()
    {
        // The event's body is decoded, so a reader learns which pool now holds the
        // token, not just that a migration happened.
        var failure = PumpEventDecoder.Decode(Migration().ToArray(), out var decoded);

        Assert.Equal(PumpDecodeFailure.None, failure);
        Assert.Equal(PumpEventKind.MigrationComplete, decoded.Kind);
        Assert.True(decoded.HasTypedEvent);
        Assert.NotNull(decoded.Migration);
        Assert.Equal(Key(0x47), decoded.Migration.Pool);
    }

    [Fact]
    public void A_migration_payload_with_bytes_left_over_is_refused()
    {
        // An event payload is exactly the serialised struct, so trailing bytes
        // mean the deployed layout and the IDL disagree. Migrations get the same
        // drift check as trades.
        var bytes = Migration().ToArray().Concat(new byte[8]).ToArray();

        Assert.Equal(
            PumpDecodeFailure.TrailingBytes,
            PumpEventDecoder.DecodeMigration(bytes, out var migration, out _));

        Assert.Null(migration);
    }

    [Fact]
    public void A_truncated_migration_payload_is_refused_rather_than_read_from_its_prefix()
    {
        var full = Migration().ToArray();

        Assert.Equal(
            PumpDecodeFailure.Truncated,
            PumpEventDecoder.DecodeMigration(full.AsSpan(0, full.Length - 1), out _, out _));
    }

    [Fact]
    public void Another_event_offered_to_the_migration_decoder_is_refused_by_name()
    {
        var complete = new BorshTestWriter()
            .Discriminator(PumpIdl.Pump.Events.First(e => e.Value == "CompleteEvent").Key)
            .U64(1)
            .ToArray();

        Assert.Equal(
            PumpDecodeFailure.WrongEvent,
            PumpEventDecoder.DecodeMigration(complete, out _, out _));
    }
}
