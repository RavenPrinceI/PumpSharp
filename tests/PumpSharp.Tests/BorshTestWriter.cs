using System.Buffers.Binary;
using PumpSharp.Chain;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// Builds Borsh payloads for decoder tests.
///
/// The library is read-only, so it has no writer. This one is written by
/// hand against the IDL, separately from the decoder. If both came from one
/// schema they could agree with each other and both be wrong, which is what
/// a round-trip test is meant to catch.
/// </summary>
internal sealed class BorshTestWriter
{
    private readonly List<byte> _bytes = [];

    /// <summary>The 8-byte Anchor event discriminator, as the IDL declares it.</summary>
    public BorshTestWriter Discriminator(ulong value) => U64(value);

    public BorshTestWriter U8(byte value)
    {
        _bytes.Add(value);
        return this;
    }

    public BorshTestWriter U16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        _bytes.AddRange(buffer);
        return this;
    }

    public BorshTestWriter U32(uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        _bytes.AddRange(buffer);
        return this;
    }

    public BorshTestWriter U64(ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        _bytes.AddRange(buffer);
        return this;
    }

    public BorshTestWriter I64(long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        _bytes.AddRange(buffer);
        return this;
    }

    public BorshTestWriter I128(Int128 value)
    {
        Span<byte> buffer = stackalloc byte[16];
        BinaryPrimitives.WriteInt128LittleEndian(buffer, value);
        _bytes.AddRange(buffer);
        return this;
    }

    public BorshTestWriter U128(UInt128 value)
    {
        Span<byte> buffer = stackalloc byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(buffer, value);
        _bytes.AddRange(buffer);
        return this;
    }

    public BorshTestWriter Bool(bool value) => U8(value ? (byte)1 : (byte)0);

    /// <summary>Writes a byte where a field belongs, for malformed-value tests.</summary>
    public BorshTestWriter RawByte(byte value) => U8(value);

    public BorshTestWriter Key(Pubkey value)
    {
        Span<byte> buffer = stackalloc byte[Pubkey.Length];
        value.WriteTo(buffer);
        _bytes.AddRange(buffer);
        return this;
    }

    /// <summary>A Borsh string: a 32-bit byte count then UTF-8.</summary>
    public BorshTestWriter Text(string value)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        U32((uint)utf8.Length);
        _bytes.AddRange(utf8);
        return this;
    }

    /// <summary>
    /// The count prefix of a Borsh vector, written separately from its elements
    /// so a test can write a count that does not match what follows.
    /// </summary>
    public BorshTestWriter VecPrefix(uint count) => U32(count);

    /// <summary>A <c>Shareholder</c>: a 32-byte address and a u16 share in basis points.</summary>
    public BorshTestWriter Shareholder(Pubkey address, ushort shareBps) => Key(address).U16(shareBps);

    public byte[] ToArray() => [.. _bytes];

    public BorshTestWriter PadTo(int minimumLength)
    {
        if (_bytes.Count < minimumLength) _bytes.AddRange(new byte[minimumLength - _bytes.Count]);
        return this;
    }

    /// <summary>The payload as a feed would carry it, for end-to-end tests.</summary>
    public string ToLogLine() =>
        PumpLogClassifier.ProgramDataPrefix + Convert.ToBase64String(ToArray());
}
