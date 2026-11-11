using PumpSharp.Chain;

namespace PumpSharp.Tests;

/// <summary>
/// Keeps the README example compiling. If this file needs editing, the README
/// does too.
/// </summary>
public class ReadmeExampleTests
{
    private static ulong? Example(string logLine, string mintAddress, byte[] accountBytes)
    {
        var failure = PumpEventDecoder.DecodeLogLine(logLine, out var decoded);
        if (failure == PumpDecodeFailure.None && decoded.Kind == PumpEventKind.Trade)
        {
            var trade = decoded.Trade;
            _ = $"{(trade.IsBuy ? "buy" : "sell")} {trade.TokenAmount} of {trade.Mint}";
        }

        if (Pubkey.TryParse(mintAddress, out var mint)
            && PumpAddresses.TryBondingCurve(mint, out var curveAddress, out _))
        {
            _ = curveAddress;
        }

        if (PumpAccountDecoder.DecodeBondingCurve(accountBytes, out var curve, out _) == PumpAccountDecodeFailure.None
            && PumpBondingCurve.TryQuoteBuy(curve.Reserves, quoteInUnits: 100_000_000, out var quote))
        {
            return quote.AmountOutUnits;
        }

        return null;
    }

    [Fact]
    public void The_readme_example_refuses_empty_input_without_throwing() =>
        Assert.Null(Example("Program log: hello", "So11111111111111111111111111111111111111112", []));
}
