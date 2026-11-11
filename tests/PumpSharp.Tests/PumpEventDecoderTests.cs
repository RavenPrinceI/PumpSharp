using PumpSharp.Chain;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// The typed Pump event decoder.
///
/// These are not parity tests; those use vectors generated from the
/// official Pump SDKs (<see cref="PumpParityTests"/> and the others). These
/// tests check that:
///
/// <list type="bullet">
/// <item>the decoder reads the layout the checked-in IDL declares, field by
/// field, shown by a round trip through a separate hand-written
/// writer;</item>
/// <item>a payload that does not match that layout exactly is refused with a
/// code that says how it failed, so drift shows up as a named failure and
/// not as a wrong number;</item>
/// <item>malformed and hostile input is refused without throwing or
/// allocating.</item>
/// </list>
///
/// Matching the IDL is not the same as matching the deployed program. The
/// exact-length rule means the decoder cannot disagree with its IDL without
/// saying so.
/// </summary>
public class PumpEventDecoderTests
{
    /// <summary>
    /// Read from the checked-in IDL rather than derived, so the test uses the
    /// same source as the decoder and fails if an event is renamed.
    /// </summary>
    private static ulong DiscriminatorFor(string eventName) =>
        PumpIdl.Pump.Events.Single(entry => entry.Value == eventName).Key;

    /// <summary>A different address per seed, so swapped fields show up as wrong values.</summary>
    private static Pubkey Key(byte seed)
    {
        var bytes = new byte[Pubkey.Length];
        bytes[0] = seed;
        bytes[31] = (byte)(seed ^ 0xFF);
        return new Pubkey(bytes);
    }

    // Sample values. The reserves look like a real Pump launch (about 30 SOL
    // of virtual quote against 1.073e15 raw token units), so a misplaced field
    // tends to produce an obviously wrong number.
    private const string SampleName = "Test Coin";
    private const string SampleSymbol = "TEST";
    private const string SampleUri = "https://example.invalid/metadata.json";
    private const long SampleTimestamp = 1_761_500_000;
    private const ulong SampleVirtualTokenReserves = 1_073_000_000_000_000;
    private const ulong SampleVirtualSolReserves = 30_000_000_000;
    private const ulong SampleRealTokenReserves = 793_100_000_000_000;
    private const ulong SampleTotalSupply = 1_000_000_000_000_000;

    private static BorshTestWriter ValidCreate(Pubkey quoteMint = default, ulong virtualQuoteReserves = 0) =>
        new BorshTestWriter()
            .Discriminator(DiscriminatorFor("CreateEvent"))
            .Text(SampleName)
            .Text(SampleSymbol)
            .Text(SampleUri)
            .Key(Key(1))                         // mint
            .Key(Key(2))                         // bonding_curve
            .Key(Key(3))                         // user
            .Key(Key(4))                         // creator
            .I64(SampleTimestamp)
            .U64(SampleVirtualTokenReserves)
            .U64(SampleVirtualSolReserves)
            .U64(SampleRealTokenReserves)
            .U64(SampleTotalSupply)
            .Key(Key(5))                         // token_program
            .Bool(false)                         // is_mayhem_mode
            .Bool(true)                          // is_cashback_enabled
            .Key(quoteMint)
            .U64(virtualQuoteReserves)
            .U64(95)                             // creator_fee_bps
            .Bool(false)                         // is_holder_reward
            .U8(1);                              // depth

