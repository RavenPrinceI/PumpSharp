using System.Globalization;
using System.Text.Json;
using PumpSharp.Chain;

namespace PumpSharp.Tests;

public class TokenTransferFeeParityTests
{
    private static JsonDocument Read()
    {
        using var stream = typeof(TokenTransferFeeParityTests).Assembly.GetManifestResourceStream(
            "PumpSharp.Tests.parity.token-transfer-fee-vectors.json")!;
        return JsonDocument.Parse(stream);
    }
    private static ulong Number(JsonElement row, string key) => ulong.Parse(row.GetProperty(key).GetString()!, CultureInfo.InvariantCulture);

    [Fact]
    public void Withheld_net_and_gross_match_both_Pump_SDKs_and_SPL_including_u64_overflow_refusal()
    {
        using var document = Read();
        var root = document.RootElement;
        Assert.Equal("4.3.1", root.GetProperty("versions").GetProperty("@pump-fun/pump-sdk").GetString());
        Assert.Equal("2.2.0", root.GetProperty("versions").GetProperty("@pump-fun/pump-swap-sdk").GetString());
        Assert.Equal("0.4.15", root.GetProperty("versions").GetProperty("@solana/spl-token").GetString());
        var count = 0; var overflow = 0;
        foreach (var row in root.GetProperty("cases").EnumerateArray())
        {
            count++;
            var fee = new TokenTransferFee(row.GetProperty("bps").GetUInt16(), Number(row, "cap"));
            var amount = Number(row, "amount");
            Assert.True(fee.TryWithheld(amount, out var withheld));
            Assert.Equal(Number(row, "withheld"), withheld);
            Assert.True(fee.TryNet(amount, out var net));
            Assert.Equal(Number(row, "net"), net);
            Assert.Equal((UInt128)amount, (UInt128)net + withheld);
            var fits = row.GetProperty("grossFitsU64").GetBoolean();
            Assert.Equal(fits, fee.TryGrossForNet(amount, out var gross));
            if (fits)
            {
                Assert.Equal(Number(row, "gross"), gross);
                Assert.True(fee.TryNet(gross, out var delivered));
                Assert.True(delivered >= amount);
            }
            else { overflow++; Assert.Equal(0ul, gross); }
        }
        Assert.Equal(784, count);
        Assert.True(overflow > 0);
        TestContext.Current.TestOutputHelper!.WriteLine($"{count} official arithmetic cases; {overflow} unrepresentable gross results refused; zero mismatches.");
    }


    [Theory]
    [InlineData(10001)]
    [InlineData(65535)]
    public void Invalid_rates_cannot_produce_even_a_zero_value_quote(ushort basisPoints)
    {
        var fee = new TokenTransferFee(basisPoints, ulong.MaxValue);
        Assert.False(fee.IsValid);
        Assert.False(fee.TryWithheld(0, out var withheld));
        Assert.False(fee.TryNet(0, out var net));
        Assert.False(fee.TryGrossForNet(0, out var gross));
        Assert.Equal(0ul, withheld); Assert.Equal(0ul, net); Assert.Equal(0ul, gross);
    }
}
