namespace PumpSharp;

/// <summary>
/// Tracks which program emitted each line of a transaction's logs, so a
/// <c>Program data:</c> line is attributed to the program that wrote it, not
/// to the program the subscription was filtered on.
///
/// A <c>logsSubscribe</c> filtered on <c>mentions</c> returns the whole
/// transaction's logs, including every program the transaction invoked. A
/// Pump buy routed through an aggregator carries the aggregator's logs in
/// the same array. Reading every <c>Program data:</c> line as a possible
/// Pump event caused three problems on a live feed:
///
/// <list type="bullet">
/// <item>About 6,500 payloads per ninety seconds were counted as protocol
/// drift. Many began with four-character ASCII tags such as <c>SCQT</c>,
/// <c>PRSM</c> and <c>QRT2</c>: other protocols' log formats, not Anchor
/// discriminators.</item>
/// <item>Several hundred payloads per run were too short to hold a
/// discriminator and were counted as truncated Pump events.</item>
/// <item>Foreign binary data reached the Pump decoder. A foreign line whose
/// first eight bytes matched a Pump discriminator would decode as a Pump
/// event; only the exact-length check stood in the way.</item>
/// </list>
///
/// Solana brackets each invocation in the log stream:
///
/// <code>
/// Program &lt;id&gt; invoke [1]
/// Program log: Instruction: Buy
/// Program data: &lt;base64&gt;
/// Program &lt;id&gt; success
/// </code>
///
/// The emitter of a line is the program at the top of the invoke stack.
/// This type keeps that stack as one bit per frame in a <see cref="ulong"/>,
/// set when the frame is a Pump-family program. The only question asked is
/// whether the innermost frame is Pump, so one bit per frame is enough, and
/// nothing allocates.
///
/// Two depth counters are not enough: they cannot represent
/// foreign, then Pump, then foreign, so a Pump event inside a routed trade
/// would be dropped. A test covers this case.
///
/// State is one integer and one bitmask, and depth is clamped to the number
/// of bits, so a malformed log stream cannot make it grow.
/// </summary>
public struct PumpLogScope
{
    /// <summary>Prefix of an invocation line.</summary>
    private const string ProgramPrefix = "Program ";

    /// <summary>What follows the program id when an invocation opens.</summary>
    private const string InvokeMarker = " invoke [";

    /// <summary>
    /// Deepest nesting tracked, one bit per frame. Solana allows four levels of
    /// CPI, so this is generous; the limit stops a malformed stream from
    /// driving the depth without bound.
    /// </summary>
    private const int MaxDepth = 64;

    private int _depth;
    private ulong _pumpFrames;
    private ulong _swapFrames;

    /// <summary>
    /// True when the innermost active program is one this library decodes, so
    /// a <c>Program data:</c> line seen now is a Pump-family event.
    ///
    /// Only the top frame is read. A Pump instruction that invokes an aggregator
    /// must not claim the aggregator's data lines, even though Pump is still
    /// lower on the stack, and an aggregator that invokes Pump must not hide
    /// Pump's own lines.
    /// </summary>
    public bool InPumpProgram => _depth > 0 && (_pumpFrames & (1UL << (_depth - 1))) != 0;

    /// <summary>
    /// The innermost program's id when it is Pump or PumpSwap, otherwise null.
    /// For diagnostics: it names the emitter of a sampled payload.
    /// </summary>
    public string? InnermostPumpProgram => !InPumpProgram
        ? null
        : (_swapFrames & (1UL << (_depth - 1))) != 0 ? PumpProgramIds.PumpSwap : PumpProgramIds.Pump;

    /// <summary>Current invocation depth, so a caller can tell which frame a log line belongs to.</summary>
    public readonly int Depth => _depth;

    /// <summary>
    /// True when no invocation marker has been seen.
    ///
    /// Lets a caller tell "this log stream has no invocation markers" apart from
    /// "the current program is foreign". The first means the provider omits the
    /// markers, and a caller should fall back to reading every line rather than
    /// dropping everything.
    /// </summary>
    public bool SawNoInvocation => !_sawAnyInvocation;

    private bool _sawAnyInvocation;

    /// <summary>
    /// Offers one log line to the scope. Returns true when the line was an
    /// invocation marker and carries no payload.
    /// </summary>
    public bool Consume(ReadOnlySpan<char> line)
    {
        if (!line.StartsWith(ProgramPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = line[ProgramPrefix.Length..];

        var invoke = rest.IndexOf(InvokeMarker, StringComparison.Ordinal);
        if (invoke > 0)
        {
            _sawAnyInvocation = true;
            Push(rest[..invoke]);
            return true;
        }

        // "Program <id> success" and "Program <id> failed: ..." both close an
        // invocation. "consumed ... compute units", "Program log:" and
        // "Program data:" do not.
        if (rest.EndsWith(" success", StringComparison.Ordinal)
            || rest.Contains(" failed", StringComparison.Ordinal))
        {
            Pop();
            return true;
        }

        return false;
    }

    private void Push(ReadOnlySpan<char> programId)
    {
        if (_depth >= MaxDepth)
        {
            return;
        }

        var swap = programId.Equals(PumpProgramIds.PumpSwap, StringComparison.Ordinal);
        var pump = swap || programId.Equals(PumpProgramIds.Pump, StringComparison.Ordinal);

        var frame = 1UL << _depth;
        _pumpFrames = pump ? _pumpFrames | frame : _pumpFrames & ~frame;
        _swapFrames = swap ? _swapFrames | frame : _swapFrames & ~frame;
        _depth++;
    }

    private void Pop()
    {
        if (_depth > 0)
        {
            _depth--;
        }
    }
}
