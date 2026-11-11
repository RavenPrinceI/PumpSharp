using System.Globalization;
using System.Text;
using System.Text.Json;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// Parity against the official Pump SDK.
///
/// Every expected value in <c>parity/pump-sdk-4.3.1-vectors.json</c> was
/// produced by running <c>@pump-fun/pump-sdk</c> 4.3.1 with the script next
/// to it. Copying the SDK's formulas into a generator would only show that
/// the copy agrees with itself; running the SDK shows what the SDK does.
///
/// This suite covers the curve arithmetic and the fee primitive. The
/// gross-to-net derivation, the PumpSwap crossing branch, transfer fees and
/// mayhem curves have their own suites. Here:
///
/// <list type="bullet">
/// <item><c>curve-buy</c> and <c>curve-sell</c> are compared with
/// <c>TryQuoteBuy</c> and <c>TryQuoteSell</c>.</item>
/// <item><c>fee</c> and <c>fee-no-creator</c> are compared with
/// <c>TryFee</c>, composed the way the SDK composes it.</item>
/// <item><c>unpriceable</c> checks agreement on which states have no quote,
/// not on a number.</item>
/// </list>
///
/// The generator calls the SDK's core helpers, not its exported entry
/// points. The exported buy prices on
/// <c>(gross - 1) * 10_000 / (bps + 10_000)</c>, so even at zero fees it
/// prices on <c>gross - 1</c>; a self-check in the generator caught a
/// 33,496-unit difference on a one-SOL buy. <c>TryQuoteBuy</c> takes the
/// amount that reaches the curve, so the generator gets the core result by
/// asking the exported function for <c>net + 1</c>.
///
/// Mismatches are collected and reported together rather than failing on
/// the first one, so a reader can see whether a mismatch is systematic.
/// </summary>
public class PumpParityTests
{
    private sealed record Vector(
        string Group,
        string Name,
        JsonElement Inputs,
        JsonElement Outputs);

    private static readonly Lazy<JsonDocument> Document = new(() =>
    {
        using var stream = typeof(PumpParityTests).Assembly
            .GetManifestResourceStream("PumpSharp.Tests.parity.pump-sdk-4.3.1-vectors.json")
            ?? throw new InvalidOperationException(
                "the parity vectors are not embedded; parity cannot be inferred");

        return JsonDocument.Parse(stream);
    });

    private static JsonElement Root => Document.Value.RootElement;

    private static IEnumerable<Vector> Group(string group)
    {
        foreach (var vector in Root.GetProperty("vectors").EnumerateArray())
        {
            if (vector.GetProperty("group").GetString() == group)
            {
                yield return new Vector(
                    group,
                    vector.GetProperty("name").GetString() ?? "unnamed",
                    vector.GetProperty("inputs"),
                    vector.GetProperty("outputs"));
            }
        }
    }

    private static ulong U64(JsonElement element, string name) =>
        ulong.Parse(element.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);

    private static ulong U64OrDefault(JsonElement element, string name, ulong fallback) =>
        element.TryGetProperty(name, out var property)
            ? ulong.Parse(property.GetString()!, CultureInfo.InvariantCulture)
            : fallback;

    private static int I32(JsonElement element, string name) =>
        element.GetProperty(name).GetInt32();

    /// <summary>Canonical fresh-curve reserves, matching the generator's defaults.</summary>
    private const ulong FreshVirtualToken = 1_073_000_000_000_000;
    private const ulong FreshVirtualQuote = 30_000_000_000;
    private const ulong FreshRealToken = 793_100_000_000_000;

    private static PumpCurveReserves Reserves(JsonElement inputs) => new()
    {
        VirtualBaseUnits = U64OrDefault(inputs, "virtualToken", FreshVirtualToken),
        VirtualQuoteUnits = U64OrDefault(inputs, "virtualQuote", FreshVirtualQuote),
        RealBaseUnits = U64OrDefault(inputs, "realToken", FreshRealToken),
        RealQuoteUnits = U64OrDefault(inputs, "realQuote", 0),
        Basis = PumpReserveBasis.Sol,
    };

    [Fact]
    public void The_vectors_were_generated_by_executing_the_official_sdk()
    {
        // Check the vector file's header: it must name the SDK that produced it,
        // and the version must match the embedded IDLs.
        Assert.Equal("@pump-fun/pump-sdk", Root.GetProperty("source").GetString());
        Assert.Equal("4.3.1", Root.GetProperty("version").GetString());
        Assert.Contains(
            "executing",
            Root.GetProperty("generatedBy").GetString(),
            StringComparison.Ordinal);

        // The generator's own setup check: the +1 adjustment isolates the core
        // helper, so the curve vectors test the curve alone.
        Assert.True(Root.GetProperty("setupCheck")
            .GetProperty("netIsolatedMatchesCore").GetBoolean());

        Assert.True(Root.GetProperty("vectorCount").GetInt32() > 150);
    }