    private static BorshTestWriter ValidTrade(
        int shareholders = 0,
        string ixName = "buy",
        Pubkey quoteMint = default,
        ulong virtualQuoteReserves = 0,
        ulong realQuoteReserves = 0)
    {
        var writer = new BorshTestWriter()
            .Discriminator(DiscriminatorFor("TradeEvent"))
            .Key(Key(1))                          // mint
            .U64(1_000_000_000)                   // sol_amount
            .U64(34_600_000_000_000)              // token_amount
            .Bool(true)                           // is_buy
            .Key(Key(9))                          // user
            .I64(SampleTimestamp)
            .U64(31_000_000_000)                  // virtual_sol_reserves
            .U64(1_038_400_000_000_000)           // virtual_token_reserves
            .U64(1_000_000_000)                   // real_sol_reserves
            .U64(758_500_000_000_000)             // real_token_reserves
            .Key(Key(6))                          // fee_recipient
            .U64(100)                             // fee_basis_points
            .U64(10_000_000)                      // fee
            .Key(Key(4))                          // creator
            .U64(95)                              // creator_fee_basis_points
            .U64(9_500_000)                       // creator_fee
            .Bool(true)                           // track_volume
            .U64(0)                               // total_unclaimed_tokens
            .U64(0)                               // total_claimed_tokens
            .U64(1_000_000_000)                   // current_sol_volume
            .I64(SampleTimestamp)                 // last_update_timestamp
            .Text(ixName)
            .Bool(false)                          // mayhem_mode
            .U64(0)                               // cashback_fee_basis_points
            .U64(1_000)                           // cashback
            .U64(0)                               // buyback_fee_basis_points
            .U64(2_000)                           // buyback_fee
            .VecPrefix((uint)shareholders);

        for (var i = 0; i < shareholders; i++)
        {
            writer.Shareholder(Key((byte)(100 + i)), (ushort)(i + 1));
        }

        return writer
            .Key(quoteMint)
            .U64(0)                               // quote_amount
            .U64(virtualQuoteReserves)
            .U64(realQuoteReserves)
            .U64(0)                               // holder_rewards_bps
            .U64(3_000)                           // holder_rewards
            .U64(9_500_000)                       // creator_fee_unclaimed
            .U64(4_000);                          // quote_transfer_fee
    }

    /// <summary>Decodes, or fails the test, returning a non-null event.</summary>
    private static PumpCreateEvent DecodeCreateOrFail(byte[] payload)
    {
        var failure = PumpEventDecoder.DecodeCreate(payload, out var create);

        Assert.Equal(PumpDecodeFailure.None, failure);
        return create ?? throw new InvalidOperationException("A successful decode returned no event.");
    }

