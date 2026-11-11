using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// Sweeps the two searches inside <see cref="PumpSwapQuote"/>.
///
/// The exact-quote directions have no closed form: the budget is
/// <c>net + fee(net)</c> with the fee made of three separate ceilings, which
/// cannot be inverted. The code estimates and then searches, and its
/// comments say the search is short because each ceiling adds at most one
/// unit of slack.
///
/// These tests check that the result is optimal: the largest net whose fee
/// still fits the budget, and the smallest gross whose proceeds after fees
/// still cover the request. A search that stopped early would return a
/// slightly worse quote, and the parity vectors would not catch it because
/// they compare with the SDK's shaved value, not the optimum.
/// </summary>
public class PumpSwapFittingTests
{
    private const ulong BaseReserve = 206_900_000_000_000;
    private const ulong QuoteReserve = 85_000_000_000;

    private static IEnumerable<PumpSwapFees> Schedules()
    {
        ulong[] rates = [0, 1, 2, 5, 20, 25, 30, 95, 99, 250, 1_000];

        foreach (var lp in rates)
        {
            foreach (var creator in rates)
            {
                yield return new PumpSwapFees
                {
                    LpFeeBps = lp,
                    ProtocolFeeBps = 5,
                    CreatorFeeBps = creator,
                    HasCoinCreator = creator > 0,
                };
            }
        }
    }

    private static IEnumerable<ulong> Budgets()
    {
        // Small values are where the ceilings matter most relative to the amount.
        // Odd offsets keep the sweep off round numbers.
        ulong[] magnitudes = [10, 97, 1_000, 10_007, 1_000_003, 999_999_937, 10_000_000_019];

        foreach (var magnitude in magnitudes)
        {
            yield return magnitude;
            yield return magnitude + 1;
            yield return magnitude + 7;
        }
    }

    [Fact]
    public void A_budget_is_fitted_to_the_largest_net_that_still_leaves_room_for_the_fee()
    {
        var suboptimal = 0;
        var overspent = 0;
        var cases = 0;

        foreach (var fees in Schedules())
        {
            foreach (var budget in Budgets())
            {
                if (!PumpSwapQuote.TryQuoteBuyExactQuote(
                        BaseReserve, QuoteReserve, budget, fees, out var quote, out _))
                {
                    continue;
                }

                cases++;
                var net = quote.NetQuoteUnits;

                // It must fit.
                if (net + PumpSwapQuote.ComposeFee(net, fees) > budget)
                {
                    overspent++;
                }

                // One more unit must not fit. That is what optimal means here.
                if (net < ulong.MaxValue
                    && net + 1 + PumpSwapQuote.ComposeFee(net + 1, fees) <= budget)
                {
                    suboptimal++;
                }
            }
        }

        Assert.True(cases > 2_000, $"the sweep must actually cover something; quoted {cases}");
        Assert.Equal(0, overspent);
        Assert.Equal(0, suboptimal);
    }

    [Fact]
    public void A_requested_payout_is_grossed_up_to_the_smallest_amount_that_covers_it()
    {
        var suboptimal = 0;
        var short_ = 0;
        var cases = 0;

        foreach (var fees in Schedules())
        {
            foreach (var wanted in Budgets())
            {
                if (!PumpSwapQuote.TryQuoteSellExactQuote(
                        BaseReserve, QuoteReserve, wanted, fees, out var quote, out _))
                {
                    continue;
                }

                cases++;
                var gross = quote.NetQuoteUnits;

                // It must cover the request.
                if (gross - PumpSwapQuote.ComposeFee(gross, fees) < wanted)
                {
                    short_++;
                }

                // One unit less must not cover it, or the gross-up overshot and the sale
                // would use more base than needed.
                if (gross > 0
                    && gross - 1 - PumpSwapQuote.ComposeFee(gross - 1, fees) >= wanted)
                {
                    suboptimal++;
                }
            }
        }

        Assert.True(cases > 2_000, $"the sweep must actually cover something; quoted {cases}");
        Assert.Equal(0, short_);
        Assert.Equal(0, suboptimal);
    }

    [Fact]
    public void A_quote_never_rounds_in_the_trader_s_favour()
    {
        // Every rounding choice in this file points the same way: a buy's cost
        // rounds up and a sale's proceeds round down. Checked as one property over
        // the sweep, because that property is what matters to P&L.
        foreach (var fees in Schedules())
        {
            foreach (var amount in Budgets())
            {
                if (PumpSwapQuote.TryQuoteBuyExactBase(
                        BaseReserve, QuoteReserve, amount, fees, out var buy, out _))
                {
                    Assert.True(buy.QuoteUnits >= buy.NetQuoteUnits);
                    Assert.Equal(buy.QuoteUnits - buy.NetQuoteUnits, buy.FeeUnits);
                }

                if (PumpSwapQuote.TryQuoteSellExactBase(
                        BaseReserve, QuoteReserve, amount, fees, out var sell, out _))
                {
                    Assert.True(sell.QuoteUnits <= sell.NetQuoteUnits);
                    Assert.Equal(sell.NetQuoteUnits - sell.QuoteUnits, sell.FeeUnits);
                }
            }
        }
    }
}
