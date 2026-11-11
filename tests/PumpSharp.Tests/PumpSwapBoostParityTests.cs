using System.Globalization;
using System.Text.Json;
using PumpSharp;

namespace PumpSharp.Tests;

public class PumpSwapBoostParityTests
{
    private static ulong U64(JsonElement row, string key) => ulong.Parse(row.GetProperty(key).GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public void Signed_reserves_and_real_liquidity_match_the_executed_official_reference()
    {
        using var stream = typeof(PumpSwapBoostParityTests).Assembly.GetManifestResourceStream(
            "PumpSharp.Tests.parity.pump-swap-sdk-2.2.0-boost-vectors.json")!;
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Assert.Equal("@pump-fun/pump-swap-sdk", root.GetProperty("source").GetString());
        Assert.Equal("2.2.0", root.GetProperty("sourceVersion").GetString());
        Assert.Equal(454, root.GetProperty("vectorCount").GetInt32());
        var mismatches = new List<string>();
        var successes = 0;
        var refusals = 0;
        foreach (var row in root.GetProperty("vectors").EnumerateArray())
        {
            var virtualQuote = Int128.Parse(row.GetProperty("virtualQuote").GetString()!, CultureInfo.InvariantCulture);
            Assert.True(PumpSwapQuote.TryQuoteReserves(U64(row, "quoteVault"), virtualQuote, U64(row, "protocolFees"),
                U64(row, "creatorFees"), out var effective, out var real, out _));
            var fees = new PumpSwapFees { LpFeeBps = U64(row, "lpBps"), ProtocolFeeBps = U64(row, "protocolBps"),
                CreatorFeeBps = U64(row, "creatorBps"), HasCoinCreator = row.GetProperty("hasCreator").GetBoolean() };
            var tiers = root.GetProperty("feeSchedules").GetProperty(row.GetProperty("schedule").GetString()!)
                .EnumerateArray().Select(tier => new PumpSwapFeeTier
                {
                    MarketCapThreshold = UInt128.Parse(tier.GetProperty("threshold").GetString()!, CultureInfo.InvariantCulture),
                    Fees = new() { LpFeeBps = U64(tier, "lpBps"), ProtocolFeeBps = U64(tier, "protocolBps"),
                        CreatorFeeBps = U64(tier, "creatorBps"), HasCoinCreator = false },
                }).ToArray();
            var config = new PumpSwapFeeConfig { Bump = 0, Admin = default, FlatFees = tiers[0].Fees,
                FeeTiers = tiers, StableFeeTiers = [], ExoticFlatFees = default };
            var supply = row.GetProperty("state").GetString() == "settlement-boundary" ? 1000ul : 1_000_000ul;
            Assert.True(PumpSwapFeeSchedule.TryResolve(config, PumpSwapQuoteSchedule.SolLike, true,
                supply, U64(row, "baseReserve"), effective, false, 0, false, fees.HasCoinCreator, out var localFees, out _));
            if (localFees != fees) mismatches.Add("Effective-reserve fee tier disagrees with reference.");
            PumpSwapPoolQuote quote;
            PumpSwapQuoteRefusal reason;
            var success = row.GetProperty("method").GetString() == "buyBaseInput"
                ? PumpSwapQuote.TryQuoteBuyExactBase(U64(row, "baseReserve"), effective, U64(row, "amount"), fees, out quote, out reason)
                : PumpSwapQuote.TryQuoteSellExactBase(U64(row, "baseReserve"), effective, U64(row, "amount"), fees, out quote, out reason, real);
            if (row.TryGetProperty("error", out var error))
            {
                refusals++;
                if (success || error.GetString()!.Contains("real quote reserves", StringComparison.Ordinal)
                    && reason != PumpSwapQuoteRefusal.ExceedsRealReserves)
                    mismatches.Add($"{row.GetProperty("state").GetString()} {row.GetProperty("method").GetString()} {row.GetProperty("amount").GetString()}: expected refusal {error.GetString()}, got {reason}");
            }
            else
            {
                successes++;
                var expected = row.GetProperty("expected");
                if (!success || quote.NetQuoteUnits != U64(expected, "net") || quote.QuoteUnits != U64(expected, "total"))
                    mismatches.Add($"{row.GetProperty("state").GetString()} {row.GetProperty("method").GetString()} {row.GetProperty("amount").GetString()}: {reason}, net {quote.NetQuoteUnits}, total {quote.QuoteUnits}");
            }
        }
        Assert.Equal(339, successes);
        Assert.Equal(115, refusals);
        Assert.True(mismatches.Count == 0, string.Join('\n', mismatches));
        TestContext.Current.TestOutputHelper!.WriteLine($"454 executed official vectors: {successes} numeric successes, {refusals} refusals, {mismatches.Count} mismatches.");
    }

    [Fact]
    public void Untrusted_signed_state_cannot_overflow_or_spend_the_accrued_fees()
    {
        foreach (var value in new[] { Int128.MinValue, Int128.MaxValue, (Int128)(-1001), (Int128)ulong.MaxValue })
        {
            Assert.False(PumpSwapQuote.TryQuoteReserves(1000, value, 0, 0, out _, out _, out var refusal));
            Assert.Equal(PumpSwapQuoteRefusal.InvalidReserveState, refusal);
        }
        Assert.False(PumpSwapQuote.TryQuoteReserves(1000, 0, ulong.MaxValue, ulong.MaxValue, out _, out _, out _));
        Assert.False(PumpSwapQuote.TryQuoteReserves(1000, 0, 1000, 1, out _, out _, out _));
        Assert.True(PumpSwapQuote.TryQuoteReserves(ulong.MaxValue, 0, 0, 0, out var effective, out var real, out _));
        Assert.Equal(ulong.MaxValue, effective);
        Assert.Equal(ulong.MaxValue, real);
    }
}
