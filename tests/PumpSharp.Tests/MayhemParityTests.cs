using System.Globalization;
using System.Text.Json;
using PumpSharp;
using PumpSharp.Chain;

namespace PumpSharp.Tests;

public class MayhemParityTests
{
    private static JsonDocument Read()
    {
        using var stream = typeof(MayhemParityTests).Assembly.GetManifestResourceStream("PumpSharp.Tests.parity.mayhem-sdk-vectors.json")!;
        return JsonDocument.Parse(stream);
    }
    private static ulong Number(JsonElement row, string key) => ulong.Parse(row.GetProperty(key).GetString()!, CultureInfo.InvariantCulture);
    private static PumpSwapFees Rates(ulong lp, ulong protocol, ulong creator) => new()
        { LpFeeBps = lp, ProtocolFeeBps = protocol, CreatorFeeBps = creator, HasCoinCreator = false };
    private static PumpSwapFeeTier[] Tiers(JsonElement root, string key) => root.GetProperty(key).EnumerateArray().Select(row =>
        new PumpSwapFeeTier { MarketCapThreshold = UInt128.Parse(row.GetProperty("threshold").GetString()!, CultureInfo.InvariantCulture),
            Fees = Rates(Number(row, "lp"), Number(row, "protocol"), Number(row, "creator")) }).ToArray();
    private static PumpSwapFeeConfig Config(JsonElement root, bool legacy) => new()
    {
        Admin = default, Bump = 0, FlatFees = Rates(15,6,3), ExoticFlatFees = Rates(40,4,4),
        FeeTiers = Tiers(root, "solTiers"), StableFeeTiers = legacy ? [] : Tiers(root, "stableTiers"),
    };
    private static PumpSwapQuoteSchedule Schedule(JsonElement row) => row.GetProperty("quote").GetString() switch
        { "sol" => PumpSwapQuoteSchedule.SolLike, "stable" => PumpSwapQuoteSchedule.Stable, _ => PumpSwapQuoteSchedule.Exotic };

    [Fact]
    public void Pool_fees_and_both_quote_directions_match_discriminating_reference_cases()
    {
        using var document = Read();
        var root = document.RootElement;
        Assert.Equal("2.2.0", root.GetProperty("swapVersion").GetString());
        var normal = Config(root, false);
        var legacy = Config(root, true);
        var mismatches = new List<string>();
        var count = 0;
        foreach (var row in root.GetProperty("poolCases").EnumerateArray())
        {
            count++;
            var resolved = PumpSwapFeeSchedule.TryResolve(row.GetProperty("legacy").GetBoolean() ? legacy : normal,
                Schedule(row), row.GetProperty("canonical").GetBoolean(), Number(row,"supply"), Number(row,"baseReserve"),
                Number(row,"quoteReserve"), row.GetProperty("mayhem").GetBoolean(), Number(row,"creatorBps"),
                row.GetProperty("configurable").GetBoolean(), true, out var fees, out var refusal);
            if (!resolved || fees.LpFeeBps != Number(row,"lpBps") || fees.ProtocolFeeBps != Number(row,"protocolBps")
                || fees.CreatorFeeBps != Number(row,"resolvedCreatorBps"))
            {
                mismatches.Add($"case {count}: fee mismatch, refusal {refusal}");
                continue;
            }
            if (!PumpSwapQuote.TryQuoteBuyExactBase(Number(row,"baseReserve"), Number(row,"quoteReserve"), 500_000,
                fees, out var buy, out _) || buy.NetQuoteUnits != Number(row,"buyNet") || buy.QuoteUnits != Number(row,"buyTotal"))
                mismatches.Add($"case {count}: buy mismatch");
            if (!PumpSwapQuote.TryQuoteSellExactBase(Number(row,"baseReserve"), Number(row,"quoteReserve"), 500_000,
                fees, out var sell, out _) || sell.NetQuoteUnits != Number(row,"sellNet") || sell.QuoteUnits != Number(row,"sellTotal"))
                mismatches.Add($"case {count}: sell mismatch");
        }
        Assert.Equal(864, count);
        TestContext.Current.TestOutputHelper!.WriteLine($"Pool reference cases {count}; unexplained mismatches {mismatches.Count}.");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches: " + string.Join('\n', mismatches.Take(8)));
    }

