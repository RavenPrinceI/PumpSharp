namespace PumpSharp;

/// <summary>
/// Dense ordinals for <see cref="PumpEventKind"/>.
///
/// The enum values are sparse: <see cref="PumpEventKind.Unrecognised"/> is
/// 99 so new kinds can be added without renumbering. Counter arrays need a
/// dense index, or each would be 100 slots wide.
/// </summary>
public static class PumpEventKinds
{
    /// <summary>Every kind, in ordinal order, which is also the report order.</summary>
    public static IReadOnlyList<PumpEventKind> All { get; } =
    [
        PumpEventKind.None,
        PumpEventKind.Create,
        PumpEventKind.Trade,
        PumpEventKind.Complete,
        PumpEventKind.MigrationComplete,
        PumpEventKind.PumpSwapActivity,
        PumpEventKind.KnownOther,
        PumpEventKind.Unrecognised,
    ];

    /// <summary>Number of distinct kinds; the width of a counter array.</summary>
    public const int Count = 8;

    /// <summary>Dense index for a kind.</summary>
    public static int Ordinal(PumpEventKind kind) => kind switch
    {
        PumpEventKind.None => 0,
        PumpEventKind.Create => 1,
        PumpEventKind.Trade => 2,
        PumpEventKind.Complete => 3,
        PumpEventKind.MigrationComplete => 4,
        PumpEventKind.PumpSwapActivity => 5,
        PumpEventKind.KnownOther => 6,
        PumpEventKind.Unrecognised => 7,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown Pump event kind."),
    };

    /// <summary>Inverse of <see cref="Ordinal"/>.</summary>
    public static PumpEventKind FromOrdinal(int ordinal) =>
        ordinal >= 0 && ordinal < Count
            ? All[ordinal]
            : throw new ArgumentOutOfRangeException(nameof(ordinal), ordinal, "Out of range.");
}
