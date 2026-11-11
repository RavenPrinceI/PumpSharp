using System.Security.Cryptography;
using System.Text;

namespace PumpSharp;

/// <summary>
/// Counts the Pump events in a transaction's log lines, by kind.
///
/// It reads the 8-byte Anchor discriminator from each <c>Program data:</c>
/// line and maps it to a <see cref="PumpEventKind"/>. It decodes no fields
/// and does no arithmetic. For fields, use <see cref="PumpEventDecoder"/>.
///
/// Anchor computes an event discriminator as the first 8 bytes of
/// <c>sha256("event:" + EventName)</c>. This classifier reads the
/// discriminators from the checked-in IDL, and the tests check them against
/// that convention.
///
/// It runs once per notification, so it works over spans and a stack buffer
/// and does not allocate.
/// </summary>
public static class PumpLogClassifier
{
    /// <summary>
    /// Prefix of a log line carrying a base64 Anchor event payload. Public so
    /// the decoder matches on the same constant.
    /// </summary>
    public const string ProgramDataPrefix = "Program data: ";

    /// <summary>Anchor discriminators are 8 bytes.</summary>
    private const int DiscriminatorLength = 8;

    /// <summary>
    /// Longest base64 payload decoded. A Pump event is a few hundred bytes. The
    /// cap stops a hostile or malformed log line from forcing a large stack
    /// allocation.
    /// </summary>
    private const int MaxPayloadBytes = 1024;

    /// <summary>
    /// The four events counted individually, by name. Any other event the IDL
    /// declares becomes <see cref="PumpEventKind.KnownOther"/>, and a
    /// discriminator the IDL does not declare becomes
    /// <see cref="PumpEventKind.Unrecognised"/>, which indicates drift.
    /// </summary>
    private static readonly Dictionary<string, PumpEventKind> NamedKinds = new(StringComparer.Ordinal)
    {
        ["CreateEvent"] = PumpEventKind.Create,
        ["TradeEvent"] = PumpEventKind.Trade,
        ["CompleteEvent"] = PumpEventKind.Complete,
        ["CompletePumpAmmMigrationEvent"] = PumpEventKind.MigrationComplete,
    };

    /// <summary>
    /// Event names this library tracks individually. Exposed so the fingerprint
    /// can state what is tracked by name.
    /// </summary>
    public static IReadOnlyList<string> MappedEventNames { get; } =
    [
        "CreateEvent",
        "TradeEvent",
        "CompleteEvent",
        "CompletePumpAmmMigrationEvent",
    ];

    /// <summary>
    /// Counts the Pump events in one transaction's logs.
    ///
    /// <paramref name="counts"/> is indexed by <see cref="PumpEventKinds.Ordinal"/>
    /// and is added to, not replaced, so a caller can accumulate across
    /// notifications without allocating.
    /// </summary>
    /// <returns>Number of Anchor event payloads seen, decodable or not.</returns>
    public static int Classify(IReadOnlyList<string> logs, Span<int> counts)
    {
        ArgumentNullException.ThrowIfNull(logs);
        if (counts.Length < PumpEventKinds.Count)
        {
            throw new ArgumentException(
                $"counts must have at least {PumpEventKinds.Count} entries, "
                + "indexed by PumpEventKinds.Ordinal.",
                nameof(counts));
        }

        var payloads = 0;

        for (var i = 0; i < logs.Count; i++)
        {
            var line = logs[i];
            if (line is null || !line.StartsWith(ProgramDataPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            payloads++;
            var kind = ClassifyPayload(line.AsSpan(ProgramDataPrefix.Length));
            counts[PumpEventKinds.Ordinal(kind)]++;
        }

        return payloads;
    }

    /// <summary>
    /// Maps one base64 Anchor payload to a kind. Returns
    /// <see cref="PumpEventKind.Unrecognised"/> for anything that does not
    /// decode to at least a discriminator, so malformed input is counted instead
    /// of throwing.
    /// </summary>
    public static PumpEventKind ClassifyPayload(ReadOnlySpan<char> base64)
    {
        // Reject oversized input before decoding it.
        if (base64.Length is 0 or > (MaxPayloadBytes * 4 / 3) + 4)
        {
            return PumpEventKind.Unrecognised;
        }

        Span<byte> decoded = stackalloc byte[MaxPayloadBytes];
        if (!Convert.TryFromBase64Chars(base64, decoded, out var written)
            || written < DiscriminatorLength)
        {
            return PumpEventKind.Unrecognised;
        }

        var discriminator = BitConverter.ToUInt64(decoded[..DiscriminatorLength]);

        // Looked up in the checked-in IDL, which declares all 32 events. The
        // sha256("event:" + Name) derivation gives the same bytes, but a derived
        // table would only cover the names written into it.
        if (!PumpIdl.Pump.Events.TryGetValue(discriminator, out var eventName))
        {
            // Not declared by the IDL this build embeds. Either Pump shipped a new
            // event or the IDL is out of date.
            return PumpEventKind.Unrecognised;
        }

        return NamedKinds.TryGetValue(eventName, out var kind)
            ? kind
            : PumpEventKind.KnownOther;
    }

    /// <summary>
    /// Anchor event discriminator: the first 8 bytes of sha256("event:" + name).
    /// Public so tests can check the convention directly.
    /// </summary>
    public static ulong EventDiscriminator(string eventName)
    {
        ArgumentException.ThrowIfNullOrEmpty(eventName);

        var prefixed = "event:" + eventName;
        var byteCount = Encoding.UTF8.GetByteCount(prefixed);
        var buffer = byteCount <= 128 ? stackalloc byte[128] : new byte[byteCount];
        var encoded = Encoding.UTF8.GetBytes(prefixed, buffer);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(buffer[..encoded], hash);

        return BitConverter.ToUInt64(hash[..DiscriminatorLength]);
    }

}
