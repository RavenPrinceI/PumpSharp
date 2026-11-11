using PumpSharp.Chain;

namespace PumpSharp;

/// <summary>Curve-v3 inputs, all taken from one consistent set of account reads.</summary>
public readonly record struct PumpCurveV3State(PumpCurveReserves Reserves, ulong ProtocolFeeBps,
    ulong CreatorFeeBps, ulong BuybackBps, ulong BaseVaultUnits, ulong MigrationFeeUnits,
    bool NativeQuote, bool IsMayhemMode, bool Complete);

public enum PumpCurveV3Refusal { None, InvalidState, Empty, UnsupportedRange, EmptyPoolToBe, PoolDepleted, BudgetInsufficient }

public readonly record struct PumpCurveV3BuyQuote(ulong BaseUnits, ulong CurveBaseUnits, ulong CurveNetQuoteUnits,
    ulong PoolBaseUnits, ulong PoolNetQuoteUnits, ulong FeeUnits, ulong BuybackUnits, ulong TransferFeeUnits, ulong CashUnits)
{
    public ulong NetQuoteUnits => CurveNetQuoteUnits + PoolNetQuoteUnits;
}

/// <summary>Curve-v3 exact-base cost and exact-budget output, matching SDK
/// 4.3.1. The synthetic migration prices each leg separately at the curve's
/// fee rates. Intermediates use wider integers, and inputs the program
/// cannot handle are refused rather than wrapped.</summary>
public static class PumpCurveV3Quote
{
    public static bool TryBuyExactBase(in PumpCurveV3State state, ulong requestedBase,
        TokenTransferFee? transferFee, out PumpCurveV3BuyQuote quote, out PumpCurveV3Refusal refusal)
    {
        quote = default;
        refusal = Validate(state, transferFee);
        if (refusal != PumpCurveV3Refusal.None) return false;
        if (requestedBase == 0 || state.Complete || state.Reserves.VirtualBaseUnits == 0)
        { refusal = PumpCurveV3Refusal.Empty; return false; }
        var curveBase = Math.Min(requestedBase, state.Reserves.RealBaseUnits);
        if (!CurveCost(state.Reserves, curveBase, out var curveNet))
        { refusal = PumpCurveV3Refusal.UnsupportedRange; return false; }
        ulong poolBase = 0, poolNet = 0;
        if (requestedBase > curveBase && !state.IsMayhemMode)
        {
            if (!PoolToBe(state, curveBase, curveNet, out var poolB, out var poolQ))
            { refusal = PumpCurveV3Refusal.EmptyPoolToBe; return false; }
            poolBase = requestedBase - curveBase;
            if (poolBase >= poolB) { refusal = PumpCurveV3Refusal.PoolDepleted; return false; }
            var wide = ((UInt128)poolQ * poolBase + poolB - poolBase - 1) / (poolB - poolBase);
            if (wide > ulong.MaxValue) { refusal = PumpCurveV3Refusal.UnsupportedRange; return false; }
            poolNet = (ulong)wide;
        }
        var fee = Fees(state, curveNet) + Fees(state, poolNet);
        var buyback = Buyback(state, curveNet) + Buyback(state, poolNet);
        var net = (UInt128)curveNet + poolNet;
        var booked = net + fee;
        var cash = Gross(booked - buyback, transferFee) + buyback;
        if (net > ulong.MaxValue || fee > ulong.MaxValue || cash > ulong.MaxValue)
        { refusal = PumpCurveV3Refusal.UnsupportedRange; return false; }
        quote = new(curveBase + poolBase, curveBase, curveNet, poolBase, poolNet,
            (ulong)fee, (ulong)buyback, (ulong)(cash - booked), (ulong)cash);
        return true;
    }

    /// <summary>Matches getBuyV3TokenAmountFromQuoteAmount. A zero result
    /// cannot be executed (dust or a completed curve) and is reported as
    /// Empty.</summary>
    public static bool TryBaseForWalletBudget(in PumpCurveV3State state, ulong budget,
        TokenTransferFee? transferFee, out ulong baseUnits, out PumpCurveV3Refusal refusal)
    {
        baseUnits = 0;
        refusal = Validate(state, transferFee);
        if (refusal != PumpCurveV3Refusal.None) return false;
        if (budget == 0 || state.Complete || state.Reserves.VirtualBaseUnits == 0)
        { refusal = PumpCurveV3Refusal.Empty; return false; }
        var spendable = (UInt128)budget;
        if (transferFee is { } fee)
        {
            if (!fee.TryNet(budget, out var netBudget)) { refusal = PumpCurveV3Refusal.InvalidState; return false; }
            spendable = netBudget;
        }
        var net = BudgetNet(state, spendable);
        if (net <= 1) { refusal = PumpCurveV3Refusal.Empty; return false; }
        var tokens = TokensFor(state.Reserves.VirtualBaseUnits, state.Reserves.VirtualQuoteUnits, net);
        var remaining = state.Reserves.RealBaseUnits;
        if (tokens <= remaining || state.IsMayhemMode)
        {
            var debit = Debit(state, net, 0, transferFee);
            if (debit > budget)
            {
                var excess = debit - budget;
                if (excess >= net - 1) { refusal = PumpCurveV3Refusal.Empty; return false; }
                net -= excess;
                tokens = TokensFor(state.Reserves.VirtualBaseUnits, state.Reserves.VirtualQuoteUnits, net);
            }
            baseUnits = Math.Min(tokens, remaining);
            if (baseUnits == 0) { refusal = PumpCurveV3Refusal.Empty; return false; }
            return true;
        }
        if (!CurveCost(state.Reserves, remaining, out var curveNet))
        { refusal = PumpCurveV3Refusal.UnsupportedRange; return false; }
        if (Debit(state, curveNet, 0, transferFee) > budget)
        { refusal = PumpCurveV3Refusal.BudgetInsufficient; return false; }
        var curveCost = (UInt128)curveNet + Fees(state, curveNet);
        if (spendable <= curveCost) return NonEmpty(remaining, out baseUnits, out refusal);
        var left = spendable - curveCost;
        var afterFees = left * 10_000 / (10_000 + state.ProtocolFeeBps + state.CreatorFeeBps);
        if (afterFees < 2) return NonEmpty(remaining, out baseUnits, out refusal);
        var legNet = BudgetNet(state, left);
        if (!PoolToBe(state, remaining, curveNet, out var poolB, out var poolQ))
        { refusal = PumpCurveV3Refusal.EmptyPoolToBe; return false; }
        if (legNet <= 1) return NonEmpty(remaining, out baseUnits, out refusal);
        var legTokens = TokensFor(poolB, poolQ, legNet);
        var cost = Debit(state, curveNet, legNet, transferFee);
        if (cost > budget)
        {
            var excess = cost - budget;
            if (excess >= legNet - 1) return NonEmpty(remaining, out baseUnits, out refusal);
            legNet -= excess;
            legTokens = TokensFor(poolB, poolQ, legNet);
            // With valid rates, debit(n) - n never decreases as n grows, so removing
            // the whole excess needs one correction. That is checked here instead of
            // copying the SDK's open-ended loop.
            if (Debit(state, curveNet, legNet, transferFee) > budget)
            { refusal = PumpCurveV3Refusal.BudgetInsufficient; return false; }
        }
        var total = (UInt128)remaining + legTokens;
        if (total > ulong.MaxValue) { refusal = PumpCurveV3Refusal.UnsupportedRange; return false; }
        return NonEmpty((ulong)total, out baseUnits, out refusal);
    }

