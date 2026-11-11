

namespace PumpSharp;

/// <summary>
/// Counts where the deployed protocol and the checked-in IDL disagree, in
/// enough detail to say what to change.
///
/// Two single counters are not enough. One "undecodable" count mixes causes
/// that need opposite fixes, and one "unknown discriminator" count includes
/// every PumpSwap event when a feed carries both programs. This type splits
/// both: failures by reason, undeclared discriminators by whether another
/// checked-in IDL declares them, and, for the layouts this library decodes,
/// the range of payload lengths and of the offset where the reader stopped.
///
/// Borsh is positional and has no field tags, so a layout mismatch always
/// sits at a byte offset that can be compared with field offsets from the
/// IDL:
///
/// <list type="bullet">
/// <item>Trailing bytes, same count every time: the program emits fields the
/// IDL does not declare. The count is the size of the addition.</item>
/// <item>Truncated, same stopping offset every time: this build expects
/// fields the program no longer emits. The offset names the field.</item>
/// <item>Either, at varying offsets: not a layout change. Look for a
/// variable-length field before the failure, or corrupt input.</item>
/// </list>
///
/// Memory use is fixed: one counter per failure reason and a min/max range
/// per decoded layout, so it can stay on in production.
/// </summary>
public sealed class PumpDriftTracker
{
    /// <summary>Slots for the failure counters. One past the highest enum value.</summary>
    internal const int FailureSlots = (int)PumpDecodeFailure.MalformedBase64 + 1;

    private readonly long[] _byFailure = new long[FailureSlots];

    private LayoutSpread _createFailures;
    private LayoutSpread _tradeTrailing;
    private LayoutSpread _tradeShort;
    private LayoutSpread _tradeSuccesses;

    private long _pumpSwapDeclared;
    private long _pumpFeesDeclared;
    private long _toleratedSwapEvents;
    private long _undeclaredAnywhere;

    /// <summary>
    /// The undeclared discriminators, with how often each was seen and how long
    /// its payloads were.
    ///
    /// A count shows that a disagreement exists. The bytes show what it is.
    ///
    /// Limited to <see cref="MaxTrackedDiscriminators"/> distinct values;
    /// anything beyond is counted as overflow. A feed with hundreds of distinct
    /// unknown discriminators is a finding in itself.
    /// </summary>
    private readonly Dictionary<ulong, DiscriminatorTally> _undeclared = [];

    private long _undeclaredOverflow;

    /// <summary>
    /// Distinct undeclared discriminators remembered. Pump declares 32 events
    /// and PumpSwap 27, so more than 64 unnamed kinds would not be clarified by
    /// listing them.
    /// </summary>
    public const int MaxTrackedDiscriminators = 64;

