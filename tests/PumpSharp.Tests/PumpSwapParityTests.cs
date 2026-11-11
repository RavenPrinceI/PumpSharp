using System.Globalization;
using System.Text;
using System.Text.Json;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// Parity for PumpSwap pool quotes against the official PumpSwap SDK.
///
/// Every expected value in <c>parity/pump-swap-sdk-2.2.0-vectors.json</c>
/// was produced by running <c>@pump-fun/pump-swap-sdk</c> 2.2.0 with the
/// script next to it, not by copying its formulas.
///
/// Mismatches are collected and reported together, so a reader can tell a
/// systematic difference from an isolated one.
///
/// Two directions are expected to differ by one unit. The SDK's
/// <c>buyQuoteInput</c> prices on <c>budget - 1</c>, and its gross-up for
/// <c>sellQuoteInput</c> lands one unit short of the request. The SDK's own
/// documentation places that shave in its budget fitting ("shaved until
/// net + fees fits the budget"), not in the program's arithmetic. These two
/// directions are therefore checked against that documented relationship
/// instead of equality, and any other difference fails.
/// </summary>
public class PumpSwapParityTests
{
    private static readonly Lazy<JsonDocument> Document = new(() =>
    {
        using var stream = typeof(PumpSwapParityTests).Assembly
            .GetManifestResourceStream("PumpSharp.Tests.parity.pump-swap-sdk-2.2.0-vectors.json")
            ?? throw new InvalidOperationException(
                "the PumpSwap parity vectors are not embedded; parity cannot be inferred");

        return JsonDocument.Parse(stream);
    });

    private static JsonElement Root => Document.Value.RootElement;

    private static IEnumerable<JsonElement> Vectors =>
        Root.GetProperty("vectors").EnumerateArray();

