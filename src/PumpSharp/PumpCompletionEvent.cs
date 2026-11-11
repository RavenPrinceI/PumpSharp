using PumpSharp.Chain;

namespace PumpSharp;

/// <summary>A decoded <c>CompleteEvent</c> from the checked-in Pump IDL.</summary>
public sealed record PumpCompletionEvent
{
    public required Pubkey User { get; init; }
    public required Pubkey Mint { get; init; }
    public required Pubkey BondingCurve { get; init; }
    public required long Timestamp { get; init; }
    public required Pubkey QuoteMint { get; init; }
}

/// <summary>Lifecycle facts about one token, kept separate from prices.</summary>
public readonly record struct PumpTokenLifecycle
{
    public required Pubkey Mint { get; init; }
    public Pubkey QuoteMint { get; init; }
    public bool CurveCompleted { get; init; }
    public Pubkey? Pool { get; init; }
    public ulong? MigrationSlot { get; init; }
    public bool Conflicted { get; init; }
    public ulong ObservedAtSlot { get; init; }
}
