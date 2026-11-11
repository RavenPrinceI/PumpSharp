namespace PumpSharp;

/// <summary>
/// What a decode attempt observed, successful or not: enough to locate a
/// protocol disagreement, not just count it.
///
/// The decoder refuses any payload it cannot consume exactly. On an early
/// live run, every <c>TradeEvent</c> was refused while <c>CreateEvent</c>
/// decoded. The refusal was correct, but a count alone could not explain
/// it, because <see cref="PumpDecodeFailure.Truncated"/> and
/// <see cref="PumpDecodeFailure.TrailingBytes"/> have opposite causes:
///
/// <list type="bullet">
/// <item>Trailing bytes: the program emits more than the checked-in IDL
/// declares. The IDL is behind, and the extra byte count is the size of the
/// addition.</item>
/// <item>Truncated: this build expects fields the program does not emit. The
/// IDL is ahead of the program or a field was transcribed wrongly, and the
/// consumed offset shows where the read stopped.</item>
/// </list>
///
/// Borsh is positional, so a layout mismatch always sits at a byte offset.
/// <see cref="ConsumedBytes"/> is where the reader stopped, counted from the
/// start of the payload including the discriminator, so it can be compared
/// directly with field offsets worked out from the IDL. The same offset
/// across many payloads means a changed layout. Varying offsets mean
/// corruption or a variable-length field.
///
/// A feed that carries both Pump and PumpSwap, read with only the Pump IDL,
/// sees every PumpSwap event as undeclared. On one live run that was about
/// 78% of the drift count. <see cref="DeclaredBy"/> separates other
/// programs' events, which are expected, from discriminators no checked-in
/// IDL declares, which are drift.
/// </summary>
public readonly record struct PumpDecodeDiagnostics
{
    /// <summary>The event kind, as far as the discriminator could say.</summary>
    public required PumpEventKind Kind { get; init; }

    /// <summary>How the attempt ended. <see cref="PumpDecodeFailure.None"/> for a clean read.</summary>
    public required PumpDecodeFailure Failure { get; init; }

    /// <summary>The whole payload's length, discriminator included.</summary>
    public required int PayloadBytes { get; init; }

    /// <summary>
    /// The eight discriminator bytes read as a little-endian u64, or zero when
    /// the payload is too short to hold one.
    ///
    /// Kept so an undeclared discriminator can be identified, not just counted:
    /// it can be matched against another program's IDL, an instruction
    /// discriminator, or sha256("event:" + Name) for a guessed name.
    /// </summary>
    public ulong Discriminator { get; init; }

    /// <summary>
    /// Offset the reader reached, discriminator included. Equal to
    /// <see cref="PayloadBytes"/> on a clean read.
    /// </summary>
    public required int ConsumedBytes { get; init; }

    /// <summary>
    /// Which other checked-in program declares this discriminator, when the Pump
    /// IDL does not.
    ///
    /// Anything other than <see cref="PumpForeignProgram.None"/> is another
    /// program's event in a Pump transaction's logs. That is expected: feeds
    /// often include PumpSwap, and Pump calls the fee program by CPI. Only
    /// <see cref="PumpForeignProgram.None"/> is drift.
    /// </summary>
    public PumpForeignProgram DeclaredBy { get; init; }

    /// <summary>
    /// Bytes past the layout this build read. Positive means the deployed
    /// program emits more than the checked-in IDL declares.
    /// </summary>
    public int TrailingBytes => PayloadBytes > ConsumedBytes ? PayloadBytes - ConsumedBytes : 0;
}

/// <summary>
/// A checked-in program other than Pump that declares a discriminator.
/// </summary>
/// <remarks>
/// Three programs log into a Pump transaction and the decoder reads one of
/// them. On one live feed, PumpSwap alone made up about 85% of the
/// "undeclared" discriminators, so the drift count was mostly another
/// program's normal traffic. Naming the other programs keeps the drift count
/// meaningful.
/// </remarks>
public enum PumpForeignProgram
{
    /// <summary>
    /// No checked-in IDL declares it. This is the drift case: either Pump
    /// shipped something new or an IDL is out of date.
    /// </summary>
    None = 0,

    /// <summary>The PumpSwap AMM, often subscribed to alongside Pump.</summary>
    PumpSwap = 1,

    /// <summary>The Pump fee program, reached by CPI from Pump.</summary>
    PumpFees = 2,
}
