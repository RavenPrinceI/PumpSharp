using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// The checked-in official IDLs, which the fingerprint hashes.
///
/// These are not parity tests. They check that the embedded IDL is the one
/// claimed, that its declarations match the program addresses used
/// elsewhere, and that the classifier tells "declared but not tracked" apart
/// from "not in the IDL at all".
/// </summary>
public class PumpIdlTests
{
    [Fact]
    public void The_pump_idl_is_embedded_and_parseable()
    {
        var idl = PumpIdl.Pump;

        Assert.True(idl.SizeBytes > 100_000, $"the IDL is only {idl.SizeBytes} bytes");
        Assert.NotEmpty(idl.Events);
        Assert.NotEmpty(idl.Accounts);
    }

    [Fact]
    public void The_idl_declares_the_program_address_this_project_hard_codes()
    {
        // The most useful offline check: if Pump redeploys, the embedded IDL and
        // the constant disagree and this fails, instead of a caller quietly
        // watching the wrong program.
        Assert.Equal(PumpProgramIds.Pump, PumpIdl.Pump.ProgramAddress);
    }

    [Fact]
    public void The_pump_swap_idl_declares_the_amm_program_address()
    {
        Assert.Equal(PumpProgramIds.PumpSwap, PumpIdl.PumpSwap.ProgramAddress);
    }

    [Fact]
    public void Both_idls_hash_to_a_recorded_fingerprint()
    {
        // The fingerprint must carry the hashes of the bytes actually read.
        var fingerprint = PumpProtocolFingerprint.Current;

        Assert.True(fingerprint.IdlHashesRecorded);
        Assert.Equal(PumpIdl.Pump.Sha256, fingerprint.PumpIdlSha256);
        Assert.Equal(PumpIdl.PumpSwap.Sha256, fingerprint.PumpAmmIdlSha256);
        Assert.Equal(64, fingerprint.PumpIdlSha256.Length);
        Assert.NotEqual(fingerprint.PumpIdlSha256, fingerprint.PumpAmmIdlSha256);
        Assert.Equal("4.3.1", fingerprint.PumpSdkReference);
        Assert.Equal("2.2.0", fingerprint.PumpSwapSdkReference);
    }

    [Fact]
    public void The_derivation_this_project_used_before_agrees_with_the_idl()
    {
        // The sha256("event:" + Name) derivation agrees with the IDL for every
        // tracked event.
        foreach (var name in PumpLogClassifier.MappedEventNames)
        {
            var derived = PumpLogClassifier.EventDiscriminator(name);

            Assert.True(
                PumpIdl.Pump.Events.TryGetValue(derived, out var idlName),
                $"the derived discriminator for {name} is not in the IDL");
            Assert.Equal(name, idlName);
        }
    }

    [Fact]
    public void A_declared_but_untracked_event_is_known_rather_than_unrecognised()
    {
        // Drift detection depends on this distinction. A fee sweep is routine; an
        // undeclared discriminator means the protocol changed.
        var untracked = PumpIdl.Pump.Events
            .First(e => !PumpLogClassifier.MappedEventNames.Contains(e.Value));

        var payload = new byte[32];
        BitConverter.TryWriteBytes(payload, untracked.Key);

        Assert.Equal(
            PumpEventKind.KnownOther,
            PumpLogClassifier.ClassifyPayload(Convert.ToBase64String(payload)));
    }

    [Fact]
    public void A_discriminator_absent_from_the_idl_is_unrecognised()
    {
        // The real drift case: Pump shipped a new event, or the IDL is out of
        // date.
        var payload = new byte[32];
        BitConverter.TryWriteBytes(payload, 0xDEADBEEFCAFEF00DUL);

        Assert.Equal(
            PumpEventKind.Unrecognised,
            PumpLogClassifier.ClassifyPayload(Convert.ToBase64String(payload)));
    }
}