    [Fact]
    public void Curve_fee_components_and_totals_match_actual_supply_on_mayhem_and_fixed_supply_otherwise()
    {
        using var document = Read();
        var root = document.RootElement;
        var normal = Config(root, false);
        var legacy = Config(root, true);
        var mismatches = new List<string>();
        var count = 0;
        foreach (var row in root.GetProperty("curveCases").EnumerateArray())
        {
            count++;
            var mintText = row.GetProperty("quote").GetString() switch
                { "sol" => PumpQuoteMints.WrappedSol, "stable" => PumpQuoteMints.Usdc, _ => "CktRuQ2mttgRGkXJtyksdKHjUdc2C4TgDzyB98oEzy8" };
            Assert.True(Pubkey.TryParse(mintText, out var quoteMint));
            var reserves = new PumpCurveReserves { VirtualBaseUnits = Number(row,"baseReserve"), VirtualQuoteUnits = Number(row,"quoteReserve"),
                RealBaseUnits = 800_000, RealQuoteUnits = Number(row,"quoteReserve"), Basis = PumpReserveBasis.Sol };
            var enabled = row.GetProperty("hasCreator").GetBoolean();
            var resolved = PumpCurveFeeSchedule.TryResolve(row.GetProperty("legacy").GetBoolean() ? legacy : normal,
                quoteMint, reserves, Number(row,"supply"), row.GetProperty("mayhem").GetBoolean(), Number(row,"creatorBps"),
                row.GetProperty("configurable").GetBoolean(), enabled, out var fees, out var refusal);
            if (!resolved || fees.ProtocolFeeBps != Number(row,"protocolBps") || fees.CreatorFeeBps != Number(row,"resolvedCreatorBps")
                || fees.CreatorFeeEnabled != enabled)
            {
                mismatches.Add($"case {count}: curve fees differ, refusal {refusal}");
                continue;
            }
            foreach (var expected in row.GetProperty("feeTotals").EnumerateArray())
            {
                var amount = Number(expected,"amount");
                Assert.True(PumpBondingCurve.TryFee(amount, fees.ProtocolFeeBps, PumpFeeRounding.Up, out var protocol));
                Assert.True(PumpBondingCurve.TryFee(amount, enabled ? fees.CreatorFeeBps : 0, PumpFeeRounding.Up, out var creator));
                if (protocol + creator != Number(expected,"fee")) mismatches.Add($"case {count}: fee total differs");
            }
        }
        Assert.Equal(864, count);
        TestContext.Current.TestOutputHelper!.WriteLine($"Curve reference cases {count}, fee totals {count * 3}; unexplained mismatches {mismatches.Count}.");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} mismatches: " + string.Join('\n', mismatches.Take(8)));
    }

    [Fact]
    public void The_fixture_discriminates_opposite_supply_rules_in_both_venues()
    {
        using var document = Read();
        var root = document.RootElement;
        JsonElement Example(string set, bool mayhem) => root.GetProperty(set).EnumerateArray().First(row =>
            row.GetProperty("mayhem").GetBoolean() == mayhem && row.GetProperty("supply").GetString() == "1000000"
            && row.GetProperty("quoteReserve").GetString() == "1000" && row.GetProperty("quote").GetString() == "sol"
            && !row.GetProperty("configurable").GetBoolean() && !row.GetProperty("legacy").GetBoolean()
            && (set == "poolCases" ? row.GetProperty("canonical").GetBoolean() : row.GetProperty("hasCreator").GetBoolean()));
        Assert.Equal(20ul, Number(Example("poolCases", false), "protocolBps"));
        Assert.Equal(40ul, Number(Example("poolCases", true), "protocolBps"));
        Assert.Equal(40ul, Number(Example("curveCases", false), "protocolBps"));
        Assert.Equal(20ul, Number(Example("curveCases", true), "protocolBps"));
        Assert.Equal(1_000_000_000_000_000ul, PumpSwapFeeSchedule.MayhemSupplyUnits);
        Assert.Equal(1_000_000_000_000_000ul, PumpCurveFeeSchedule.OrdinarySupplyUnits);
    }

    [Fact]
    public void Fee_and_budget_extremes_cannot_wrap_into_plausible_quotes()
    {
        var invalid = new PumpSwapFees { LpFeeBps = ulong.MaxValue, ProtocolFeeBps = 1, CreatorFeeBps = 1, HasCoinCreator = true };
        Assert.True(invalid.TotalBps > ulong.MaxValue);
        Assert.False(PumpSwapQuote.TryQuoteBuyExactBase(10000,10000,5000,invalid,out _,out var refusal));
        Assert.Equal(PumpSwapQuoteRefusal.FeeRateOutOfRange, refusal);
        Assert.Throws<OverflowException>(() => PumpSwapQuote.ComposeFee(10000, invalid));
        Assert.True(PumpSwapQuote.TryQuoteBuyExactQuote(10000,10000,ulong.MaxValue,PumpSwapFees.None,out var buy,out _));
        Assert.Equal(ulong.MaxValue, buy.NetQuoteUnits);
        Assert.Equal(9999ul, buy.BaseUnits);
        var dust = Rates(20,5,5) with { HasCoinCreator = true };
        Assert.True(PumpSwapQuote.TryQuoteSellExactQuote(10000,10000,1,dust,out var sell,out _));
        Assert.True(sell.NetQuoteUnits >= sell.FeeUnits);
        Assert.True(sell.NetQuoteUnits - PumpSwapQuote.ComposeFee(sell.NetQuoteUnits, dust) >= 1);
    }

    [Fact]
    public void Mayhem_does_not_change_the_curve_core_formula_or_real_reserve_limit()
    {
        using var document = Read();
        Assert.Equal("4.3.1", document.RootElement.GetProperty("pumpVersion").GetString());
        var count = 0;
        foreach (var row in document.RootElement.GetProperty("curveMath").EnumerateArray())
        {
            var reserves = new PumpCurveReserves { VirtualBaseUnits = Number(row,"virtualBase"), VirtualQuoteUnits = Number(row,"virtualQuote"),
                RealBaseUnits = Number(row,"realBase"), RealQuoteUnits = Number(row,"realQuote"), Basis = PumpReserveBasis.Sol };
            Assert.True(PumpBondingCurve.TryQuoteBuy(reserves, Number(row,"amount"), out var buy));
            Assert.True(PumpBondingCurve.TryQuoteSell(reserves, Number(row,"amount"), out var sell));
            Assert.Equal(Number(row,"buyBase"), buy.AmountOutUnits);
            Assert.Equal(Number(row,"sellQuote"), sell.AmountOutUnits);
            Assert.Equal(sell.AmountOutUnits > reserves.RealQuoteUnits, sell.ExceedsRealReserves);
            count++;
        }
        Assert.Equal(30, count);
    }
}
