using System.Numerics;
using System.Security.Cryptography;

namespace PumpSharp.Chain;

/// <summary>
/// Solana program-derived address (PDA) derivation:
/// <c>create_program_address</c> and <c>find_program_address</c>.
///
/// Implemented here so the library needs no Solana SDK for one pure
/// function. The tests check it against SolSharp's separate implementation.
///
/// A PDA is the SHA-256 of the seeds, the program id and the ASCII marker
/// <c>ProgramDerivedAddress</c>, refused if the result is a valid ed25519
/// public key. A PDA must have no private key, so an on-curve candidate is
/// discarded and <see cref="TryFind"/> tries the next bump. Without the
/// curve check the address would look right, but the runtime would reject
/// it.
///
/// Seeds are limited to <see cref="MaxSeeds"/> seeds of at most
/// <see cref="MaxSeedLength"/> bytes, the runtime's own limits. Enforcing
/// them lets the scratch buffer live on the stack, and a seed set the chain
/// would reject is refused here.
/// </summary>
public static class ProgramAddress
{
    /// <summary>Most seeds the Solana runtime allows.</summary>
    public const int MaxSeeds = 16;

    /// <summary>Most bytes per seed the Solana runtime allows.</summary>
    public const int MaxSeedLength = 32;

    /// <summary>The marker appended before hashing.</summary>
    private static ReadOnlySpan<byte> Marker => "ProgramDerivedAddress"u8;

    /// <summary>Largest buffer a legal seed set can need: seeds, bump, program id, marker.</summary>
    private const int ScratchBytes = (MaxSeeds * MaxSeedLength) + 1 + Pubkey.Length + 21;

    /// <summary>
    /// Derives the address for an exact seed set, failing if the result is a
    /// valid ed25519 key.
    ///
    /// This is <c>create_program_address</c>; a bump, if wanted, is the last
    /// seed. Most callers want <see cref="TryFind"/>, which searches for a bump.
    /// </summary>
    public static bool TryCreate(
        ReadOnlySpan<ReadOnlyMemory<byte>> seeds,
        Pubkey programId,
        out Pubkey address)
    {
        address = default;

        return seeds.Length <= MaxSeeds
            && SeedsAreLegal(seeds)
            && TryDerive(seeds, programId, bump: null, out address);
    }

    /// <summary>
    /// Searches bumps from 255 down for a seed set whose hash is off the curve.
    /// This is <c>find_program_address</c>.
    ///
    /// Returns false only when no bump from 255 to 0 works. Each candidate is on
    /// the curve with probability about one half, so this almost never happens,
    /// but it is possible and is reported rather than thrown.
    /// </summary>
    public static bool TryFind(
        ReadOnlySpan<ReadOnlyMemory<byte>> seeds,
        Pubkey programId,
        out Pubkey address,
        out byte bump)
    {
        address = default;
        bump = 0;

        // One short of MaxSeeds: the bump occupies the last slot.
        if (seeds.Length >= MaxSeeds || !SeedsAreLegal(seeds))
        {
            return false;
        }

        for (var candidate = 255; candidate >= 0; candidate--)
        {
            if (TryDerive(seeds, programId, (byte)candidate, out address))
            {
                bump = (byte)candidate;
                return true;
            }
        }

        address = default;
        return false;
    }

    /// <summary>
    /// True when 32 bytes decode to a point on the ed25519 curve, and so could
    /// have a private key.
    ///
    /// A compressed Edwards point is the <c>y</c> coordinate plus a sign bit.
    /// The curve equation gives <c>x^2 = (y^2 - 1) / (d*y^2 + 1)</c>, so the
    /// point exists when that ratio is a square in the field. Writing the ratio
    /// as <c>u/v</c>, it is a square exactly when <c>u*v</c> is (dividing by
    /// <c>v^2</c> does not change squareness), so one Legendre symbol answers
    /// the question without computing a square root. The sign bit picks between
    /// <c>x</c> and <c>-x</c>, which both exist or both do not, so it is
    /// ignored.
    ///
    /// The high bit is masked rather than rejected, as the reference
    /// implementation does for non-canonical encodings. That only matters for
    /// the nineteen values at the top of the range, which a SHA-256 output hits
    /// with probability about 2^-251.
    /// </summary>
    public static bool IsOnCurve(Pubkey candidate)
    {
        Span<byte> bytes = stackalloc byte[Pubkey.Length];
        candidate.WriteTo(bytes);

        // The sign bit is not part of y and does not affect whether the point
        // exists.
        bytes[31] &= 0x7f;

        var y = new BigInteger(bytes, isUnsigned: true, isBigEndian: false) % Prime;
        var ySquared = y * y % Prime;

        var u = Mod(ySquared - BigInteger.One);
        var v = Mod((D * ySquared) + BigInteger.One);

        if (v.IsZero)
        {
            return false;
        }

        if (u.IsZero)
        {
            // y is plus or minus one, so x is zero. Both are points.
            return true;
        }

        // Legendre symbol: one for a non-zero square, p-1 for a non-square.
        return BigInteger.ModPow(Mod(u * v), LegendreExponent, Prime).IsOne;
    }

    private static bool SeedsAreLegal(ReadOnlySpan<ReadOnlyMemory<byte>> seeds)
    {
        foreach (var seed in seeds)
        {
            if (seed.Length > MaxSeedLength)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Hashes one candidate and returns it when it is off the curve.
    ///
    /// The input is assembled in one stack buffer and hashed in one pass, so
    /// the exact bytes being hashed are visible in one place.
    /// </summary>
    private static bool TryDerive(
        ReadOnlySpan<ReadOnlyMemory<byte>> seeds,
        Pubkey programId,
        byte? bump,
        out Pubkey address)
    {
        Span<byte> scratch = stackalloc byte[ScratchBytes];
        var written = 0;

        foreach (var seed in seeds)
        {
            seed.Span.CopyTo(scratch[written..]);
            written += seed.Length;
        }

        if (bump is { } value)
        {
            scratch[written++] = value;
        }

        programId.WriteTo(scratch[written..]);
        written += Pubkey.Length;

        Marker.CopyTo(scratch[written..]);
        written += Marker.Length;

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(scratch[..written], hash);

        var candidate = new Pubkey(hash);
        if (IsOnCurve(candidate))
        {
            address = default;
            return false;
        }

        address = candidate;
        return true;
    }

    /// <summary>The ed25519 field prime, 2^255 - 19.</summary>
    private static BigInteger Prime { get; } = BigInteger.Pow(2, 255) - 19;

    /// <summary>(p - 1) / 2, the Legendre exponent.</summary>
    private static BigInteger LegendreExponent { get; } = (Prime - 1) / 2;

    /// <summary>The curve constant d, which is -121665 / 121666 (mod p).</summary>
    private static BigInteger D { get; } =
        Mod(-121665 * BigInteger.ModPow(121666, Prime - 2, Prime));

    /// <summary>
    /// Reduces into 0..p-1. <see cref="BigInteger"/>'s remainder keeps the sign
    /// of the dividend, so a negative intermediate would otherwise stay negative
    /// and compare unequal to the same field element.
    /// </summary>
    private static BigInteger Mod(BigInteger value)
    {
        var remainder = value % Prime;
        return remainder.Sign < 0 ? remainder + Prime : remainder;
    }
}
