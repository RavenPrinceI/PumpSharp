namespace PumpSharp;

/// <summary>
/// Event categories for counting a feed by type: trades plus the lifecycle
/// events in the official IDL (<c>createEvent</c>, <c>completeEvent</c> and
/// <c>completePumpAmmMigrationEvent</c>).
///
/// These are counts, not decodes; <see cref="PumpEventDecoder"/> reads
/// fields. <see cref="Unrecognised"/> keeps unmapped discriminators visible
/// instead of folding them into another bucket.
/// </summary>
public enum PumpEventKind
{
    /// <summary>A Pump program log carrying no decodable Anchor event payload.</summary>
    None = 0,

    /// <summary>Token launch (<c>createEvent</c>).</summary>
    Create = 1,

    /// <summary>Bonding-curve buy or sell (<c>tradeEvent</c>).</summary>
    Trade = 2,

    /// <summary>Curve completed (<c>completeEvent</c>).</summary>
    Complete = 3,

    /// <summary>Migration to PumpSwap finished (<c>completePumpAmmMigrationEvent</c>).</summary>
    MigrationComplete = 4,

    /// <summary>A PumpSwap pool event.</summary>
    PumpSwapActivity = 5,

    /// <summary>
    /// An event declared in the checked-in IDL that is not tracked individually,
    /// such as fee sweeps, admin operations and volume accumulator updates.
    ///
    /// Separate from <see cref="Unrecognised"/>. "Known but not tracked" and
    /// "not in the IDL" are different facts. The Pump IDL declares 32 events and
    /// four are tracked, so mixing them would hide drift among normal traffic.
    /// </summary>
    KnownOther = 6,

    /// <summary>
    /// An Anchor event payload whose discriminator is not in the IDL. A rising
    /// count here signals protocol drift.
    /// </summary>
    Unrecognised = 99,
}
