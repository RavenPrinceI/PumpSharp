namespace PumpSharp;

/// <summary>
/// The values from PumpSwap's GlobalConfig account that affect pricing. The
/// decoder reads past the recipient addresses without keeping them.
/// </summary>
public sealed record PumpSwapGlobalConfig
{
    public required ulong LpFeeBasisPoints { get; init; }
    public required ulong ProtocolFeeBasisPoints { get; init; }
    public required ulong CoinCreatorFeeBasisPoints { get; init; }
    public required byte DisableFlags { get; init; }
    public required bool MayhemModeEnabled { get; init; }
    public required bool IsCashbackEnabled { get; init; }
    public required ulong BuybackBasisPoints { get; init; }
    public required bool BoostEnabled { get; init; }
    public required bool CreatorFeeConfigurable { get; init; }
    public required ulong MaxConfigurableCreatorFeeBps { get; init; }

    // Bit meanings are declared on GlobalConfig.disable_flags in pump_amm.json.
    public bool BuyDisabled => (DisableFlags & (1 << 3)) != 0;
    public bool SellDisabled => (DisableFlags & (1 << 4)) != 0;
}
