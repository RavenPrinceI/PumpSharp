using System.Security.Cryptography;
using System.Text.Json;
using PumpSharp;

namespace PumpSharp.Tests;

public class PumpQuoteCoverageAuditTests
{
    [Fact]
    public void Verified_quote_scope_is_bound_to_the_reviewed_references_idls_and_exact_test_inputs()
    {
        var assembly = typeof(PumpQuoteCoverageAuditTests).Assembly;
        using var auditBytes = assembly.GetManifestResourceStream("PumpSharp.Tests.parity.coverage-audit.json")!;
        Assert.Equal(PumpProtocolFingerprint.Current.QuoteCoverageAuditSha256, Convert.ToHexStringLower(SHA256.HashData(auditBytes)));
        auditBytes.Position = 0;
        using var audit = JsonDocument.Parse(auditBytes);
        var root = audit.RootElement;
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        var fingerprint = PumpProtocolFingerprint.Current;
        var references = root.GetProperty("references").EnumerateArray().ToDictionary(
            row => row.GetProperty("Name").GetString()!, row => row.GetProperty("Version").GetString());
        Assert.Equal(fingerprint.PumpSdkReference, references["@pump-fun/pump-sdk"]);
        Assert.Equal(fingerprint.PumpSwapSdkReference, references["@pump-fun/pump-swap-sdk"]);
        Assert.Equal(5, root.GetProperty("supportedExecution").GetArrayLength());
        var idls = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["pump"] = PumpIdl.Pump.Sha256, ["pump_amm"] = PumpIdl.PumpSwap.Sha256, ["pump_fees"] = PumpIdl.PumpFees.Sha256 };
        foreach (var row in root.GetProperty("idls").EnumerateArray())
            Assert.Equal(idls[row.GetProperty("name").GetString()!], row.GetProperty("sha256").GetString());
        Assert.Equal(11, root.GetProperty("artifacts").GetArrayLength());
        foreach (var row in root.GetProperty("artifacts").EnumerateArray())
        {
            var name = "PumpSharp.Tests.parity." + row.GetProperty("name").GetString();
            using var input = assembly.GetManifestResourceStream(name);
            Assert.NotNull(input);
            Assert.Equal(row.GetProperty("bytes").GetInt64(), input.Length);
            Assert.Equal(row.GetProperty("sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(input)));
        }
        Assert.True(fingerprint.QuoteMathsParityVerified);
    }
}
