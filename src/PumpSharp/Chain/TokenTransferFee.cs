namespace PumpSharp.Chain;

/// <summary>One Token-2022 per-transfer fee, in the transferred token's raw
/// units. It does not include protocol fees, network fees or any currency
/// conversion. Reference: token-2022
/// interface/src/extension/transfer_fee/mod.rs. Checked against both
/// official Pump SDKs.</summary>
public readonly record struct TokenTransferFee(ushort BasisPoints, ulong MaximumFee)
{
    public bool IsValid => BasisPoints <= 10_000;

    public bool TryWithheld(ulong sent, out ulong withheld)
    {
        withheld = 0;
        if (!IsValid) return false;
        var rounded = ((UInt128)sent * BasisPoints + 9_999) / 10_000;
        withheld = (ulong)UInt128.Min(rounded, MaximumFee);
        return true;
    }

    public bool TryNet(ulong sent, out ulong received)
    {
        received = 0;
        if (!TryWithheld(sent, out var withheld)) return false;
        received = sent - withheld;
        return true;
    }

    /// <summary>Matches the official pre-fee amount, including its rounding.
    /// Results that do not fit in a u64 are refused, not clamped.</summary>
    public bool TryGrossForNet(ulong received, out ulong sent)
    {
        sent = 0;
        if (!IsValid) return false;
        if (BasisPoints == 0 || received == 0) { sent = received; return true; }
        UInt128 gross;
        if (BasisPoints == 10_000)
            gross = (UInt128)received + MaximumFee;
        else
        {
            var denominator = (uint)(10_000 - BasisPoints);
            var uncapped = ((UInt128)received * 10_000 + denominator - 1) / denominator;
            gross = uncapped - received >= MaximumFee ? (UInt128)received + MaximumFee : uncapped;
        }
        if (gross > ulong.MaxValue) return false;
        sent = (ulong)gross;
        return true;
    }
}

public readonly record struct TokenTransferFeeEntry(ulong Epoch, TokenTransferFee Fee);

/// <summary>The mint's two scheduled rates. The caller must pass the chain
/// epoch from the same account reads; the local clock is not an epoch.
/// Keeping both entries stops a future rate from being applied early.</summary>
public readonly record struct TokenTransferFeeSchedule(TokenTransferFeeEntry Older, TokenTransferFeeEntry Newer)
{
    public TokenTransferFee AtEpoch(ulong epoch) => epoch >= Newer.Epoch ? Newer.Fee : Older.Fee;
}
