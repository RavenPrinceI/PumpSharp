using System.Buffers.Binary;

namespace PumpSharp.Chain;

/// <summary>
/// A 32-byte Solana address: a mint, a bonding curve, a wallet or a program.
///
/// Stored as four <see cref="ulong"/> fields, not a byte array, so an
/// address never allocates. Decoded events carry several addresses and a
/// busy feed produces over a thousand events a second. Four fields also
/// make equality and hashing four integer comparisons.
///
/// The words are read and written little-endian on every host, so a decoded
/// address round-trips exactly. The word split is a storage detail; do not
/// interpret the words as numbers.
/// </summary>
public readonly struct Pubkey : IEquatable<Pubkey>, IComparable<Pubkey>
{
    /// <summary>Length of a Solana address in bytes.</summary>
    public const int Length = 32;

    /// <summary>Length of the longest base58 rendering of 32 bytes.</summary>
    public const int MaxBase58Length = 44;

    private readonly ulong _word0;
    private readonly ulong _word1;
    private readonly ulong _word2;
    private readonly ulong _word3;

    /// <summary>Reads an address from exactly <see cref="Length"/> bytes.</summary>
    /// <exception cref="ArgumentException">The span is not 32 bytes long.</exception>
    public Pubkey(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Length)
        {
            throw new ArgumentException(
                $"A Solana address is {Length} bytes; got {bytes.Length}.",
                nameof(bytes));
        }

        _word0 = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        _word1 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]);
        _word2 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        _word3 = BinaryPrimitives.ReadUInt64LittleEndian(bytes[24..]);
    }

    /// <summary>The all-zero address, which Solana uses for "none".</summary>
    public static Pubkey Default => default;

    /// <summary>True for the all-zero address. Pump uses it for an absent optional account.</summary>
    public bool IsDefault => (_word0 | _word1 | _word2 | _word3) == 0;

    /// <summary>Writes the 32 bytes into <paramref name="destination"/>.</summary>
    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Length)
        {
            throw new ArgumentException(
                $"Need {Length} bytes to write an address; got {destination.Length}.",
                nameof(destination));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, _word0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], _word1);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[16..], _word2);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[24..], _word3);
    }

    /// <summary>
    /// Parses a base58 address. Returns false instead of throwing, because
    /// addresses usually come from configuration or test data where a bad value
    /// should be reported.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<char> base58, out Pubkey pubkey)
    {
        pubkey = default;

        Span<byte> bytes = stackalloc byte[Length];
        if (!Base58.TryDecode(base58, bytes, out var written) || written != Length)
        {
            return false;
        }

        pubkey = new Pubkey(bytes);
        return true;
    }

    /// <summary>
    /// The base58 form, as shown by explorers and the Pump docs. Allocates a
    /// string, so keep it out of decode paths.
    /// </summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Length];
        WriteTo(bytes);
        return Base58.Encode(bytes) ?? string.Empty;
    }

    /// <summary>
    /// Orders addresses by their bytes, most significant first.
    ///
    /// Useful when records keyed by address must be serialised in a stable
    /// order, for example before hashing them.
    ///
    /// The words are stored little-endian, so comparing them as numbers would
    /// order by the least significant byte of each group of eight. Each word is
    /// byte-reversed before comparing, which gives the same result as comparing
    /// the 32 bytes in order.
    /// </summary>
    public int CompareTo(Pubkey other)
    {
        var comparison = Compare(_word0, other._word0);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Compare(_word1, other._word1);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = Compare(_word2, other._word2);
        return comparison != 0 ? comparison : Compare(_word3, other._word3);
    }

    private static int Compare(ulong left, ulong right) =>
        System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(left)
            .CompareTo(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(right));

    public static bool operator <(Pubkey left, Pubkey right) => left.CompareTo(right) < 0;

    public static bool operator >(Pubkey left, Pubkey right) => left.CompareTo(right) > 0;

    public static bool operator <=(Pubkey left, Pubkey right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Pubkey left, Pubkey right) => left.CompareTo(right) >= 0;

    public bool Equals(Pubkey other) =>
        _word0 == other._word0
        && _word1 == other._word1
        && _word2 == other._word2
        && _word3 == other._word3;

    public override bool Equals(object? obj) => obj is Pubkey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_word0, _word1, _word2, _word3);

    public static bool operator ==(Pubkey left, Pubkey right) => left.Equals(right);

    public static bool operator !=(Pubkey left, Pubkey right) => !left.Equals(right);
}
