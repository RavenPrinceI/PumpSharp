namespace PumpSharp;

/// <summary>The values from Pump's Global account that affect pricing. The
/// recipient arrays are read past in IDL order and not kept.</summary>
public sealed record PumpGlobalConfig(ulong FeeBasisPoints, ulong CreatorFeeBasisPoints,
    ulong BuybackBasisPoints, bool CreatorFeeConfigurable, ulong MaxConfigurableCreatorFeeBps,
    ulong PoolMigrationFee, bool MayhemModeEnabled);
