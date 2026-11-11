namespace PumpSharp;

/// <summary>Why a payload did not decode. <see cref="None"/> is success.</summary>
public enum PumpDecodeFailure
{
    /// <summary>Decoded cleanly, consuming the payload exactly.</summary>
    None = 0,

    /// <summary>Not a <c>Program data:</c> line, or not valid base64.</summary>
    NotAnEventPayload = 1,

    /// <summary>Longer than <see cref="PumpEventDecoder.MaxPayloadBytes"/>. Refused before decoding.</summary>
    PayloadTooLarge = 2,

    /// <summary>
    /// The IDL does not declare this 8-byte discriminator. Either Pump shipped a
    /// new event or the IDL is out of date, so callers should count this as
    /// protocol drift.
    /// </summary>
    UnknownDiscriminator = 3,

    /// <summary>A known event, but not the one this call was asked to decode.</summary>
    WrongEvent = 4,

    /// <summary>Ran out of bytes mid-field: the payload is shorter than the layout.</summary>
    Truncated = 5,

    /// <summary>A value was not valid Borsh, such as a bool outside 0/1 or a length past the end.</summary>
    MalformedValue = 6,

    /// <summary>
    /// Decoded, but with bytes left over: the payload is longer than the
    /// layout. Refused because reading a prefix of an unknown layout can
    /// produce a plausible but wrong value. Trailing bytes mean the deployed
    /// program and the checked-in IDL disagree.
    /// </summary>
    TrailingBytes = 7,

    /// <summary>
    /// The line was a <c>Program data:</c> line but its base64 did not decode.
    ///
    /// Kept apart from <see cref="NotAnEventPayload"/>. Most log lines are not
    /// event payloads, which is normal. A malformed payload is data the caller
    /// lost. If both were reported the same way, a caller could not tell lost
    /// payloads from ordinary log lines.
    /// </summary>
    MalformedBase64 = 8,
}

/// <summary>
/// Decodes Pump Anchor event payloads into typed records.
///
/// <see cref="PumpLogClassifier"/> only reads the discriminator to count
/// events by kind. This type reads fields, so use it for the events you act
/// on.
///
/// Field order comes from the checked-in official IDLs and is written out by
/// hand in IDL order rather than generated from a schema. A protocol change
/// then shows up as a reviewable diff. The discriminators also come from the
/// IDL, through <see cref="PumpIdl"/>.
///
/// A payload must be consumed exactly. Too few bytes and too many bytes are
/// both refused, so the decoder either agrees with its IDL or declines. It
/// never returns a number read from a partial layout.
/// </summary>
public static class PumpEventDecoder
{
    /// <summary>Anchor event discriminators are 8 bytes.</summary>
    private const int DiscriminatorLength = 8;

    /// <summary>
    /// Largest payload decoded. This is above the largest payload the current
    /// layouts can produce and small enough for a stack buffer.
    ///
    /// From the checked-in IDL: <c>CreateEvent</c> is at most about 510 bytes
    /// (three strings whose Metaplex limits total 242, six addresses, five u64,
    /// an i64, three bools and a u8). <c>TradeEvent</c> is 387 bytes with an
    /// empty <c>ix_name</c> and no shareholders, plus the name and 34 bytes per
    /// shareholder, which leaves room for about forty shareholders. Live
    /// payloads were 390 to 439 bytes. A larger payload is reported as
    /// <see cref="PumpDecodeFailure.PayloadTooLarge"/>, not truncated, because a
    /// truncated decode would be a different event.
    /// </summary>
    public const int MaxPayloadBytes = 2048;

    /// <summary>Longest base64 text that can encode <see cref="MaxPayloadBytes"/>.</summary>
    private const int MaxBase64Length = (((MaxPayloadBytes + 2) / 3) * 4) + 4;

    /// <summary>Bytes per <c>Shareholder</c>: a 32-byte address and a u16 share.</summary>
    private const int ShareholderSize = 34;