    [Fact]
    public void The_buy_curve_matches_the_official_reference()
    {
        var mismatches = new StringBuilder();
        var compared = 0;

        foreach (var vector in Group("curve-buy"))
        {
            var reserves = Reserves(vector.Inputs);
            var amountIn = U64(vector.Inputs, "amountIn");
            var expected = U64(vector.Outputs, "tokensOut");

            compared++;

            if (!PumpBondingCurve.TryQuoteBuy(reserves, amountIn, out var quote))
            {
                mismatches.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {vector.Name}: refused to quote, reference says {expected}");

                continue;
            }

            if (quote.AmountOutUnits != expected)
            {
                mismatches.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {vector.Name}: ours {quote.AmountOutUnits}, reference {expected}, "
                    + $"delta {(long)quote.AmountOutUnits - (long)expected}");
            }
        }

        Assert.True(compared > 50, $"only {compared} buy vectors were compared");
        Assert.True(
            mismatches.Length == 0,
            $"{compared} buy vectors compared, mismatches:\n{mismatches}");
    }

    [Fact]
    public void The_sell_curve_matches_the_official_reference()
    {
        var mismatches = new StringBuilder();
        var compared = 0;

        foreach (var vector in Group("curve-sell"))
        {
            var reserves = Reserves(vector.Inputs);
            var amountIn = U64(vector.Inputs, "amountIn");
            var expected = U64(vector.Outputs, "quoteOut");

            compared++;

            if (!PumpBondingCurve.TryQuoteSell(reserves, amountIn, out var quote))
            {
                mismatches.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {vector.Name}: refused to quote, reference says {expected}");

                continue;
            }

            if (quote.AmountOutUnits != expected)
            {
                mismatches.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {vector.Name}: ours {quote.AmountOutUnits}, reference {expected}, "
                    + $"delta {(long)quote.AmountOutUnits - (long)expected}");
            }
        }

        Assert.True(compared > 30, $"only {compared} sell vectors were compared");
        Assert.True(
            mismatches.Length == 0,
            $"{compared} sell vectors compared, mismatches:\n{mismatches}");
    }

    /// <summary>
    /// Composes a fee the way the official SDK does: each component rounded up
    /// separately, then summed.
    ///
    /// <c>TryFee(amount, protocol + creator, Up)</c> looks equivalent but can be
    /// off by one unit per trade, because <c>ceil(a·p/d) + ceil(a·c/d)</c> can
    /// exceed <c>ceil(a·(p+c)/d)</c>.
    /// </summary>
    private static bool TryOfficialFee(
        ulong amount,
        ulong protocolBps,
        ulong creatorBps,
        bool chargeCreator,
        out ulong feeUnits)
    {
        feeUnits = 0;

        if (!PumpBondingCurve.TryFee(amount, protocolBps, PumpFeeRounding.Up, out var protocol))
        {
            return false;
        }

        var creator = 0UL;
        if (chargeCreator
            && !PumpBondingCurve.TryFee(amount, creatorBps, PumpFeeRounding.Up, out creator))
        {
            return false;
        }

        feeUnits = protocol + creator;
        return true;
    }

    [Fact]
    public void The_fee_maths_matches_the_official_reference()
    {
        var mismatches = new StringBuilder();
        var compared = 0;

        foreach (var vector in Group("fee"))
        {
            var amount = U64(vector.Inputs, "amount");
            var protocolBps = (ulong)I32(vector.Inputs, "protocolFeeBps");
            var creatorBps = (ulong)I32(vector.Inputs, "creatorFeeBps");
            var expected = U64(vector.Outputs, "feeUnits");

            compared++;

            // The generator's `fee` group uses a curve with a real creator,
            // so both components are charged.
            if (!TryOfficialFee(amount, protocolBps, creatorBps, true, out var ours))
            {
                mismatches.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {vector.Name}: refused, reference says {expected}");

                continue;
            }

            if (ours != expected)
            {
                mismatches.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {vector.Name}: ours {ours}, reference {expected}");
            }
        }

        Assert.True(compared > 40, $"only {compared} fee vectors were compared");
        Assert.True(
            mismatches.Length == 0,
            $"{compared} fee vectors compared, mismatches:\n{mismatches}");
    }

    [Fact]
    public void A_curve_with_no_creator_pays_no_creator_fee()
    {
        // An easily missed condition in the SDK source. Getting it wrong would
        // overstate cost on every curve created before the creator field existed.
        var mismatches = new StringBuilder();
        var compared = 0;

        foreach (var vector in Group("fee-no-creator"))
        {
            var amount = U64(vector.Inputs, "amount");
            var protocolBps = (ulong)I32(vector.Inputs, "protocolFeeBps");
            var creatorBps = (ulong)I32(vector.Inputs, "creatorFeeBps");
            var expected = U64(vector.Outputs, "feeUnits");

            compared++;

            Assert.True(TryOfficialFee(amount, protocolBps, creatorBps, false, out var ours));

            if (ours != expected)
            {
                mismatches.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  {vector.Name}: ours {ours}, reference {expected}");
            }
        }

        Assert.True(compared > 5, $"only {compared} vectors were compared");
        Assert.True(mismatches.Length == 0, $"mismatches:\n{mismatches}");
    }

    [Fact]
    public void Rounding_up_is_the_official_choice_and_rounding_down_is_not()
    {
        // The vectors show which way the SDK rounds. Both directions are checked
        // so the answer is stated explicitly.
        var witnessed = 0;

        foreach (var vector in Group("fee"))
        {
            var amount = U64(vector.Inputs, "amount");
            var protocolBps = (ulong)I32(vector.Inputs, "protocolFeeBps");
            var creatorBps = (ulong)I32(vector.Inputs, "creatorFeeBps");
            var expected = U64(vector.Outputs, "feeUnits");

            Assert.True(PumpBondingCurve.TryFee(
                amount, protocolBps, PumpFeeRounding.Down, out var downProtocol));

            Assert.True(PumpBondingCurve.TryFee(
                amount, creatorBps, PumpFeeRounding.Down, out var downCreator));

            if (downProtocol + downCreator != expected)
            {
                witnessed++;
            }
        }

        Assert.True(
            witnessed > 0,
            "no vector distinguished rounding up from rounding down, so the "
            + "suite would pass with the wrong choice");
    }

    [Fact]
    public void A_quote_mint_does_not_change_the_curve_arithmetic()
    {
        // For identical reserve numbers the SDK returns identical output whether
        // the curve is quoted in SOL or another mint. The quote mint selects a fee
        // schedule; it does not change the curve maths.
        //
        // That is why this library reports the basis alongside the number instead
        // of branching the arithmetic on it. If this test ever fails, that
        // assumption no longer holds.
        var mismatches = new StringBuilder();
        var compared = 0;

        foreach (var vector in Group("quote-mint-buy"))
        {
            var reserves = Reserves(vector.Inputs) with { Basis = PumpReserveBasis.Quote };
            var amountIn = U64(vector.Inputs, "amountIn");
            var expected = U64(vector.Outputs, "tokensOut");

            compared++;

            Assert.True(PumpBondingCurve.TryQuoteBuy(reserves, amountIn, out var quote));

            if (quote.AmountOutUnits != expected)
            {
                mismatches.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  buy {vector.Name}: ours {quote.AmountOutUnits}, reference {expected}");
            }

            Assert.Equal(PumpReserveBasis.Quote, quote.Basis);
        }

        foreach (var vector in Group("quote-mint-sell"))
        {
            var reserves = Reserves(vector.Inputs) with { Basis = PumpReserveBasis.Quote };
            var amountIn = U64(vector.Inputs, "amountIn");
            var expected = U64(vector.Outputs, "quoteOut");

            compared++;

            Assert.True(PumpBondingCurve.TryQuoteSell(reserves, amountIn, out var quote));

            if (quote.AmountOutUnits != expected)
            {
                mismatches.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"  sell {vector.Name}: ours {quote.AmountOutUnits}, reference {expected}");
            }
        }

        Assert.True(compared > 30, $"only {compared} quote-mint vectors were compared");
        Assert.True(
            mismatches.Length == 0,
            $"{compared} quote-mint vectors compared, mismatches:\n{mismatches}");
    }

    [Fact]
    public void We_refuse_a_curve_the_reference_prices_with_the_sign_bit_set()
    {
        // Pump's docs say virtual_quote_reserves may be negative, while the IDL
        // declares it as u64. Which is right changes the sign of a price.
        //
        // The SDK reads the field as u64 and prices the curve. This library
        // refuses instead. That difference is intentional and is recorded here so
        // nobody mistakes it for a bug. Whoever revisits it can compare against
        // the SDK's answer in these vectors.
        var compared = 0;

        foreach (var vector in Group("negative-quote-reserves"))
        {
            var virtualQuote = U64(vector.Inputs, "virtualQuote");
            var reference = vector.Outputs.GetProperty("tokensOut").GetString();

            compared++;

            // The sign bit is set, so these vectors cover the case.
            Assert.True((long)virtualQuote < 0);

            var reserves = new PumpCurveReserves
            {
                VirtualBaseUnits = FreshVirtualToken,
                VirtualQuoteUnits = virtualQuote,
                RealBaseUnits = FreshRealToken,
                RealQuoteUnits = 0,
                Basis = PumpReserveBasis.Sol,
            };

            Assert.True(reserves.HasNegativeSignedQuoteReserves);
            Assert.False(reserves.CanPrice);

            Assert.False(
                PumpBondingCurve.TryQuoteBuy(reserves, 1_000_000_000, out _),
                $"{vector.Name}: this project must refuse; the reference says {reference}");

            Assert.False(PumpBondingCurve.TryQuoteSell(reserves, 1_000_000_000_000, out _));

            // The SDK did return a number, which is what makes this a difference
            // rather than agreement.
            Assert.NotNull(reference);
            Assert.DoesNotContain("throws", reference, StringComparison.Ordinal);
        }

        Assert.Equal(4, compared);
    }

    [Fact]
    public void Original_migration_crossing_vectors_match_the_v3_budget_quote()
    {
        // These come before the larger 3,024-case v3 grid and cover different,
        // realistic reserve sizes.
        var compared = 0;
        var crossed = 0;
        var stayedOnCurve = 0;

        foreach (var vector in Group("crossing"))
        {
            var tokensOut = vector.Outputs.GetProperty("tokensOut").GetString();
            compared++;

            Assert.NotNull(tokensOut);
            Assert.DoesNotContain("throws", tokensOut, StringComparison.Ordinal);

            // A buy that crosses returns more than the remaining supply, because the
            // rest comes from the pool. A buy that does not cross is an ordinary curve
            // buy through the v3 entry point. The group needs both sides to test the
            // boundary.
            var remaining = U64(vector.Inputs, "realToken");
            var received = ulong.Parse(tokensOut!, CultureInfo.InvariantCulture);
            var reserves = new PumpCurveReserves
            {
                VirtualBaseUnits = U64(vector.Inputs, "virtualToken"),
                VirtualQuoteUnits = U64(vector.Inputs, "virtualQuote"),
                RealBaseUnits = remaining, RealQuoteUnits = U64(vector.Inputs, "realQuote"),
                Basis = PumpReserveBasis.Sol,
            };
            var state = new PumpCurveV3State(reserves, 0, 0, 0, remaining + 206_900_000_000_000, 0, true, false, false);
            Assert.True(PumpCurveV3Quote.TryBaseForWalletBudget(state, U64(vector.Inputs, "amountIn"), null, out var actual, out _));
            Assert.Equal(received, actual);

            if (received > remaining)
            {
                crossed++;
            }
            else
            {
                stayedOnCurve++;
            }
        }

        Assert.Equal(8, compared);

        // Checked in both directions. An earlier version required every vector to
        // cross and failed: with twenty trillion tokens left on the curve, a
        // one-SOL buy is nowhere near crossing. The group is a boundary set, not a
        // crossing set.
        Assert.True(crossed >= 4, $"only {crossed} vectors crossed into the pool");
        Assert.True(
            stayedOnCurve >= 4,
            $"only {stayedOnCurve} vectors stayed on the curve, so the boundary "
            + "is not exercised");

        TestContext.Current.TestOutputHelper!.WriteLine($"{compared} original v3 crossing states: numeric parity, including {crossed} crossings and {stayedOnCurve} curve-only buys.");
    }

    [Fact]
    public void We_agree_with_the_reference_about_which_states_have_no_quote()
    {
        foreach (var vector in Group("unpriceable"))
        {
            var reserves = Reserves(vector.Inputs);
            var referenceBuy = vector.Outputs.GetProperty("tokensOut").GetString();

            var quoted = PumpBondingCurve.TryQuoteBuy(reserves, 1_000_000_000, out var quote);

            if (referenceBuy == "0")
            {
                // The SDK declines by returning zero; this library declines by refusing.
                // Both mean "no quote", but a refusal cannot be mistaken for a measured
                // zero.
                Assert.True(
                    !quoted || quote.AmountOutUnits == 0,
                    $"{vector.Name}: reference gives no quote, ours gave "
                    + $"{quote.AmountOutUnits}");

                continue;
            }

            // A completed curve is the one case where the SDK still quotes:
            // getBuyTokenAmountFromSolAmount does not read `complete`. This library
            // quotes it too, and callers are expected to check completion themselves.
            Assert.Equal("complete", vector.Name);
            Assert.True(quoted);
            Assert.Equal(
                ulong.Parse(referenceBuy!, CultureInfo.InvariantCulture),
                quote.AmountOutUnits);
        }
    }
}
