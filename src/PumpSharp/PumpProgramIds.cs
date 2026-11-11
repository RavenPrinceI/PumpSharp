namespace PumpSharp;

/// <summary>
/// Program addresses, copied from Pump's official public docs (read 24
/// October 2026).
///
/// They are recorded in <see cref="PumpProtocolFingerprint"/>, so if Pump
/// deploys a program at a new address the fingerprint changes and callers
/// can stop until the change is reviewed.
/// </summary>
public static class PumpProgramIds
{
    /// <summary>The Pump bonding-curve program.</summary>
    public const string Pump = "6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P";

    /// <summary>The PumpSwap AMM program.</summary>
    public const string PumpSwap = "pAMMBay6oceH9fJKBRHGP5D4bD4sWpmSwMn52FMfXEA";

    /// <summary>Both programs, Pump first.</summary>
    public static IReadOnlyList<string> All { get; } = [Pump, PumpSwap];
}