    /// <summary>
    /// Decodes whichever typed event a <c>Program data:</c> log line carries:
    /// a log line in, a typed event out.
    /// </summary>
    public static PumpDecodeFailure DecodeLogLine(ReadOnlySpan<char> line, out PumpDecodedEvent decoded)
    {
        decoded = default;

        Span<byte> payload = stackalloc byte[MaxPayloadBytes];
        var read = TryReadPayload(line, payload, out var written);

        return read != PumpDecodeFailure.None
            ? read
            : Decode(payload[..written], out decoded);
    }

    /// <summary>
    /// Extracts the raw Anchor payload from a <c>Program data:</c> line,
    /// without interpreting it.
    ///
    /// Separate from <see cref="DecodeLogLine"/> for callers that need the
    /// bytes without decoding them, such as a pipeline that queues payloads and
    /// decodes them on another thread. Keeping the prefix match and base64
    /// decoding in one place avoids two copies that could drift apart.
    /// </summary>
    public static PumpDecodeFailure TryReadPayload(
        ReadOnlySpan<char> line,
        Span<byte> destination,
        out int written)
    {
        written = 0;

        if (!line.StartsWith(PumpLogClassifier.ProgramDataPrefix, StringComparison.Ordinal))
        {
            return PumpDecodeFailure.NotAnEventPayload;
        }

        var base64 = line[PumpLogClassifier.ProgramDataPrefix.Length..];
        if (base64.Length > MaxBase64Length)
        {
            return PumpDecodeFailure.PayloadTooLarge;
        }

        // A destination smaller than the payload is reported as too large, not
        // truncated, because a prefix of an event decodes into a plausible wrong
        // value.
        if (!Convert.TryFromBase64Chars(base64, destination, out written))
        {
            written = 0;
            return base64.Length > (((destination.Length + 2) / 3) * 4)
                ? PumpDecodeFailure.PayloadTooLarge
                : PumpDecodeFailure.MalformedBase64;
        }

        // The prefix matched, so an empty result is a malformed payload, not an
        // ordinary log line.
        return written == 0 ? PumpDecodeFailure.MalformedBase64 : PumpDecodeFailure.None;
    }

    /// <summary>
    /// Decodes a raw payload (discriminator and body) into its typed event. An
    /// event the IDL declares but this library does not decode is reported
    /// through <see cref="PumpDecodedEvent.Kind"/> with no failure. "Known but
    /// not decoded" is a different fact from "failed to decode", and mixing them
    /// would hide drift among routine traffic.
    /// </summary>
    public static PumpDecodeFailure Decode(ReadOnlySpan<byte> payload, out PumpDecodedEvent decoded) =>
        Decode(payload, out decoded, out _);

