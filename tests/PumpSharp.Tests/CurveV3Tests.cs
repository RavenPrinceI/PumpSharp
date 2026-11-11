using System.Globalization;
using System.Text.Json;
using PumpSharp.Chain;

namespace PumpSharp.Tests;

public class CurveV3Tests
{
    [Fact]
    public void A_dust_budget_cannot_publish_a_zero_output_after_the_remaining_curve_supply_is_zero()
    {
        var state = new PumpCurveV3State(new() { VirtualBaseUnits = 1_000_000, VirtualQuoteUnits = 30_000,
            RealBaseUnits = 0, RealQuoteUnits = 1000, Basis = PumpReserveBasis.Quote }, 20, 30, 0, 30_000, 500, true, false, false);
        Assert.False(PumpCurveV3Quote.TryBaseForWalletBudget(state, 4, null, out var amount, out var refusal));
        Assert.Equal(0ul, amount);
        Assert.Equal(PumpCurveV3Refusal.Empty, refusal);
    }

    [Fact]
    public void Repeated_v3_quotes_have_no_managed_allocation_or_retained_leg_collection()
    {
        var state = new PumpCurveV3State(new() { VirtualBaseUnits = 1_000_000, VirtualQuoteUnits = 30_000,
            RealBaseUnits = 10_000, RealQuoteUnits = 1000, Basis = PumpReserveBasis.Quote }, 20, 30, 5000, 30_000, 500, true, false, false);
        var transfer = new TokenTransferFee(500, 1000);
        for (var i = 0; i < 100; i++)
        {
            _ = PumpCurveV3Quote.TryBuyExactBase(state, 10_001, transfer, out _, out _);
            _ = PumpCurveV3Quote.TryBaseForWalletBudget(state, 1000, transfer, out _, out _);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var succeeded = 0;
        for (var i = 0; i < 10_000; i++)
        {
            if (PumpCurveV3Quote.TryBuyExactBase(state, 10_001, transfer, out _, out _)) succeeded++;
            if (PumpCurveV3Quote.TryBaseForWalletBudget(state, 1000, transfer, out _, out _)) succeeded++;
        }
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(20_000, succeeded);
        Assert.Equal(0, allocated);
        TestContext.Current.TestOutputHelper!.WriteLine($"10,000 v3 exact-base/budget pairs: {allocated} managed bytes; {elapsed.TotalMilliseconds:F3} ms (warmed offline probe).");
    }

    [Fact]
    public void Exact_base_and_wallet_budget_quotes_match_the_executed_official_v3_reference()
    {
        using var vectors=JsonDocument.Parse(typeof(CurveV3Tests).Assembly.GetManifestResourceStream("PumpSharp.Tests.parity.curve-v3-vectors.json")!);
        var count=0; var refused=0; var crossing=0;
        foreach(var row in vectors.RootElement.GetProperty("cases").EnumerateArray())
        {
            var state=new PumpCurveV3State(new() { VirtualBaseUnits=1_000_000,VirtualQuoteUnits=30_000,
                RealBaseUnits=10_000,RealQuoteUnits=1000,Basis=PumpReserveBasis.Quote },20,
                row.GetProperty("creator").GetBoolean()?30ul:0,row.GetProperty("buyback").GetUInt64(),30_000,500,
                row.GetProperty("native").GetBoolean(),row.GetProperty("mayhem").GetBoolean(),false);
            var amount=ulong.Parse(row.GetProperty("amount").GetString()!,CultureInfo.InvariantCulture);
            var fee=new TokenTransferFee(row.GetProperty("bps").GetUInt16(),ulong.Parse(row.GetProperty("cap").GetString()!,CultureInfo.InvariantCulture));
            var expected=row.GetProperty("fits").GetBoolean();
            bool actual; ulong value;
            if(row.GetProperty("kind").GetString()=="base")
            {
                actual=PumpCurveV3Quote.TryBuyExactBase(state,amount,fee,out var quote,out _);
                value=quote.CashUnits;
                if(actual)
                {
                    Assert.Equal((UInt128)quote.CashUnits,(UInt128)quote.NetQuoteUnits+quote.FeeUnits+quote.TransferFeeUnits);
                    Assert.Equal(quote.BaseUnits,quote.CurveBaseUnits+quote.PoolBaseUnits);
                    if(quote.PoolBaseUnits>0) crossing++;
                }
            }
            else actual=PumpCurveV3Quote.TryBaseForWalletBudget(state,amount,fee,out value,out _);
            Assert.True(actual==expected,$"case {count}: {row}");
            if(actual) Assert.Equal(ulong.Parse(row.GetProperty("output").GetString()!,CultureInfo.InvariantCulture),value);
            else refused++;
            count++;
        }
        Assert.Equal(3024,count); Assert.True(crossing>0);
        TestContext.Current.TestOutputHelper!.WriteLine($"{count} official v3 exact-base/budget cases; {refused} expected refusals, {crossing} representable synthetic-migration buys; zero mismatches.");
    }
}
