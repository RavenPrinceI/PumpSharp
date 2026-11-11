namespace PumpSharp.Chain;

/// <summary>
/// Base58 in the Bitcoin alphabet, which Solana uses for addresses,
/// signatures and mints.
///
/// Implemented here so the library has no dependencies. The algorithm is
/// short, has one edge case (leading zeroes) and is round-trip tested.
///
/// Apart from the returned string, neither direction allocates. The working
/// buffers are on the stack, sized from a length limit that is checked
/// before any work starts.
/// </summary>
public static class Base58
{
    private const string Alphabet = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";

    /// <summary>
    /// Longest input accepted in either direction. A Solana address is 32 bytes
    /// (44 base58 characters) and a signature 64 bytes (88). This limit is above
    /// both and low enough to keep the quadratic conversion cheap. Longer input
    /// is rejected before it can size a stack buffer.
    /// </summary>
    public const int MaxLength = 128;

    /// <summary>
    /// log(256)/log(58) times 1000, rounded up: base58 digits per input byte.
    /// Used to size the conversion buffer once.
    /// </summary>
    private const int EncodedDigitsPerByteX1000 = 1366;

    /// <summary>Inverse: log(58)/log(256) times 1000, for the decode buffer.</summary>
    private const int DecodedBytesPerDigitX1000 = 733;

    /// <summary>
    /// Encodes bytes as base58. Returns <see langword="null"/> when the input
    /// exceeds <see cref="MaxLength"/>, and an empty string for empty input.
    /// </summary>
    public static string? Encode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxLength)
        {
            return null;
        }

        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        // Leading zero bytes have no numeric value, so each is written directly
        // as a '1' character.
        var zeros = 0;
        while (zeros < bytes.Length && bytes[zeros] == 0)
        {
            zeros++;
        }

        var capacity = (((bytes.Length - zeros) * EncodedDigitsPerByteX1000) / 1000) + 1;
        Span<byte> digits = stackalloc byte[capacity];
        digits.Clear();

        // Long multiplication. The buffer holds a big number, most significant
        // digit first, filled from the right. `length` counts the digits in use,
        // so each input byte only walks those.
        var length = 0;

        for (var index = zeros; index < bytes.Length; index++)
        {
            var carry = (int)bytes[index];
            var walked = 0;

            for (var position = capacity - 1;
                 (carry != 0 || walked < length) && position >= 0;
                 position--, walked++)
            {
                carry += 256 * digits[position];
                digits[position] = (byte)(carry % 58);
                carry /= 58;
            }

            length = walked;
        }

        var firstDigit = capacity - length;
        while (firstDigit < capacity && digits[firstDigit] == 0)
        {
            firstDigit++;
        }

        Span<char> output = stackalloc char[zeros + (capacity - firstDigit)];
        output[..zeros].Fill('1');

        for (var i = 0; i < capacity - firstDigit; i++)
        {
            output[zeros + i] = Alphabet[digits[firstDigit + i]];
        }

        return new string(output);
    }

    /// <summary>
    /// Decodes base58 into <paramref name="destination"/>. Returns false for an
    /// invalid character, an over-long input or a result that does not fit. It
    /// does not throw.
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<char> text, Span<byte> destination, out int written)
    {
        written = 0;

        if (text.Length is 0 or > MaxLength)
        {
            return false;
        }

        var zeros = 0;
        while (zeros < text.Length && text[zeros] == '1')
        {
            zeros++;
        }

        var capacity = (((text.Length - zeros) * DecodedBytesPerDigitX1000) / 1000) + 1;
        Span<byte> bytes = stackalloc byte[capacity];
        bytes.Clear();

        var length = 0;

        for (var index = zeros; index < text.Length; index++)
        {
            var digit = Alphabet.IndexOf(text[index], StringComparison.Ordinal);
            if (digit < 0)
            {
                return false;
            }

            var carry = digit;
            var walked = 0;

            for (var position = capacity - 1;
                 (carry != 0 || walked < length) && position >= 0;
                 position--, walked++)
            {
                carry += 58 * bytes[position];
                bytes[position] = (byte)(carry % 256);
                carry /= 256;
            }

            length = walked;
        }

        var firstByte = capacity - length;
        while (firstByte < capacity && bytes[firstByte] == 0)
        {
            firstByte++;
        }

        var decodedLength = zeros + (capacity - firstByte);
        if (decodedLength > destination.Length)
        {
            return false;
        }

        destination[..zeros].Clear();
        bytes[firstByte..capacity].CopyTo(destination[zeros..]);
        written = decodedLength;
        return true;
    }
}