    /// <summary>
    /// Records one decode attempt, successful or not.
    ///
    /// Successful <c>TradeEvent</c> decodes are recorded too, so a failing
    /// length can be compared with a working one ("these payloads are N bytes
    /// longer than the ones that decode").
    /// </summary>
    public void Observe(in PumpDecodeDiagnostics diagnostics)
    {
        var failure = diagnostics.Failure;

        if (failure is PumpDecodeFailure.UnknownDiscriminator)
        {
            switch (diagnostics.DeclaredBy)
            {
                case PumpForeignProgram.PumpSwap:
                    _pumpSwapDeclared++;
                    break;

                case PumpForeignProgram.PumpFees:
                    _pumpFeesDeclared++;
                    break;

                // A known shape at its observed length, counted separately so a caller
                // need not halt on it. Any other length is still drift.
                case PumpForeignProgram.None when PumpUndeclaredEvents.IsToleratedSwapEvent(
                    diagnostics.Discriminator, diagnostics.PayloadBytes):
                    _toleratedSwapEvents++;
                    break;

                default:
                    _undeclaredAnywhere++;
                    RecordUndeclared(diagnostics);
                    break;
            }
        }

        if (failure is not PumpDecodeFailure.None)
        {
            _byFailure[(int)failure]++;
        }

        switch (diagnostics.Kind)
        {
            case PumpEventKind.Create when failure is not PumpDecodeFailure.None:
                _createFailures.Observe(diagnostics);
                break;

            // Over-long and short payloads are kept in separate ranges. Mixed together,
            // a bucket of 2,573 over-long and 60 short payloads reported a consumed
            // range of 57..451 and an implied overrun of up to 402 bytes, which no
            // single payload had. A range only means something over one cause.
            case PumpEventKind.Trade when failure is PumpDecodeFailure.TrailingBytes:
                _tradeTrailing.Observe(diagnostics);
                break;

            case PumpEventKind.Trade when failure is not PumpDecodeFailure.None:
                _tradeShort.Observe(diagnostics);
                break;

            case PumpEventKind.Trade:
                _tradeSuccesses.Observe(diagnostics);
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// The undeclared discriminators, most common first, with ties ordered by
    /// value so two runs over the same feed agree.
    /// </summary>
    private List<UndeclaredDiscriminator> ReadUndeclared()
    {
        var undeclared = new List<UndeclaredDiscriminator>(_undeclared.Count);

        foreach (var (discriminator, tally) in _undeclared)
        {
            undeclared.Add(new UndeclaredDiscriminator
            {
                Hex = Convert.ToHexStringLower(BitConverter.GetBytes(discriminator)),
                Count = tally.Count,
                MinPayloadBytes = tally.MinPayloadBytes,
                MaxPayloadBytes = tally.MaxPayloadBytes,
            });
        }

        undeclared.Sort((left, right) =>
        {
            var byCount = right.Count.CompareTo(left.Count);
            return byCount != 0
                ? byCount
                : string.CompareOrdinal(left.Hex, right.Hex);
        });

        return undeclared;
    }

    private void RecordUndeclared(in PumpDecodeDiagnostics diagnostics)
    {
        if (_undeclared.TryGetValue(diagnostics.Discriminator, out var tally))
        {
            tally.Observe(diagnostics.PayloadBytes);
            return;
        }

        if (_undeclared.Count >= MaxTrackedDiscriminators)
        {
            _undeclaredOverflow++;
            return;
        }

        var fresh = new DiscriminatorTally();
        fresh.Observe(diagnostics.PayloadBytes);
        _undeclared[diagnostics.Discriminator] = fresh;
    }

    /// <summary>The report, as a value, so a reader never sees it change mid-serialisation.</summary>
    public PumpDriftReport Report => new()
    {
        Truncated = _byFailure[(int)PumpDecodeFailure.Truncated],
        TrailingBytes = _byFailure[(int)PumpDecodeFailure.TrailingBytes],
        MalformedValue = _byFailure[(int)PumpDecodeFailure.MalformedValue],
        PayloadTooLarge = _byFailure[(int)PumpDecodeFailure.PayloadTooLarge],
        NotAnEventPayload = _byFailure[(int)PumpDecodeFailure.NotAnEventPayload],
        WrongEvent = _byFailure[(int)PumpDecodeFailure.WrongEvent],
        PumpSwapDeclared = _pumpSwapDeclared,
        PumpFeesDeclared = _pumpFeesDeclared,
        ToleratedSwapEvents = _toleratedSwapEvents,
        UndeclaredAnywhere = _undeclaredAnywhere,
        UndeclaredOverflow = _undeclaredOverflow,
        Undeclared = ReadUndeclared(),
        CreateFailures = _createFailures.Read(),
        TradeTrailing = _tradeTrailing.Read(),
        TradeShort = _tradeShort.Read(),
        TradeSuccesses = _tradeSuccesses.Read(),
    };

    /// <summary>
    /// The min/max range of one layout's payload length and stopping offset.
    ///
    /// Min and max are enough to answer the useful question, which is whether
    /// the offset is stable. A histogram would cost more and say no more.
    /// </summary>
    private struct LayoutSpread
    {
        private long _count;
        private int _minPayload;
        private int _maxPayload;
        private int _minConsumed;
        private int _maxConsumed;
        private int _minTrailing;
        private int _maxTrailing;

        public void Observe(in PumpDecodeDiagnostics diagnostics)
        {
            // The overrun is measured per payload. Deriving it from the min and max
            // above could report a difference no single payload had.
            var trailing = diagnostics.TrailingBytes;

            if (_count == 0)
            {
                _minPayload = diagnostics.PayloadBytes;
                _maxPayload = diagnostics.PayloadBytes;
                _minConsumed = diagnostics.ConsumedBytes;
                _maxConsumed = diagnostics.ConsumedBytes;
                _minTrailing = trailing;
                _maxTrailing = trailing;
            }
            else
            {
                _minPayload = Math.Min(_minPayload, diagnostics.PayloadBytes);
                _maxPayload = Math.Max(_maxPayload, diagnostics.PayloadBytes);
                _minConsumed = Math.Min(_minConsumed, diagnostics.ConsumedBytes);
                _maxConsumed = Math.Max(_maxConsumed, diagnostics.ConsumedBytes);
                _minTrailing = Math.Min(_minTrailing, trailing);
                _maxTrailing = Math.Max(_maxTrailing, trailing);
            }

            _count++;
        }

        public PumpLayoutObservation Read() => new()
        {
            Count = _count,
            MinPayloadBytes = _minPayload,
            MaxPayloadBytes = _maxPayload,
            MinConsumedBytes = _minConsumed,
            MaxConsumedBytes = _maxConsumed,
            MinTrailingBytes = _minTrailing,
            MaxTrailingBytes = _maxTrailing,
        };
    }
}

/// <summary>One layout's observed lengths and stopping offsets.</summary>
public readonly record struct PumpLayoutObservation
{
    /// <summary>Payloads observed in this bucket.</summary>
    public required long Count { get; init; }

    /// <summary>Shortest payload seen, discriminator included.</summary>
    public required int MinPayloadBytes { get; init; }

    /// <summary>Longest payload seen.</summary>
    public required int MaxPayloadBytes { get; init; }

    /// <summary>Lowest offset the reader stopped at.</summary>
    public required int MinConsumedBytes { get; init; }

    /// <summary>Highest offset the reader stopped at.</summary>
    public required int MaxConsumedBytes { get; init; }

    /// <summary>
    /// True when every payload in this bucket stopped at the same offset.
    ///
    /// The most useful bit in the report: a stable offset means a layout
    /// change, and a varying one does not.
    /// </summary>
    public bool ConsumedOffsetStable => Count > 0 && MinConsumedBytes == MaxConsumedBytes;

    /// <summary>
    /// Fewest bytes past the layout on any one payload.
    ///
    /// Measured per payload, not derived from the lengths above, which could
    /// compare two different payloads and report an overrun none of them had.
    /// </summary>
    public required int MinTrailingBytes { get; init; }

    /// <summary>Most bytes past the layout seen on any one payload.</summary>
    public required int MaxTrailingBytes { get; init; }

    /// <summary>
    /// True when every payload in this bucket overran by the same amount, which
    /// is what a layout grown by a fixed number of fields looks like.
    /// </summary>
    public bool TrailingStable => Count > 0 && MinTrailingBytes == MaxTrailingBytes;
}

/// <summary>
/// The protocol-disagreement report. Each field is a measurement, and
/// together they say what to change, not only that something is wrong.
/// </summary>
public readonly record struct PumpDriftReport
{
    /// <summary>Payloads shorter than the layout this build reads.</summary>
    public required long Truncated { get; init; }

    /// <summary>Payloads longer than the layout this build reads.</summary>
    public required long TrailingBytes { get; init; }

    /// <summary>Payloads holding a value Borsh does not allow: a misaligned read or hostile input.</summary>
    public required long MalformedValue { get; init; }

    /// <summary>Payloads above the decoder's ceiling, refused before decoding.</summary>
    public required long PayloadTooLarge { get; init; }

    /// <summary>Payloads that were not an Anchor event payload at all.</summary>
    public required long NotAnEventPayload { get; init; }

    /// <summary>A declared event decoded as the wrong one. Should be zero.</summary>
    public required long WrongEvent { get; init; }

    /// <summary>
    /// Not declared by the Pump IDL but declared by the PumpSwap IDL: another
    /// program's event, expected when a feed observes both programs, and not
    /// drift.
    /// </summary>
    public required long PumpSwapDeclared { get; init; }

    /// <summary>
    /// Declared by the Pump fee program, which Pump calls by CPI. Expected
    /// traffic, not drift, and kept separate for the same reason as PumpSwap.
    /// </summary>
    public required long PumpFeesDeclared { get; init; }

    /// <summary>
    /// Undeclared <c>SwapEvent</c> payloads of exactly the observed length.
    /// Named but not decoded, and kept out of <see cref="UndeclaredAnywhere"/>
    /// because the shape is known. See <see cref="PumpUndeclaredEvents"/>.
    /// </summary>
    public long ToleratedSwapEvents { get; init; }

    /// <summary>
    /// The undeclared discriminators, most common first. Bounded.
    ///
    /// A count shows that a disagreement exists; these show what it is.
    /// </summary>
    public required IReadOnlyList<UndeclaredDiscriminator> Undeclared { get; init; }

    /// <summary>Payloads whose discriminator did not fit the bounded tally.</summary>
    public required long UndeclaredOverflow { get; init; }

    /// <summary>
    /// Declared by neither checked-in IDL. This is the protocol-drift count.
    /// </summary>
    public required long UndeclaredAnywhere { get; init; }

    /// <summary>Failed <c>CreateEvent</c> payloads.</summary>
    public required PumpLayoutObservation CreateFailures { get; init; }

    /// <summary>
    /// <c>TradeEvent</c> payloads longer than the layout this build reads:
    /// the program emits more than the IDL declares.
    /// </summary>
    public required PumpLayoutObservation TradeTrailing { get; init; }

    /// <summary>
    /// <c>TradeEvent</c> payloads shorter than the layout this build reads, or
    /// malformed. Kept apart from the over-long ones because the causes are
    /// opposite, and one range over both would describe neither.
    /// </summary>
    public required PumpLayoutObservation TradeShort { get; init; }

    /// <summary>Successful <c>TradeEvent</c> payloads, for comparison with the failures.</summary>
    public required PumpLayoutObservation TradeSuccesses { get; init; }
}

/// <summary>One undeclared discriminator, as observed.</summary>
public readonly record struct UndeclaredDiscriminator
{
    /// <summary>
    /// The eight bytes as lower-case hex, in wire order.
    ///
    /// Wire order rather than the little-endian u64 used for lookup, because
    /// IDLs write discriminators as byte arrays and this value is meant to be
    /// compared with one.
    /// </summary>
    public required string Hex { get; init; }

    /// <summary>How many payloads carried it.</summary>
    public required long Count { get; init; }

    /// <summary>Shortest payload seen with it, discriminator included.</summary>
    public required int MinPayloadBytes { get; init; }

    /// <summary>Longest payload seen with it.</summary>
    public required int MaxPayloadBytes { get; init; }

    /// <summary>
    /// True when every payload carrying it had the same length, which suggests
    /// a fixed layout with no variable-length field.
    /// </summary>
    public bool FixedLength => Count > 0 && MinPayloadBytes == MaxPayloadBytes;
}

/// <summary>Mutable per-discriminator tally. A class so it updates in place.</summary>
internal sealed class DiscriminatorTally
{
    public long Count { get; private set; }

    public int MinPayloadBytes { get; private set; }

    public int MaxPayloadBytes { get; private set; }

    public void Observe(int payloadBytes)
    {
        if (Count == 0)
        {
            MinPayloadBytes = payloadBytes;
            MaxPayloadBytes = payloadBytes;
        }
        else
        {
            MinPayloadBytes = Math.Min(MinPayloadBytes, payloadBytes);
            MaxPayloadBytes = Math.Max(MaxPayloadBytes, payloadBytes);
        }

        Count++;
    }
}