    private static bool NonEmpty(ulong value, out ulong baseUnits, out PumpCurveV3Refusal refusal)
    {
        baseUnits = value;
        refusal = value == 0 ? PumpCurveV3Refusal.Empty : PumpCurveV3Refusal.None;
        return value != 0;
    }

    private static PumpCurveV3Refusal Validate(in PumpCurveV3State state, TokenTransferFee? fee) =>
        state.ProtocolFeeBps > 10_000 || state.CreatorFeeBps > 10_000 || state.BuybackBps > 10_000
            || (fee is { IsValid: false }) || state.Reserves.HasNegativeSignedQuoteReserves
            ? PumpCurveV3Refusal.InvalidState : PumpCurveV3Refusal.None;
    private static UInt128 Fees(in PumpCurveV3State state, UInt128 net) =>
        (net * state.ProtocolFeeBps + 9999) / 10_000 + (net * state.CreatorFeeBps + 9999) / 10_000;
    private static UInt128 Buyback(in PumpCurveV3State state, UInt128 net) => state.IsMayhemMode ? 0
        : ((net * state.ProtocolFeeBps + 9999) / 10_000) * state.BuybackBps / 10_000;
    private static UInt128 Debit(in PumpCurveV3State state, UInt128 curveNet, UInt128 legNet, TokenTransferFee? fee)
    {
        var buyback = Buyback(state, curveNet) + Buyback(state, legNet);
        return Gross(curveNet + legNet + Fees(state, curveNet) + Fees(state, legNet) - buyback, fee) + buyback;
    }
    private static UInt128 Gross(UInt128 net, TokenTransferFee? fee)
    {
        if (fee is not { } transfer || transfer.BasisPoints == 0 || net == 0) return net;
        if (transfer.BasisPoints == 10_000) return net + transfer.MaximumFee;
        var raw = (net * 10_000 + (uint)(9999 - transfer.BasisPoints)) / (uint)(10_000 - transfer.BasisPoints);
        return raw - net >= transfer.MaximumFee ? net + transfer.MaximumFee : raw;
    }
    private static UInt128 BudgetNet(in PumpCurveV3State state, UInt128 budget)
    {
        var net = budget * 10_000 / (10_000 + state.ProtocolFeeBps + state.CreatorFeeBps);
        var cost = net + Fees(state, net);
        return cost > budget ? cost - budget >= net ? 0 : net - (cost - budget) : net;
    }
    private static ulong TokensFor(ulong baseReserve, ulong quoteReserve, UInt128 net)
    {
        if (net <= 1) return 0;
        return (ulong)((net - 1) * baseReserve / ((UInt128)quoteReserve + net - 1));
    }
    private static bool CurveCost(in PumpCurveReserves reserves, ulong tokens, out ulong cost)
    {
        cost = 0;
        if (tokens >= reserves.VirtualBaseUnits) return false;
        var wide = (UInt128)tokens * reserves.VirtualQuoteUnits / (reserves.VirtualBaseUnits - tokens) + 1;
        if (wide > ulong.MaxValue) return false;
        cost = (ulong)wide;
        return true;
    }
    private static bool PoolToBe(in PumpCurveV3State state, ulong curveTokens, ulong curveNet, out ulong baseReserve, out ulong quoteReserve)
    {
        baseReserve = 0; quoteReserve = 0;
        var raised = (UInt128)state.Reserves.RealQuoteUnits + curveNet;
        var migration = state.NativeQuote ? state.MigrationFeeUnits : 0;
        if (state.BaseVaultUnits <= curveTokens || raised <= migration || raised - migration > ulong.MaxValue) return false;
        baseReserve = state.BaseVaultUnits - curveTokens; quoteReserve = (ulong)(raised - migration);
        return true;
    }
}