    /// <summary>
    /// Decodes a payload and reports where the read ended.
    ///
    /// A refusal count alone does not say what to fix. Truncated and
    /// trailing-bytes failures have opposite causes, and the offset where the
    /// reader stopped points to the field. See <see cref="PumpDecodeDiagnostics"/>.
    ///
    /// Nothing here allocates, and diagnostics are produced on every path,
    /// including success, so callers can aggregate them on a hot path without
    /// decoding twice.
    /// </summary>
    public static PumpDecodeFailure Decode(
        ReadOnlySpan<byte> payload,
        out PumpDecodedEvent decoded,
        out PumpDecodeDiagnostics diagnostics)
    {
        decoded = default;

        var discriminator = payload.Length >= DiscriminatorLength
            ? BitConverter.ToUInt64(payload[..DiscriminatorLength])
            : 0;

        var kind = PeekKind(payload, out var failure);
        if (failure != PumpDecodeFailure.None)
        {
            // The kind is still reported. An undeclared discriminator comes back as
            // Unrecognised, which callers count as drift.
            decoded = new PumpDecodedEvent { Kind = kind };

            diagnostics = new PumpDecodeDiagnostics
            {
                Kind = kind,
                Failure = failure,
                Discriminator = discriminator,
                PayloadBytes = payload.Length,

                // Nothing past the discriminator was read. If the payload was too short
                // to hold one, nothing was read at all.
                ConsumedBytes = payload.Length >= DiscriminatorLength ? DiscriminatorLength : 0,
                DeclaredBy = failure is PumpDecodeFailure.UnknownDiscriminator
                    ? DeclaringForeignProgram(payload)
                    : PumpForeignProgram.None,
            };

            return failure;
        }

        switch (kind)
        {
            case PumpEventKind.Create:
            {
                var createFailure = DecodeCreate(payload, out var create, out var consumed);
                decoded = new PumpDecodedEvent { Kind = kind, Create = create };
                diagnostics = Diagnose(kind, createFailure, discriminator, payload.Length, consumed);
                return createFailure;
            }

            case PumpEventKind.Trade:
            {
                var tradeFailure = DecodeTrade(payload, out var trade, out var consumed);
                decoded = new PumpDecodedEvent { Kind = kind, Trade = trade };
                diagnostics = Diagnose(kind, tradeFailure, discriminator, payload.Length, consumed);
                return tradeFailure;
            }

            case PumpEventKind.Complete:
            {
                var completeFailure = DecodeCompletion(payload, out var completion, out var consumed);
                decoded = new PumpDecodedEvent { Kind = kind, Completion = completion };
                diagnostics = Diagnose(kind, completeFailure, discriminator, payload.Length, consumed);
                return completeFailure;
            }

            case PumpEventKind.MigrationComplete:
            {
                // Migration payloads are decoded, not skipped. So a migration payload
                // whose layout disagrees with the IDL reports a named failure instead of
                // counting as consumed. That is drift detection working, not a new fault.
                var migrationFailure = DecodeMigration(payload, out var migration, out var consumed);
                decoded = new PumpDecodedEvent { Kind = kind, Migration = migration };
                diagnostics = Diagnose(kind, migrationFailure, discriminator, payload.Length, consumed);
                return migrationFailure;
            }

            default:
                decoded = new PumpDecodedEvent { Kind = kind };

                // A declared event this library does not decode. The discriminator was
                // read and the body was skipped on purpose, so the payload counts as
                // consumed, not as trailing bytes.
                diagnostics = Diagnose(
                    kind,
                    PumpDecodeFailure.None,
                    discriminator,
                    payload.Length,
                    payload.Length);

                return PumpDecodeFailure.None;
        }
    }

    private static PumpDecodeDiagnostics Diagnose(
        PumpEventKind kind,
        PumpDecodeFailure failure,
        ulong discriminator,
        int payloadBytes,
        int consumedBytes) => new()
        {
            Kind = kind,
            Failure = failure,
            Discriminator = discriminator,
            PayloadBytes = payloadBytes,
            ConsumedBytes = consumedBytes,
        };

    /// <summary>
    /// Which other checked-in program declares this discriminator, or
    /// <see cref="PumpForeignProgram.None"/> if none does.
    ///
    /// Three programs log into a Pump transaction and this decoder reads one of
    /// them, so PumpSwap and fee-program events are always undeclared here.
    /// Without this check, a drift counter would mostly count another program's
    /// normal traffic.
    /// </summary>
    public static PumpForeignProgram DeclaringForeignProgram(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < DiscriminatorLength)
        {
            return PumpForeignProgram.None;
        }

        var discriminator = BitConverter.ToUInt64(payload[..DiscriminatorLength]);

        if (PumpIdl.PumpSwap.Events.ContainsKey(discriminator))
        {
            return PumpForeignProgram.PumpSwap;
        }

