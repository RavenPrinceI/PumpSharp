using System.Security.Cryptography;
using System.Text;

namespace PumpSharp;

/// <summary>
/// Events the deployed programs emit that no checked-in IDL declares.
///
/// These are kept out of the embedded IDLs. Adding one would change the
/// fingerprint and suggest an official source that does not exist, so the
/// IDLs stay exactly as released.
///
/// <c>SwapEvent</c> (<c>40c6cde8260871e2</c>) is 25 bytes including the
/// discriminator and is emitted while Pump or PumpSwap is the innermost
/// program. It appeared 8 times in 198,680 payloads in one measurement and
/// again in a later run. On 2026-11-02 it was identified as
/// <c>sha256("event:SwapEvent")[..8]</c> by hashing candidate names from the
/// official IDLs, and a second implementation confirmed it. No SDK release
/// up to pump-sdk 4.3.1 or pump-swap-sdk 2.2.0 declares it. Its fields and
/// the instruction that emits it are unknown, and nothing decodes it.
///
/// Callers that halt on unknown drift do not need to halt on this event, but
/// only when both the discriminator and the 25-byte length match. A
/// <c>SwapEvent</c> of any other length is an unknown layout and still
/// counts as drift.
/// </summary>
public static class PumpUndeclaredEvents
{
    /// <summary>The name the discriminator derives from.</summary>
    public const string SwapEventName = "SwapEvent";

    /// <summary>The only length observed, discriminator included.</summary>
    public const int SwapEventPayloadBytes = 25;

    /// <summary>
    /// The discriminator as the decoder's little-endian u64 lookup reads it.
    ///
    /// Derived, because there is no declaration to read. The same derivation
    /// matches the IDL for every declared event (see <see cref="PumpIdl"/>), and
    /// a test checks that.
    /// </summary>
    public static ulong SwapEventDiscriminator { get; } = AnchorEventDiscriminator(SwapEventName);

    /// <summary>True only for the exact shape seen on the live feed.</summary>
    public static bool IsToleratedSwapEvent(ulong discriminator, int payloadBytes) =>
        discriminator == SwapEventDiscriminator && payloadBytes == SwapEventPayloadBytes;

    /// <summary>
    /// True when any checked-in IDL declares the payload's discriminator.
    ///
    /// Three dictionary lookups and no field reads, so it is cheap enough to
    /// decide what to sample on a hot path.
    /// </summary>
    public static bool IsDeclaredAnywhere(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8)
        {
            return false;
        }

        var discriminator = BitConverter.ToUInt64(payload[..8]);
        return PumpIdl.Pump.Events.ContainsKey(discriminator)
            || PumpIdl.PumpSwap.Events.ContainsKey(discriminator)
            || PumpIdl.PumpFees.Events.ContainsKey(discriminator);
    }

    /// <summary>Anchor's event discriminator: the first eight bytes of sha256("event:" + name).</summary>
    public static ulong AnchorEventDiscriminator(string name)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes("event:" + name), hash);
        return BitConverter.ToUInt64(hash[..8]);
    }
}