    private static ulong U64(JsonElement vector, string name) =>
        ulong.Parse(vector.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    private static bool Has(JsonElement vector, string name) =>
        vector.TryGetProperty(name, out var value) && value.ValueKind is not JsonValueKind.Null;

    /// <summary>Rebuilds the fee config a schedule vector was generated against.</summary>
    private static PumpSwapFeeConfig ConfigOf(JsonElement vector) => new()
    {
        Bump = 0,
        Admin = default,
        FlatFees = Rates(
            U64(vector, "flatLpFeeBps"),
            U64(vector, "flatProtocolFeeBps"),
            U64(vector, "flatCreatorFeeBps")),
        FeeTiers = TiersOf(vector, "tiers"),
        StableFeeTiers = TiersOf(vector, "stableTiers"),

        // The generator sets the exotic schedule only for the exotic cases. All
        // zeroes is how the program marks it as unset.
        ExoticFlatFees = vector.GetProperty("exoticSet").GetBoolean()
            ? Rates(40, 4, 4)
            : Rates(0, 0, 0),
    };

    private static PumpSwapFees Rates(ulong lp, ulong protocol, ulong creator) => new()
    {
        LpFeeBps = lp,
        ProtocolFeeBps = protocol,
        CreatorFeeBps = creator,
        HasCoinCreator = false,
    };

    private static PumpSwapFeeTier[] TiersOf(JsonElement vector, string property) =>
        [.. vector.GetProperty(property).EnumerateArray().Select(tier => new PumpSwapFeeTier
        {
            MarketCapThreshold = UInt128.Parse(
                tier.GetProperty("thresholdLamports").GetString()!,
                CultureInfo.InvariantCulture),
            Fees = Rates(
                ulong.Parse(tier.GetProperty("lpFeeBps").GetString()!, CultureInfo.InvariantCulture),
                ulong.Parse(tier.GetProperty("protocolFeeBps").GetString()!, CultureInfo.InvariantCulture),
                ulong.Parse(tier.GetProperty("creatorFeeBps").GetString()!, CultureInfo.InvariantCulture)),
        })];

    private static PumpSwapFees FeesOf(JsonElement vector) => new()
    {
        LpFeeBps = U64(vector, "lpFeeBps"),
        ProtocolFeeBps = U64(vector, "protocolFeeBps"),
        CreatorFeeBps = U64(vector, "creatorFeeBps"),
        HasCoinCreator = vector.GetProperty("hasCoinCreator").GetBoolean(),
    };

    [Fact]
    public void The_fixture_names_the_reference_it_came_from()
    {
        // The header must say which SDK version produced the file, so a later
        // protocol upgrade shows up as an outdated file rather than an
        // unexplained mismatch.
        Assert.Equal("@pump-fun/pump-swap-sdk", Root.GetProperty("source").GetString());
        Assert.Equal("2.2.0", Root.GetProperty("sourceVersion").GetString());
        Assert.True(Root.GetProperty("vectorCount").GetInt32() >= 180);
    }

    [Fact]
    public void Exact_base_quotes_agree_with_the_reference_to_the_unit()
    {
        // The two most common directions: buy a known token amount, and sell a
        // known position.
        var mismatches = new StringBuilder();
        var compared = 0;

        foreach (var vector in Vectors)
        {
            var kind = vector.GetProperty("kind").GetString();
            var name = vector.GetProperty("name").GetString();

            if (kind is not ("curve" or "fees" or "boundary" or "tier"))
            {
                continue;
            }

            if (!Has(vector, "uiQuote") || !name!.StartsWith("buy_base_input", StringComparison.Ordinal)
                && !name.StartsWith("sell_base_input", StringComparison.Ordinal))
            {
                continue;
            }

            var baseReserve = U64(vector, "baseReserve");
            var quoteReserve = U64(vector, "quoteReserve");
            var amount = U64(vector, "input");
            var fees = FeesOf(vector);

            var expectedNet = U64(vector, "internalQuote");
            var expectedTotal = U64(vector, "uiQuote");

            var buying = name.StartsWith("buy_base_input", StringComparison.Ordinal);

            var quoted = buying
                ? PumpSwapQuote.TryQuoteBuyExactBase(
                    baseReserve, quoteReserve, amount, fees, out var quote, out var refusal)
                : PumpSwapQuote.TryQuoteSellExactBase(
                    baseReserve, quoteReserve, amount, fees, out quote, out refusal);

            compared++;

            if (!quoted)
            {
                mismatches.AppendLine(CultureInfo.InvariantCulture,
                    $"{kind}/{name} in={amount}: refused as {refusal}, reference said {expectedTotal}");
                continue;
            }

            if (quote.NetQuoteUnits != expectedNet || quote.QuoteUnits != expectedTotal)
            {
                mismatches.AppendLine(CultureInfo.InvariantCulture,
                    $"{kind}/{name} in={amount} fees={fees.LpFeeBps}/{fees.ProtocolFeeBps}/"
                    + $"{fees.CreatorFeeBps} cc={fees.HasCoinCreator}: "
                    + $"net {quote.NetQuoteUnits} vs {expectedNet}, "
                    + $"total {quote.QuoteUnits} vs {expectedTotal}");
            }
        }

        Assert.True(compared >= 60, $"expected the fixture to cover the exact-base directions; compared {compared}");
        Assert.Equal(string.Empty, mismatches.ToString());
    }

    [Fact]
    public void The_fee_composition_is_three_ceilings_summed_as_the_reference_composes_it()
    {
        // Seventeen vectors tell three summed ceilings apart from one ceiling of
        // the summed rate. The difference does not cancel when strategies are
        // compared, because it costs high-turnover strategies the most.
        var mismatches = new StringBuilder();
        var discriminating = 0;

        foreach (var vector in Vectors)
        {
            if (vector.GetProperty("kind").GetString() != "fees"
                || vector.GetProperty("name").GetString() != "buy_base_input")
            {
                continue;
            }

            var fees = FeesOf(vector);
            var net = U64(vector, "internalQuote");
            var expectedFee = U64(vector, "uiQuote") - net;

            var composed = PumpSwapQuote.ComposeFee(net, fees);

            var oneCeiling = fees.TotalBps == 0
                ? 0
                : (ulong)(((UInt128)net * fees.TotalBps + 9_999) / 10_000);

            if (composed != oneCeiling)
            {
                discriminating++;
            }

            if (composed != expectedFee)
            {
                mismatches.AppendLine(CultureInfo.InvariantCulture,
                    $"net={net} fees={fees.LpFeeBps}/{fees.ProtocolFeeBps}/{fees.CreatorFeeBps} "
                    + $"cc={fees.HasCoinCreator}: composed {composed}, reference {expectedFee}, "
                    + $"one-ceiling would be {oneCeiling}");
            }
        }

        Assert.Equal(string.Empty, mismatches.ToString());
        Assert.True(
            discriminating >= 10,
            $"the fixture must contain cases where the two compositions differ; found {discriminating}");
    }

    [Fact]
    public void A_creator_fee_needs_a_coin_creator()
    {
        // The SDK's computeFeesBps returns a non-zero creator rate for a pool
        // whose coin_creator is the default key, but the trade pays none of it.
        var pairs = Vectors
            .Where(v => v.GetProperty("kind").GetString() == "fees"
                && v.GetProperty("name").GetString() == "buy_base_input"
                && U64(v, "creatorFeeBps") > 0)
            .GroupBy(v => (
                v.GetProperty("lpFeeBps").GetString(),
                v.GetProperty("protocolFeeBps").GetString(),
                v.GetProperty("creatorFeeBps").GetString(),
                v.GetProperty("input").GetString()))
            .Where(group => group.Count() == 2)
            .ToList();

        Assert.NotEmpty(pairs);

        foreach (var pair in pairs)
        {
            var without = pair.Single(v => !v.GetProperty("hasCoinCreator").GetBoolean());
            var with = pair.Single(v => v.GetProperty("hasCoinCreator").GetBoolean());

            var feeWithout = U64(without, "uiQuote") - U64(without, "internalQuote");
            var feeWith = U64(with, "uiQuote") - U64(with, "internalQuote");

            Assert.True(
                feeWith > feeWithout,
                "a coin creator must cost more than none");

            Assert.Equal(feeWithout, PumpSwapQuote.ComposeFee(U64(without, "internalQuote"), FeesOf(without)));
            Assert.Equal(feeWith, PumpSwapQuote.ComposeFee(U64(with, "internalQuote"), FeesOf(with)));
        }
    }

    [Fact]
    public void The_exact_quote_directions_differ_from_the_reference_only_by_its_documented_shave()
    {
        // Checked as a relationship, not equality, because the SDK's shave is part
        // of its budget fitting. This library's base-out must be at least the
        // SDK's and at most what the unshaved curve allows.
        var compared = 0;
        var unexplained = new StringBuilder();

        foreach (var vector in Vectors)
        {
            if (vector.GetProperty("name").GetString() != "buy_quote_input"
                || !Has(vector, "base"))
            {
                continue;
            }

            var baseReserve = U64(vector, "baseReserve");
            var quoteReserve = U64(vector, "quoteReserve");
            var budget = U64(vector, "input");
            var fees = FeesOf(vector);
            var referenceBase = U64(vector, "base");

            if (!PumpSwapQuote.TryQuoteBuyExactQuote(
                    baseReserve, quoteReserve, budget, fees, out var quote, out _))
            {
                continue;
            }

            compared++;

            if (quote.BaseUnits < referenceBase)
            {
                unexplained.AppendLine(CultureInfo.InvariantCulture,
                    $"budget={budget}: quoted {quote.BaseUnits} base, below the reference's "
                    + $"{referenceBase}, so the shave is not the whole difference");
            }

            // The unshaved curve on the same net is the upper limit. Anything above it
            // is a different formula, not a shave.
            var unshaved = (ulong)((UInt128)baseReserve * quote.NetQuoteUnits
                / ((UInt128)quoteReserve + quote.NetQuoteUnits));

            if (quote.BaseUnits != unshaved)
            {
                unexplained.AppendLine(CultureInfo.InvariantCulture,
                    $"budget={budget}: quoted {quote.BaseUnits} base, un-shaved curve says {unshaved}");
            }

            // The net plus its fee must fit the budget, as in the SDK's own fitting.
            Assert.True(
                (UInt128)quote.NetQuoteUnits + quote.FeeUnits <= budget,
                $"budget={budget}: net {quote.NetQuoteUnits} plus fee {quote.FeeUnits} exceeds it");
        }

        Assert.True(compared >= 20, $"expected exact-quote coverage; compared {compared}");
        Assert.Equal(string.Empty, unexplained.ToString());
    }

    [Fact]
    public void A_sale_for_an_exact_payout_covers_the_request_where_the_reference_falls_a_unit_short()
    {
        // The SDK's gross-up nets want-1 (a request of 1,000,000 yields 999,999).
        // This library covers the full request, because otherwise every exit
        // would receive one unit less than it asked for.
        var vector = Vectors.First(v =>
            v.GetProperty("kind").GetString() == "fees"
            && v.GetProperty("name").GetString() == "sell_quote_input"
            && Has(v, "base"));

        var wanted = U64(vector, "input");
        var fees = FeesOf(vector);

        Assert.True(PumpSwapQuote.TryQuoteSellExactQuote(
            U64(vector, "baseReserve"),
            U64(vector, "quoteReserve"),
            wanted,
            fees,
            out var quote,
            out _));

        Assert.Equal(wanted, quote.QuoteUnits);
        Assert.Equal(quote.NetQuoteUnits - wanted, quote.FeeUnits);
        Assert.True(quote.NetQuoteUnits - PumpSwapQuote.ComposeFee(quote.NetQuoteUnits, fees) >= wanted);

        // The SDK's gross, one unit smaller, would not have covered it.
        var referenceGross = U64(vector, "internalQuote");
        Assert.True(referenceGross <= quote.NetQuoteUnits);
    }

    [Fact]
    public void A_trade_the_pool_cannot_settle_is_refused_rather_than_clamped()
    {
        // Same rule as the bonding curve: clamping would turn a trade the chain
        // rejects into a plausible small fill.
        const ulong BaseReserve = 206_900_000_000_000;
        const ulong QuoteReserve = 85_000_000_000;

        Assert.False(PumpSwapQuote.TryQuoteBuyExactBase(
            BaseReserve, QuoteReserve, BaseReserve, PumpSwapFees.None, out _, out var buyRefusal));
        Assert.Equal(PumpSwapQuoteRefusal.ExceedsReserves, buyRefusal);

        Assert.False(PumpSwapQuote.TryQuoteSellExactQuote(
            BaseReserve, QuoteReserve, QuoteReserve, PumpSwapFees.None, out _, out var sellRefusal));
        Assert.Equal(PumpSwapQuoteRefusal.ExceedsReserves, sellRefusal);

        Assert.False(PumpSwapQuote.TryQuoteBuyExactBase(
            0, QuoteReserve, 1, PumpSwapFees.None, out _, out var emptyRefusal));
        Assert.Equal(PumpSwapQuoteRefusal.EmptyPool, emptyRefusal);

        Assert.False(PumpSwapQuote.TryQuoteBuyExactBase(
            BaseReserve, QuoteReserve, 0, PumpSwapFees.None, out _, out var zeroRefusal));
        Assert.Equal(PumpSwapQuoteRefusal.ZeroAmount, zeroRefusal);
    }

    [Fact]
    public void The_fee_schedule_a_pool_pays_agrees_with_the_reference_on_every_vector()
    {
        // Fee schedule resolution. The grid established four rules that are not
        // in the IDL: tiers apply only to a canonical pump pool, the market cap is
        // supply x quote / base, the tier boundary is inclusive, and an all-zero
        // exotic schedule means unset, not free.
        var mismatches = new StringBuilder();
        var compared = 0;
        var tiered = 0;

        foreach (var vector in Vectors)
        {
            if (vector.GetProperty("kind").GetString() != "feeSchedule"
                || !Has(vector, "resolvedLpFeeBps"))
            {
                continue;
            }

            var mayhem = vector.GetProperty("isMayhemMode").GetBoolean();
            var schedule = vector.GetProperty("quoteSchedule").GetString() switch
            {
                "sol" => PumpSwapQuoteSchedule.SolLike,
                "stable" => PumpSwapQuoteSchedule.Stable,
                _ => PumpSwapQuoteSchedule.Exotic,
            };

            var expectedConfig = ConfigOf(vector);
            var encoded = PumpSwapAccountFixtures.FeeConfig(expectedConfig.FlatFees,
                expectedConfig.FeeTiers, expectedConfig.StableFeeTiers, expectedConfig.ExoticFlatFees).ToArray();
            Assert.Equal(PumpAccountDecodeFailure.None,
                PumpAccountDecoder.DecodeFeeConfig(encoded, out var config, out _));
            var canonical = vector.GetProperty("isCanonicalPumpPool").GetBoolean();

            var resolved = PumpSwapFeeSchedule.TryResolve(
                config,
                schedule,
                canonical,
                U64(vector, "baseSupply"),
                U64(vector, "baseReserve"),
                U64(vector, "quoteReserve"),
                isMayhemMode: mayhem,
                poolCreatorFeeBps: U64(vector, "poolCreatorFeeBps"),
                creatorFeeConfigurable: vector.GetProperty("creatorFeeConfigurable").GetBoolean(),
                hasCoinCreator: true,
                out var fees,
                out var refusal);

            compared++;

            if (!resolved)
            {
                mismatches.AppendLine(CultureInfo.InvariantCulture,
                    $"{schedule} canonical={canonical} q={U64(vector, "quoteReserve")}: "
                    + $"refused as {refusal}");
                continue;
            }

            if (canonical && schedule is not PumpSwapQuoteSchedule.Exotic)
            {
                tiered++;
            }

            var expectedLp = U64(vector, "resolvedLpFeeBps");
            var expectedProtocol = U64(vector, "resolvedProtocolFeeBps");
            var expectedCreator = U64(vector, "resolvedCreatorFeeBps");

            if (fees.LpFeeBps != expectedLp
                || fees.ProtocolFeeBps != expectedProtocol
                || fees.CreatorFeeBps != expectedCreator)
            {
                mismatches.AppendLine(CultureInfo.InvariantCulture,
                    $"{schedule} canonical={canonical} q={U64(vector, "quoteReserve")} "
                    + $"supply={U64(vector, "baseSupply")}: "
                    + $"{fees.LpFeeBps}/{fees.ProtocolFeeBps}/{fees.CreatorFeeBps} vs "
                    + $"{expectedLp}/{expectedProtocol}/{expectedCreator}");
            }
        }

        Assert.Equal(string.Empty, mismatches.ToString());
        Assert.True(compared >= 150, $"expected broad schedule coverage; compared {compared}");
        Assert.True(tiered >= 50, $"the grid must exercise the tiers, not only the flat fallback; tiered {tiered}");
    }

    [Fact]
    public void A_pool_nobody_canonical_created_pays_the_flat_schedule_whatever_it_is_worth()
    {
        // The first version of the grid passed a default creator, and every SOL
        // case came back on the flat schedule. That is how the canonical-pool rule
        // was found. Applying the tiers to every pool would misprice most pools.
        var pairs = Vectors
            .Where(v => v.GetProperty("kind").GetString() == "feeSchedule"
                && v.GetProperty("quoteSchedule").GetString() == "sol"
                && !v.GetProperty("isMayhemMode").GetBoolean()
                && !v.GetProperty("creatorFeeConfigurable").GetBoolean())
            .GroupBy(v => v.GetProperty("quoteReserve").GetString() + "|" + v.GetProperty("baseSupply").GetString())
            .Where(group => group.Count() == 2)
            .ToList();

        Assert.NotEmpty(pairs);

        foreach (var pair in pairs)
        {
            var flat = pair.Single(v => !v.GetProperty("isCanonicalPumpPool").GetBoolean());

            // The flat schedule's own rates, not any tier's.
            Assert.Equal(U64(flat, "flatLpFeeBps"), U64(flat, "resolvedLpFeeBps"));
            Assert.Equal(U64(flat, "flatProtocolFeeBps"), U64(flat, "resolvedProtocolFeeBps"));
        }
    }
}