        return PumpIdl.PumpFees.Events.ContainsKey(discriminator)
            ? PumpForeignProgram.PumpFees
            : PumpForeignProgram.None;
    }

    /// <summary>
    /// Reads the discriminator and names the event, without decoding fields.
    /// Cheap enough to use as a filter before committing to a full decode.
    /// </summary>
    public static PumpEventKind PeekKind(ReadOnlySpan<byte> payload, out PumpDecodeFailure failure)
    {
        failure = PumpDecodeFailure.None;

        if (payload.Length > MaxPayloadBytes)
        {
            failure = PumpDecodeFailure.PayloadTooLarge;
            return PumpEventKind.None;
        }

        if (payload.Length < DiscriminatorLength)
        {
            failure = PumpDecodeFailure.Truncated;
            return PumpEventKind.None;
        }

        var discriminator = BitConverter.ToUInt64(payload[..DiscriminatorLength]);
        if (!PumpIdl.Pump.Events.TryGetValue(discriminator, out var name))
        {
            failure = PumpDecodeFailure.UnknownDiscriminator;
            return PumpEventKind.Unrecognised;
        }

        return name switch
        {
            "CreateEvent" => PumpEventKind.Create,
            "TradeEvent" => PumpEventKind.Trade,
            "CompleteEvent" => PumpEventKind.Complete,
            "CompletePumpAmmMigrationEvent" => PumpEventKind.MigrationComplete,
            _ => PumpEventKind.KnownOther,
        };
    }

    /// <summary>
    /// Decodes a <c>CreateEvent</c> payload. Fields are read in the IDL's
    /// declaration order, and the trailing comments give the IDL field names.
    /// </summary>
    public static PumpDecodeFailure DecodeCreate(
        ReadOnlySpan<byte> payload,
        out PumpCreateEvent? create) =>
        DecodeCreate(payload, out create, out _);

    /// <summary>
    /// As <see cref="DecodeCreate(ReadOnlySpan{byte}, out PumpCreateEvent?)"/>,
    /// also reporting the offset the reader reached, discriminator included.
    /// </summary>
    public static PumpDecodeFailure DecodeCreate(
        ReadOnlySpan<byte> payload,
        out PumpCreateEvent? create,
        out int consumed)
    {
        create = null;
        consumed = 0;

        var kind = PeekKind(payload, out var failure);
        if (failure != PumpDecodeFailure.None)
        {
            return failure;
        }

        consumed = DiscriminatorLength;

        if (kind != PumpEventKind.Create)
        {
            return PumpDecodeFailure.WrongEvent;
        }

        var reader = new BorshReader(payload[DiscriminatorLength..]);

        var name = reader.ReadString();              // name
        var symbol = reader.ReadString();            // symbol
        var uri = reader.ReadString();               // uri
        var mint = reader.ReadPubkey();              // mint
        var bondingCurve = reader.ReadPubkey();      // bonding_curve
        var user = reader.ReadPubkey();              // user
        var creator = reader.ReadPubkey();           // creator
        var timestamp = reader.ReadI64();            // timestamp
        var virtualTokenReserves = reader.ReadU64(); // virtual_token_reserves
        var virtualSolReserves = reader.ReadU64();   // virtual_sol_reserves
        var realTokenReserves = reader.ReadU64();    // real_token_reserves
        var tokenTotalSupply = reader.ReadU64();     // token_total_supply
        var tokenProgram = reader.ReadPubkey();      // token_program
        var isMayhemMode = reader.ReadBool();        // is_mayhem_mode
        var isCashbackEnabled = reader.ReadBool();   // is_cashback_enabled
        var quoteMint = reader.ReadPubkey();         // quote_mint
        var virtualQuoteReserves = reader.ReadU64(); // virtual_quote_reserves
        var creatorFeeBps = reader.ReadU64();        // creator_fee_bps
        var isHolderReward = reader.ReadBool();      // is_holder_reward
        var depth = reader.ReadU8();                 // depth

        var verdict = Verdict(ref reader);
        consumed = DiscriminatorLength + reader.Position;

        if (verdict != PumpDecodeFailure.None)
        {
            return verdict;
        }

        create = new PumpCreateEvent
        {
            Name = name,
            Symbol = symbol,
            Uri = uri,
            Mint = mint,
            BondingCurve = bondingCurve,
            User = user,
            Creator = creator,
            TimestampUnixSeconds = timestamp,
            VirtualTokenReserves = virtualTokenReserves,
            VirtualSolReserves = virtualSolReserves,
            RealTokenReserves = realTokenReserves,
            TokenTotalSupply = tokenTotalSupply,
            TokenProgram = tokenProgram,
            IsMayhemMode = isMayhemMode,
            IsCashbackEnabled = isCashbackEnabled,
            QuoteMint = quoteMint,
            VirtualQuoteReserves = virtualQuoteReserves,
            CreatorFeeBasisPoints = creatorFeeBps,
            IsHolderReward = isHolderReward,
            Depth = depth,
        };

        return PumpDecodeFailure.None;
    }

    /// <summary>
    /// Decodes a <c>TradeEvent</c> payload. Every IDL field is read in order.
    /// Fields the record does not keep are skipped; <see cref="PumpTradeEvent"/>
    /// lists them and says why. Borsh is positional, so the quote-side fields
    /// at the end can only be reached by reading through the rest.
    /// </summary>
    public static PumpDecodeFailure DecodeTrade(
        ReadOnlySpan<byte> payload,
        out PumpTradeEvent trade) =>
        DecodeTrade(payload, out trade, out _);

    /// <summary>
    /// Decodes <c>CompletePumpAmmMigrationEvent</c> into a
    /// <see cref="PumpMigrationEvent"/>: the point at which a token stops
    /// trading on its bonding curve.
    /// </summary>
    public static PumpDecodeFailure DecodeMigration(
        ReadOnlySpan<byte> payload,
        out PumpMigrationEvent? migration) =>
        DecodeMigration(payload, out migration, out _);

    /// <summary>Decodes a <c>CompleteEvent</c> payload, which must be consumed exactly.</summary>
    public static PumpDecodeFailure DecodeCompletion(
        ReadOnlySpan<byte> payload, out PumpCompletionEvent? completion, out int consumed)
    {
        completion = null;
        consumed = 0;
        var kind = PeekKind(payload, out var failure);
        if (failure != PumpDecodeFailure.None)
        {
            return failure;
        }

        consumed = DiscriminatorLength;
        if (kind != PumpEventKind.Complete)
        {
            return PumpDecodeFailure.WrongEvent;
        }

        var reader = new BorshReader(payload[DiscriminatorLength..]);
        var user = reader.ReadPubkey();
        var mint = reader.ReadPubkey();
        var curve = reader.ReadPubkey();
        var timestamp = reader.ReadI64();
        var quoteMint = reader.ReadPubkey();
        var verdict = Verdict(ref reader);
        consumed = DiscriminatorLength + reader.Position;
        if (verdict != PumpDecodeFailure.None)
        {
            return verdict;
        }

        completion = new PumpCompletionEvent
        {
            User = user, Mint = mint, BondingCurve = curve,
            Timestamp = timestamp, QuoteMint = quoteMint,
        };
        return PumpDecodeFailure.None;
    }

    /// <summary>
    /// As <see cref="DecodeMigration(ReadOnlySpan{byte}, out PumpMigrationEvent?)"/>,
    /// also reporting the offset the reader reached, discriminator included.
    /// </summary>
    public static PumpDecodeFailure DecodeMigration(
        ReadOnlySpan<byte> payload,
        out PumpMigrationEvent? migration,
        out int consumed)
    {
        migration = null;
        consumed = 0;

        var kind = PeekKind(payload, out var failure);
        if (failure != PumpDecodeFailure.None)
        {
            return failure;
        }

        consumed = DiscriminatorLength;

        if (kind != PumpEventKind.MigrationComplete)
        {
            return PumpDecodeFailure.WrongEvent;
        }

        var reader = new BorshReader(payload[DiscriminatorLength..]);

        var user = reader.ReadPubkey();             // user
        var mint = reader.ReadPubkey();             // mint
        var mintAmount = reader.ReadU64();          // mint_amount
        var solAmount = reader.ReadU64();           // sol_amount
        var poolMigrationFee = reader.ReadU64();    // pool_migration_fee
        var bondingCurve = reader.ReadPubkey();     // bonding_curve
        var timestamp = reader.ReadI64();           // timestamp
        var pool = reader.ReadPubkey();             // pool
        var quoteMint = reader.ReadPubkey();        // quote_mint

        var verdict = Verdict(ref reader);
        consumed = DiscriminatorLength + reader.Position;

        if (verdict != PumpDecodeFailure.None)
        {
            return verdict;
        }

        migration = new PumpMigrationEvent
        {
            User = user,
            Mint = mint,
            MintAmount = mintAmount,
            SolAmount = solAmount,
            PoolMigrationFee = poolMigrationFee,
            BondingCurve = bondingCurve,
            Timestamp = timestamp,
            Pool = pool,
            QuoteMint = quoteMint,
        };

        return PumpDecodeFailure.None;
    }

    /// <summary>
    /// As <see cref="DecodeTrade(ReadOnlySpan{byte}, out PumpTradeEvent)"/>,
    /// also reporting the offset the reader reached, discriminator included.
    ///
    /// In a positional encoding a layout mismatch always sits at a byte offset.
    /// The same offset across many payloads identifies the field that changed.
    /// </summary>
    public static PumpDecodeFailure DecodeTrade(
        ReadOnlySpan<byte> payload,
        out PumpTradeEvent trade,
        out int consumed)
    {
        trade = default;
        consumed = 0;

        var kind = PeekKind(payload, out var failure);
        if (failure != PumpDecodeFailure.None)
        {
            return failure;
        }

        consumed = DiscriminatorLength;

        if (kind != PumpEventKind.Trade)
        {
            return PumpDecodeFailure.WrongEvent;
        }

        var reader = new BorshReader(payload[DiscriminatorLength..]);

        var mint = reader.ReadPubkey();              // mint
        var solAmount = reader.ReadU64();            // sol_amount
        var tokenAmount = reader.ReadU64();          // token_amount
        var isBuy = reader.ReadBool();               // is_buy
        var user = reader.ReadPubkey();              // user
        var timestamp = reader.ReadI64();            // timestamp
        var virtualSolReserves = reader.ReadU64();   // virtual_sol_reserves
        var virtualTokenReserves = reader.ReadU64(); // virtual_token_reserves
        var realSolReserves = reader.ReadU64();      // real_sol_reserves
        var realTokenReserves = reader.ReadU64();    // real_token_reserves
        _ = reader.ReadPubkey();                     // fee_recipient
        _ = reader.ReadU64();                        // fee_basis_points
        var fee = reader.ReadU64();                  // fee
        var creator = reader.ReadPubkey();           // creator
        _ = reader.ReadU64();                        // creator_fee_basis_points
        var creatorFee = reader.ReadU64();           // creator_fee
        _ = reader.ReadBool();                       // track_volume
        _ = reader.ReadU64();                        // total_unclaimed_tokens
        _ = reader.ReadU64();                        // total_claimed_tokens
        _ = reader.ReadU64();                        // current_sol_volume
        _ = reader.ReadI64();                        // last_update_timestamp
        reader.SkipString();                         // ix_name
        var mayhemMode = reader.ReadBool();          // mayhem_mode
        _ = reader.ReadU64();                        // cashback_fee_basis_points
        var cashback = reader.ReadU64();             // cashback
        _ = reader.ReadU64();                        // buyback_fee_basis_points
        var buybackFee = reader.ReadU64();           // buyback_fee
        reader.SkipVector(ShareholderSize);          // shareholders
        var quoteMint = reader.ReadPubkey();         // quote_mint
        var quoteAmount = reader.ReadU64();          // quote_amount
        var virtualQuoteReserves = reader.ReadU64(); // virtual_quote_reserves
        var realQuoteReserves = reader.ReadU64();    // real_quote_reserves
        _ = reader.ReadU64();                        // holder_rewards_bps
        var holderRewards = reader.ReadU64();        // holder_rewards
        _ = reader.ReadU64();                        // creator_fee_unclaimed
        var quoteTransferFee = reader.ReadU64();     // quote_transfer_fee

        var verdict = Verdict(ref reader);
        consumed = DiscriminatorLength + reader.Position;

        if (verdict != PumpDecodeFailure.None)
        {
            return verdict;
        }

        trade = new PumpTradeEvent
        {
            Mint = mint,
            User = user,
            Creator = creator,
            QuoteMint = quoteMint,
            IsBuy = isBuy,
            MayhemMode = mayhemMode,
            TimestampUnixSeconds = timestamp,
            SolAmount = solAmount,
            TokenAmount = tokenAmount,
            QuoteAmount = quoteAmount,
            VirtualSolReserves = virtualSolReserves,
            VirtualTokenReserves = virtualTokenReserves,
            RealSolReserves = realSolReserves,
            RealTokenReserves = realTokenReserves,
            VirtualQuoteReserves = virtualQuoteReserves,
            RealQuoteReserves = realQuoteReserves,
            Fee = fee,
            CreatorFee = creatorFee,
            Cashback = cashback,
            BuybackFee = buybackFee,
            HolderRewards = holderRewards,
            QuoteTransferFee = quoteTransferFee,
        };

        return PumpDecodeFailure.None;
    }

    /// <summary>
    /// Turns the reader's end state into a failure code.
    ///
    /// The reader reports which outcome happened because the final position
    /// cannot tell them apart: a bad bool in the last byte and a payload one
    /// byte short both stop with nothing left. An underrun means the payload is
    /// shorter than the layout, as when the protocol adds a field. An invalid
    /// value means the read was misaligned or the payload is hostile. A clean
    /// read that stopped early means the payload is longer than the layout.
    /// </summary>
    private static PumpDecodeFailure Verdict(ref BorshReader reader)
    {
        if (reader.ConsumedExactly)
        {
            return PumpDecodeFailure.None;
        }

        return reader.Failure switch
        {
            BorshFailure.Underrun => PumpDecodeFailure.Truncated,
            BorshFailure.InvalidValue => PumpDecodeFailure.MalformedValue,
            _ => PumpDecodeFailure.TrailingBytes,
        };
    }
}