    [Fact]
    public void A_create_event_decodes_every_field()
    {
        var create = DecodeCreateOrFail(ValidCreate().ToArray());

        Assert.Equal(SampleName, create.Name);
        Assert.Equal(SampleSymbol, create.Symbol);
        Assert.Equal(SampleUri, create.Uri);
        Assert.Equal(Key(1), create.Mint);
        Assert.Equal(Key(2), create.BondingCurve);
        Assert.Equal(Key(3), create.User);
        Assert.Equal(Key(4), create.Creator);
        Assert.Equal(SampleTimestamp, create.TimestampUnixSeconds);
        Assert.Equal(SampleVirtualTokenReserves, create.VirtualTokenReserves);
        Assert.Equal(SampleVirtualSolReserves, create.VirtualSolReserves);
        Assert.Equal(SampleRealTokenReserves, create.RealTokenReserves);
        Assert.Equal(SampleTotalSupply, create.TokenTotalSupply);
        Assert.Equal(Key(5), create.TokenProgram);
        Assert.False(create.IsMayhemMode);
        Assert.True(create.IsCashbackEnabled);
        Assert.True(create.QuoteMint.IsDefault);
        Assert.Equal(0UL, create.VirtualQuoteReserves);
        Assert.Equal(95UL, create.CreatorFeeBasisPoints);
        Assert.False(create.IsHolderReward);
        Assert.Equal((byte)1, create.Depth);

        // Chain time, not the observer's clock.
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(SampleTimestamp).UtcDateTime,
            create.TimestampUtc);
    }

    [Fact]
    public void A_trade_event_decodes_the_fields_it_keeps()
    {
        var failure = PumpEventDecoder.DecodeTrade(ValidTrade().ToArray(), out var trade);

        Assert.Equal(PumpDecodeFailure.None, failure);

        Assert.Equal(Key(1), trade.Mint);
        Assert.Equal(Key(9), trade.User);
        Assert.Equal(Key(4), trade.Creator);
        Assert.True(trade.QuoteMint.IsDefault);
        Assert.True(trade.IsBuy);
        Assert.False(trade.MayhemMode);
        Assert.Equal(SampleTimestamp, trade.TimestampUnixSeconds);
        Assert.Equal(1_000_000_000UL, trade.SolAmount);
        Assert.Equal(34_600_000_000_000UL, trade.TokenAmount);
        Assert.Equal(0UL, trade.QuoteAmount);
        Assert.Equal(31_000_000_000UL, trade.VirtualSolReserves);
        Assert.Equal(1_038_400_000_000_000UL, trade.VirtualTokenReserves);
        Assert.Equal(1_000_000_000UL, trade.RealSolReserves);
        Assert.Equal(758_500_000_000_000UL, trade.RealTokenReserves);
        Assert.Equal(10_000_000UL, trade.Fee);
        Assert.Equal(9_500_000UL, trade.CreatorFee);
        Assert.Equal(1_000UL, trade.Cashback);
        Assert.Equal(2_000UL, trade.BuybackFee);
        Assert.Equal(3_000UL, trade.HolderRewards);
    }

    [Fact]
    public void Every_fee_component_is_counted_in_the_total()
    {
        // The total must include every fee component, not just protocol and
        // creator fees, or execution cost is understated. The quote transfer fee
        // arrived in a protocol upgrade; skipping it would understate cost on
        // transfer-fee quote mints while still decoding cleanly.
        PumpEventDecoder.DecodeTrade(ValidTrade().ToArray(), out var trade);

        Assert.Equal(4_000UL, trade.QuoteTransferFee);
        Assert.Equal(
            10_000_000UL + 9_500_000 + 1_000 + 2_000 + 3_000 + 4_000,
            trade.TotalFees);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(40)]
    public void A_shareholder_vector_of_any_length_is_traversed(int shareholders)
    {
        // The vector sits between the fee fields and the quote-side fields, so
        // sizing its elements wrongly would shift every later field. Reading the
        // right quote reserves back confirms the sizing.
        var payload = ValidTrade(shareholders, quoteMint: Key(7), virtualQuoteReserves: 4_242).ToArray();

        var failure = PumpEventDecoder.DecodeTrade(payload, out var trade);

        Assert.Equal(PumpDecodeFailure.None, failure);
        Assert.Equal(Key(7), trade.QuoteMint);
        Assert.Equal(4_242UL, trade.VirtualQuoteReserves);
    }

    [Theory]
    [InlineData("")]
    [InlineData("buy")]
    [InlineData("buy_exact_quote_in_v3")]
    public void An_instruction_name_of_any_length_is_skipped(string ixName)
    {
        var failure = PumpEventDecoder.DecodeTrade(
            ValidTrade(ixName: ixName, quoteMint: Key(7), virtualQuoteReserves: 99).ToArray(),
            out var trade);

        Assert.Equal(PumpDecodeFailure.None, failure);
        Assert.Equal(99UL, trade.VirtualQuoteReserves);
    }

    [Fact]
    public void A_sol_quoted_curve_reports_the_sol_basis()
    {
        PumpEventDecoder.DecodeTrade(ValidTrade().ToArray(), out var trade);

        var reserves = trade.Reserves;
        Assert.Equal(PumpReserveBasis.Sol, reserves.Basis);
        Assert.Equal(trade.VirtualSolReserves, reserves.VirtualQuoteUnits);
        Assert.Equal(trade.VirtualTokenReserves, reserves.VirtualBaseUnits);
    }

    [Fact]
    public void A_non_sol_quote_mint_reports_the_quote_basis()
    {
        // Pump coins can be paired with non-SOL quote mints. The reserve pair used
        // to price a trade is reported, not assumed.
        var payload = ValidTrade(
            quoteMint: Key(7),
            virtualQuoteReserves: 500_000_000,
            realQuoteReserves: 250_000_000).ToArray();

        PumpEventDecoder.DecodeTrade(payload, out var trade);

        var reserves = trade.Reserves;
        Assert.Equal(PumpReserveBasis.Quote, reserves.Basis);
        Assert.Equal(500_000_000UL, reserves.VirtualQuoteUnits);
        Assert.Equal(250_000_000UL, reserves.RealQuoteUnits);
    }

    [Fact]
    public void A_payload_with_one_extra_byte_is_refused()
    {
        // A longer payload means the deployed program and the IDL disagree.
        // Decoding the prefix would produce a complete-looking event from an
        // unknown layout.
        var payload = ValidTrade().U8(0).ToArray();

        Assert.Equal(PumpDecodeFailure.TrailingBytes, PumpEventDecoder.DecodeTrade(payload, out _));
    }

    [Fact]
    public void A_truncated_payload_is_refused()
    {
        var full = ValidTrade().ToArray();
        var truncated = full[..^1];

        Assert.Equal(PumpDecodeFailure.Truncated, PumpEventDecoder.DecodeTrade(truncated, out _));
    }

    [Fact]
    public void A_bool_outside_zero_and_one_is_reported_as_malformed()
    {
        // is_buy is the fourth field, so a bad byte there is caught with most of
        // the payload unread. That is what separates malformed from truncated.
        var payload = new BorshTestWriter()
            .Discriminator(DiscriminatorFor("TradeEvent"))
            .Key(Key(1))
            .U64(1)
            .U64(1)
            .RawByte(0x42)
            .ToArray();

        Assert.Equal(PumpDecodeFailure.MalformedValue, PumpEventDecoder.DecodeTrade(payload, out _));
    }

    [Fact]
    public void Asking_for_the_wrong_event_is_refused()
    {
        Assert.Equal(
            PumpDecodeFailure.WrongEvent,
            PumpEventDecoder.DecodeTrade(ValidCreate().ToArray(), out _));

        Assert.Equal(
            PumpDecodeFailure.WrongEvent,
            PumpEventDecoder.DecodeCreate(ValidTrade().ToArray(), out _));
    }

    [Fact]
    public void An_undeclared_discriminator_is_the_drift_signal()
    {
        var payload = new BorshTestWriter().Discriminator(0xDEADBEEFCAFEF00D).U64(1).ToArray();

        var failure = PumpEventDecoder.Decode(payload, out var decoded);

        Assert.Equal(PumpDecodeFailure.UnknownDiscriminator, failure);
        Assert.Equal(PumpEventKind.Unrecognised, decoded.Kind);
    }

    [Fact]
    public void A_declared_event_this_project_does_not_decode_is_not_a_failure()
    {
        // "Known but not decoded" and "failed to decode" are different. The IDL
        // declares 32 events and this decoder reads four (create, trade,
        // completion and migration); reporting the rest as failures would hide
        // drift among normal traffic.
        var payload = new BorshTestWriter()
            .Discriminator(DiscriminatorFor("CollectCreatorFeeEvent"))
            .U64(1)
            .ToArray();

        var failure = PumpEventDecoder.Decode(payload, out var decoded);

        Assert.Equal(PumpDecodeFailure.None, failure);
        Assert.Equal(PumpEventKind.KnownOther, decoded.Kind);
        Assert.False(decoded.HasTypedEvent);
        Assert.Null(decoded.Create);
    }

    [Fact]
    public void An_oversized_payload_is_refused_before_decoding()
    {
        var payload = new byte[PumpEventDecoder.MaxPayloadBytes + 1];

        Assert.Equal(PumpDecodeFailure.PayloadTooLarge, PumpEventDecoder.Decode(payload, out _));
    }

    [Fact]
    public void A_payload_shorter_than_a_discriminator_is_truncated()
    {
        Assert.Equal(PumpDecodeFailure.Truncated, PumpEventDecoder.Decode(new byte[7], out _));
        Assert.Equal(PumpDecodeFailure.Truncated, PumpEventDecoder.Decode([], out _));
    }

    [Fact]
    public void A_feed_log_line_decodes_end_to_end()
    {
        // End to end: the text of a logsSubscribe notification in, a typed token
        // and its curve state out.
        var failure = PumpEventDecoder.DecodeLogLine(ValidCreate().ToLogLine(), out var decoded);

        Assert.Equal(PumpDecodeFailure.None, failure);
        Assert.Equal(PumpEventKind.Create, decoded.Kind);
        Assert.True(decoded.HasTypedEvent);

        var create = decoded.Create
            ?? throw new InvalidOperationException("A successful decode returned no event.");
        Assert.Equal(SampleName, create.Name);
        Assert.Equal(Key(1), create.Mint);
    }

    [Fact]
    public void A_trade_log_line_decodes_end_to_end()
    {
        var failure = PumpEventDecoder.DecodeLogLine(ValidTrade(shareholders: 3).ToLogLine(), out var decoded);

        Assert.Equal(PumpDecodeFailure.None, failure);
        Assert.Equal(PumpEventKind.Trade, decoded.Kind);
        Assert.Equal(Key(1), decoded.Trade.Mint);
        Assert.True(decoded.Trade.IsBuy);
    }

    [Theory]
    [InlineData("Program log: Instruction: Buy")]
    [InlineData("Program data:")]
    [InlineData("")]
    public void A_line_that_is_not_an_event_payload_is_reported_as_such(string line) =>
        Assert.Equal(PumpDecodeFailure.NotAnEventPayload, PumpEventDecoder.DecodeLogLine(line, out _));

    [Fact]
    public void Invalid_base64_is_reported_apart_from_an_ordinary_log_line() =>
        // Both cases once returned NotAnEventPayload, so a lost payload looked
        // like one of the many lines that were never payloads.
        Assert.Equal(
            PumpDecodeFailure.MalformedBase64,
            PumpEventDecoder.DecodeLogLine(PumpLogClassifier.ProgramDataPrefix + "!!!not base64!!!", out _));

    [Fact]
    public void A_line_that_is_not_a_program_data_line_is_not_a_finding() =>
        Assert.Equal(
            PumpDecodeFailure.NotAnEventPayload,
            PumpEventDecoder.DecodeLogLine("Program log: ordinary", out _));

    [Fact]
    public void An_over_long_log_line_is_refused_on_length()
    {
        var hostile = PumpLogClassifier.ProgramDataPrefix
            + new string('A', (PumpEventDecoder.MaxPayloadBytes * 2) + 8);

        Assert.Equal(PumpDecodeFailure.PayloadTooLarge, PumpEventDecoder.DecodeLogLine(hostile, out _));
    }

    [Fact]
    public void Arbitrary_bytes_behind_a_valid_discriminator_never_throw()
    {
        // Fuzzing aimed at field decoding: payloads that pass the discriminator
        // check. Random bytes almost never match the layout exactly, so success is
        // not asserted; the test only requires a return value, never an
        // exception.
        var random = new Random(20261026);
        var tradeDiscriminator = DiscriminatorFor("TradeEvent");
        var createDiscriminator = DiscriminatorFor("CreateEvent");

        for (var i = 0; i < 5_000; i++)
        {
            var body = new byte[random.Next(0, 700)];
            random.NextBytes(body);

            var discriminator = random.Next(2) == 0 ? tradeDiscriminator : createDiscriminator;
            var payload = new BorshTestWriter().Discriminator(discriminator).ToArray().Concat(body).ToArray();

            _ = PumpEventDecoder.Decode(payload, out _);
        }
    }
}
