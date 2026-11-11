using PumpSharp.Chain;

namespace PumpSharp;

public readonly record struct PumpCurveFees(ulong ProtocolFeeBps, ulong CreatorFeeBps, bool CreatorFeeEnabled);

/// <summary>Selects curve fees from the shared fee-program account layout.
/// Unlike pools, ordinary curves use the fixed supply and mayhem curves use
/// the actual mint supply. Both rules are checked against the official
/// SDKs.</summary>
public static class PumpCurveFeeSchedule
{
    public const ulong OrdinarySupplyUnits = 1_000_000_000_000_000;

    public static bool TryResolve(PumpSwapFeeConfig config, Pubkey quoteMint, in PumpCurveReserves reserves,
        ulong mintSupplyUnits, bool isMayhemMode, ulong creatorFeeBps, bool creatorFeeConfigurable,
        bool hasCreator, out PumpCurveFees fees, out PumpSwapFeeScheduleRefusal refusal)
    {
        fees = default;
        if (!PumpSwapFeeSchedule.TryResolve(config, PumpSwapFeeSchedule.ScheduleFor(quoteMint), true,
            isMayhemMode ? mintSupplyUnits : OrdinarySupplyUnits, reserves.VirtualBaseUnits, reserves.VirtualQuoteUnits,
            isMayhemMode: false, creatorFeeBps, creatorFeeConfigurable, hasCreator, out var resolved, out refusal))
            return false;
        // Curves do not charge the LP fee in the shared layout.
        fees = new(resolved.ProtocolFeeBps, resolved.CreatorFeeBps, hasCreator);
        return true;
    }
}
