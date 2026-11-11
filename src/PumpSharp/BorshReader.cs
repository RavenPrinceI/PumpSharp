using System.Buffers.Binary;
using PumpSharp.Chain;

namespace PumpSharp;

/// <summary>How a <see cref="BorshReader"/> stopped.</summary>
public enum BorshFailure
{
    /// <summary>Nothing has gone wrong.</summary>
    None = 0,

    /// <summary>
    /// A read asked for more bytes than the payload had left, so the payload is
    /// shorter than the layout. This is what a protocol that added a field after
    /// the checked-in IDL looks like.
    /// </summary>
    Underrun = 1,

    /// <summary>
    /// A value was read but is not allowed: a bool outside 0 and 1, or a string
    /// or vector length larger than the bytes that remain. This is what a
    /// misaligned read or a hostile payload looks like.
    /// </summary>
    InvalidValue = 2,
}

/// <summary>
/// Sequential reader for the Borsh encoding Anchor uses for event payloads.
///
/// It reads little-endian integers, bools and 32-byte addresses, and skips
/// variable-length fields whose contents are not needed. There is no writer
/// and no schema walk. Decoders read fields by hand in IDL order, so the
/// expected layout sits next to the IDL it came from.
///
/// Failure is sticky. A <c>TradeEvent</c> has dozens of fields, and checking
/// a flag after each one invites a missed check. Instead, an underrun or a
/// malformed value sets <see cref="Failed"/>; every later read returns the
/// default and the position stops advancing. The caller checks once at the
/// end, since a partly decoded event is not usable anyway.
///
/// Nothing here allocates, and no length read from the payload is used
/// before it is compared with the bytes actually remaining.
/// </summary>
public ref struct BorshReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public BorshReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    /// <summary>
    /// How the read failed, or <see cref="BorshFailure.None"/>. Only the first
    /// failure is kept, because later reads on a failed reader fail as a
    /// consequence.
    /// </summary>
    public BorshFailure Failure { get; private set; }

    /// <summary>
    /// True once any read has underrun the buffer or found a malformed value.
    /// Once set it is never cleared.
    /// </summary>
    public bool Failed => Failure != BorshFailure.None;

    /// <summary>Bytes consumed so far. Stops advancing once <see cref="Failed"/>.</summary>
    public int Position => _position;

    /// <summary>Bytes left unread.</summary>
    public int Remaining => _buffer.Length - _position;

    /// <summary>
    /// True when every byte was consumed and nothing failed. Decoders require
    /// this: trailing bytes mean the layout read is not the layout encoded,
    /// which is a sign of protocol drift.
    /// </summary>
    public bool ConsumedExactly => !Failed && _position == _buffer.Length;

    public byte ReadU8() => TryTake(1, out var slice) ? slice[0] : default;

    public ushort ReadU16() =>
        TryTake(sizeof(ushort), out var slice)
            ? BinaryPrimitives.ReadUInt16LittleEndian(slice)
            : default;

    public uint ReadU32() =>
        TryTake(sizeof(uint), out var slice)
            ? BinaryPrimitives.ReadUInt32LittleEndian(slice)
            : default;

    public ulong ReadU64() =>
        TryTake(sizeof(ulong), out var slice)
            ? BinaryPrimitives.ReadUInt64LittleEndian(slice)
            : default;

    public long ReadI64() =>
        TryTake(sizeof(long), out var slice)
            ? BinaryPrimitives.ReadInt64LittleEndian(slice)
            : default;

    public UInt128 ReadU128() =>
        TryTake(16, out var slice)
            ? BinaryPrimitives.ReadUInt128LittleEndian(slice)
            : default;

    /// <summary>
    /// A signed 128-bit field.
    ///
    /// PumpSwap's <c>Pool.virtual_quote_reserves</c> uses this, and the sign
    /// matters: Pump's docs say the value may be negative. Read as a
    /// <see cref="UInt128"/>, a small negative reserve would become a huge
    /// positive one and the pool would be priced from it.
    /// </summary>
    public Int128 ReadI128() =>
        TryTake(16, out var slice)
            ? BinaryPrimitives.ReadInt128LittleEndian(slice)
            : default;

    /// <summary>
    /// Borsh encodes a bool as one byte that must be 0 or 1. Any other value is
    /// treated as malformed, not coerced: a byte of 0x7f where a bool belongs
    /// means the reader is misaligned, and reading it as true would carry that
    /// error into the decoded event.
    /// </summary>
    public bool ReadBool()
    {
        if (!TryTake(1, out var slice))
        {
            return false;
        }

        if (slice[0] > 1)
        {
            MarkFailed(BorshFailure.InvalidValue);
            return false;
        }

        return slice[0] == 1;
    }

    /// <summary>Reads a 32-byte address.</summary>
    public Pubkey ReadPubkey() =>
        TryTake(Pubkey.Length, out var slice) ? new Pubkey(slice) : default;

    /// <summary>
    /// Reads a Borsh string: a 32-bit byte count followed by UTF-8. This
    /// allocates, so use it for low-rate events whose text you need (a token's
    /// name, symbol and metadata URI). Use <see cref="SkipString"/> on the trade
    /// path.
    /// </summary>
    public string ReadString()
    {
        var length = ReadU32();
        if (Failed)
        {
            return string.Empty;
        }

        // The length comes from untrusted input, so it is checked against the
        // bytes that exist before it is used.
        if (length > (uint)Remaining)
        {
            MarkFailed(BorshFailure.InvalidValue);
            return string.Empty;
        }

        return TryTake((int)length, out var slice)
            ? System.Text.Encoding.UTF8.GetString(slice)
            : string.Empty;
    }

    /// <summary>
    /// Advances past a Borsh string without creating it. The bytes still have
    /// to be walked because Borsh has no field tags, so every later field's
    /// offset depends on this length.
    /// </summary>
    public void SkipString()
    {
        var length = ReadU32();
        if (Failed)
        {
            return;
        }

        if (length > (uint)Remaining)
        {
            MarkFailed(BorshFailure.InvalidValue);
            return;
        }

        _position += (int)length;
    }

    /// <summary>
    /// Advances past a Borsh vector of fixed-size elements: a 32-bit count
    /// followed by that many elements. The count is multiplied in 64 bits so a
    /// hostile count near <see cref="uint.MaxValue"/> cannot overflow into a
    /// small byte length.
    /// </summary>
    public void SkipVector(int elementSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(elementSize);

        var count = ReadU32();
        if (Failed)
        {
            return;
        }

        var bytes = (long)count * elementSize;
        if (bytes > Remaining)
        {
            MarkFailed(BorshFailure.InvalidValue);
            return;
        }

        _position += (int)bytes;
    }

    private bool TryTake(int count, out ReadOnlySpan<byte> slice)
    {
        if (Failed || count > Remaining)
        {
            MarkFailed(BorshFailure.Underrun);
            slice = default;
            return false;
        }

        slice = _buffer.Slice(_position, count);
        _position += count;
        return true;
    }

    /// <summary>
    /// Records a failure, keeping the first cause. Never cleared.
    /// </summary>
    private void MarkFailed(BorshFailure failure)
    {
        if (Failure == BorshFailure.None)
        {
            Failure = failure;
        }
    }
}
