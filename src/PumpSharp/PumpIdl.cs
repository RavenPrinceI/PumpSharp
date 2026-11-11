using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text.Json;

namespace PumpSharp;

/// <summary>
/// The checked-in official Pump IDLs, loaded once.
///
/// <see cref="PumpProtocolFingerprint"/> records the IDLs' hashes so drift
/// can be detected. The IDLs are embedded resources, so the bytes that are
/// hashed are the same bytes that are parsed.
///
/// This type reads the IDL declarations only: event and account names and
/// their discriminators. Field decoding and quote maths live in the decoder
/// and quote types.
///
/// Discriminators are read from the IDL rather than derived from
/// sha256("event:" + Name). The two agree, but the IDL covers every
/// declared event.
/// </summary>
public static class PumpIdl
{
    private static readonly Lazy<PumpIdlContents> PumpLazy =
        new(() => Load("PumpSharp.protocol.pump.json"));

    private static readonly Lazy<PumpIdlContents> PumpSwapLazy =
        new(() => Load("PumpSharp.protocol.pump_amm.json"));

    private static readonly Lazy<PumpIdlContents> PumpFeesLazy =
        new(() => Load("PumpSharp.protocol.pump_fees.json"));

    /// <summary>The Pump bonding-curve program's IDL.</summary>
    public static PumpIdlContents Pump => PumpLazy.Value;

    /// <summary>The PumpSwap AMM program's IDL.</summary>
    public static PumpIdlContents PumpSwap => PumpSwapLazy.Value;

    /// <summary>
    /// The Pump fee program's IDL.
    ///
    /// Pump calls the fee program by CPI, so its events appear in the same
    /// logs. Without this IDL, each of those events would count as an
    /// undeclared discriminator, which on a live feed meant several thousand
    /// false drift reports per ninety seconds.
    ///
    /// Nothing decodes these events. The IDL lets them be recognised as another
    /// program's expected events rather than as unknown layouts.
    /// </summary>
    public static PumpIdlContents PumpFees => PumpFeesLazy.Value;

    /// <summary>Indirection so the AMM IDL is only parsed when something asks for it.</summary>
    public sealed class PumpSwapIdlAccessor
    {
        public static PumpIdlContents Contents => PumpSwapLazy.Value;
    }

    private static PumpIdlContents Load(string resourceName)
    {
        var assembly = typeof(PumpIdl).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException(
                $"The IDL resource '{resourceName}' is not embedded in the assembly. "
                + "Protocol layouts are read from the official IDLs and cannot be inferred.");

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        var address = root.TryGetProperty("address", out var addressElement)
            ? addressElement.GetString() ?? string.Empty
            : string.Empty;

        var version = root.TryGetProperty("metadata", out var metadata)
                      && metadata.TryGetProperty("version", out var versionElement)
            ? versionElement.GetString() ?? "unknown"
            : "unknown";

        return new PumpIdlContents
        {
            ProgramAddress = address,
            Version = version,
            Sha256 = sha256,
            SizeBytes = bytes.Length,
            Events = ReadDiscriminators(root, "events"),
            Accounts = ReadDiscriminators(root, "accounts"),
        };
    }

    /// <summary>
    /// Reads a section of declarations into a lookup keyed by the 8
    /// discriminator bytes read as a little-endian u64, so the classifier can
    /// compare without allocating.
    /// </summary>
    private static FrozenDictionary<ulong, string> ReadDiscriminators(
        JsonElement root,
        string section)
    {
        if (!root.TryGetProperty(section, out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return FrozenDictionary<ulong, string>.Empty;
        }

        var map = new Dictionary<ulong, string>();
        Span<byte> discriminator = stackalloc byte[8];

        foreach (var item in items.EnumerateArray())
        {
            if (!item.TryGetProperty("name", out var nameElement)
                || !item.TryGetProperty("discriminator", out var discriminatorElement)
                || discriminatorElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            discriminator.Clear();
            var index = 0;
            foreach (var octet in discriminatorElement.EnumerateArray())
            {
                if (index >= 8)
                {
                    break;
                }

                discriminator[index++] = (byte)octet.GetInt32();
            }

            if (index != 8)
            {
                continue;
            }

            // Last declaration wins on a collision, which cannot happen in a valid
            // Anchor IDL. TryAdd would quietly keep the first and hide a malformed
            // file.
            map[BitConverter.ToUInt64(discriminator)] = nameElement.GetString() ?? "unnamed";
        }

        return map.ToFrozenDictionary();
    }
}

/// <summary>One parsed IDL, reduced to what this library reads from it.</summary>
public sealed record PumpIdlContents
{
    /// <summary>Program address declared by the IDL itself.</summary>
    public required string ProgramAddress { get; init; }

    /// <summary>Version string from the IDL metadata.</summary>
    public required string Version { get; init; }

    /// <summary>SHA-256 of the exact embedded bytes, for drift checks.</summary>
    public required string Sha256 { get; init; }

    public required int SizeBytes { get; init; }

    /// <summary>Event discriminator to event name.</summary>
    public required FrozenDictionary<ulong, string> Events { get; init; }

    /// <summary>Account discriminator to account name.</summary>
    public required FrozenDictionary<ulong, string> Accounts { get; init; }
}
