namespace PumpSharp;

/// <summary>
/// What this build assumes about the Pump protocol, as a comparable record.
///
/// Store it next to anything protocol-sensitive, such as a saved quote, so
/// you can later tell which protocol version produced it, and stop
/// quote-sensitive work when it changes. The IDL hashes are exact. The SDK
/// fields name the TypeScript packages the vectors were generated from. The
/// Rust field repeats the version in Pump's documentation; no Rust parity
/// run was done.
/// </summary>
public sealed record PumpProtocolFingerprint
{
    public required string PumpProgramId { get; init; }

    public required string PumpSwapProgramId { get; init; }

    /// <summary>The <c>@pump-fun/pump-sdk</c> version the IDLs and vectors come from.</summary>
    public required string PumpSdkReference { get; init; }

    public required string PumpSwapSdkReference { get; init; }

    public required string PumpRustReference { get; init; }

    /// <summary>Date the versions above were read from official sources.</summary>
    public required string ResearchDate { get; init; }

    /// <summary>
    /// Event names tracked individually, with the discriminator derivation
    /// stated so it can be reproduced.
    /// </summary>
    public required IReadOnlyList<string> ProvisionalEventNames { get; init; }

    public required string EventDiscriminatorDerivation { get; init; }

    /// <summary>
    /// True when the official IDLs are embedded and hashed. Without the hashes,
    /// a drift check could only see a changed program address or version, not a
    /// changed layout.
    /// </summary>
    public required bool IdlHashesRecorded { get; init; }

    /// <summary>SHA-256 of the embedded Pump IDL.</summary>
    public required string PumpIdlSha256 { get; init; }

    /// <summary>SHA-256 of the embedded PumpSwap AMM IDL.</summary>
    public required string PumpAmmIdlSha256 { get; init; }

    /// <summary>Event declarations the checked-in Pump IDL carries.</summary>
    public required int PumpIdlEventCount { get; init; }

    /// <summary>Account declarations the checked-in Pump IDL carries.</summary>
    public required int PumpIdlAccountCount { get; init; }

    /// <summary>
    /// True when the supported quote routes match the official SDKs, as listed
    /// in the coverage audit (<c>tests/PumpSharp.Tests/parity/coverage-audit.json</c>).
    /// It says nothing about data freshness, SDK routes this library does not
    /// implement, or how real execution behaves. New maths needs a new audit.
    /// </summary>
    public required bool QuoteMathsParityVerified { get; init; }
    /// <summary>SHA-256 of the coverage audit, stored beside the parity claim so
    /// a saved record cannot silently pick up a newer audit.</summary>
    public string QuoteCoverageAuditSha256 { get; init; } = string.Empty;

    /// <summary>The fingerprint this build was compiled with.</summary>
    public static PumpProtocolFingerprint Current { get; } = new()
    {
        PumpProgramId = PumpProgramIds.Pump,
        PumpSwapProgramId = PumpProgramIds.PumpSwap,
        PumpSdkReference = "4.3.1",
        PumpSwapSdkReference = "2.2.0",
        PumpRustReference = "0.4.1",
        ResearchDate = "2026-10-31",
        ProvisionalEventNames = PumpLogClassifier.MappedEventNames,
        EventDiscriminatorDerivation =
            "read from the checked-in official IDL; verified to agree with "
            + "sha256(\"event:\" + EventName) for every event that derivation covered",
        IdlHashesRecorded = true,
        PumpIdlSha256 = PumpIdl.Pump.Sha256,
        PumpAmmIdlSha256 = PumpIdl.PumpSwap.Sha256,
        PumpIdlEventCount = PumpIdl.Pump.Events.Count,
        PumpIdlAccountCount = PumpIdl.Pump.Accounts.Count,
        QuoteMathsParityVerified = true,
        QuoteCoverageAuditSha256 = "c0095f331e8f45a09750133ac6c7ae0aae4df3da5521a2361184204f69d171ee",
    };
}