/// <summary>
/// The result of decoding one Anchor payload: which event it was and, for
/// kinds this library decodes, the typed event.
///
/// A struct with one field per shape instead of a class hierarchy, so
/// decoding a trade does not allocate.
/// </summary>
public readonly record struct PumpDecodedEvent
{
    /// <summary>Which event the discriminator named.</summary>
    public PumpEventKind Kind { get; init; }

    /// <summary>The launch, when <see cref="Kind"/> is <see cref="PumpEventKind.Create"/>.</summary>
    public PumpCreateEvent? Create { get; init; }

    /// <summary>The trade, when <see cref="Kind"/> is <see cref="PumpEventKind.Trade"/>.</summary>
    public PumpTradeEvent Trade { get; init; }

    /// <summary>
    /// The migration, when <see cref="Kind"/> is
    /// <see cref="PumpEventKind.MigrationComplete"/>: the point at which the
    /// token stops trading on its bonding curve.
    /// </summary>
    public PumpMigrationEvent? Migration { get; init; }

    public PumpCompletionEvent? Completion { get; init; }

    /// <summary>True when a typed event is present.</summary>
    public bool HasTypedEvent =>
        Kind is PumpEventKind.Create or PumpEventKind.Trade or PumpEventKind.Complete or PumpEventKind.MigrationComplete;
}
