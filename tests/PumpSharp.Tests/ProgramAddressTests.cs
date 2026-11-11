using System.Text;
using PumpSharp.Chain;
using SolSharp.Core.Primitives;
using SolSharp.Programs;

namespace PumpSharp.Tests;

/// <summary>
/// PDA derivation, checked against a separate implementation.
///
/// A test that only compared this code with itself would pass with a wrong
/// byte order, a missing marker or no curve check. The useful check is
/// agreement with Solana, and the closest available stand-in is SolSharp's
/// implementation, written separately from the same specification.
///
/// Two implementations agreeing rules out a copying mistake in either, but
/// not a misunderstanding they share. A PDA is just a hash and a curve
/// check, so that is enough here, though it would not be for a quote.
///
/// The curve check matters most. Without it, about half of all derivations
/// would return an address the runtime rejects, and a test over a few mints
/// could still pass.
/// </summary>
public class ProgramAddressTests
{
    private static Pubkey Key(byte seed)
    {
        Span<byte> bytes = stackalloc byte[Pubkey.Length];
        bytes.Fill(seed);

        // Arbitrary bytes standing in for program ids and mints, which derivation
        // treats as arbitrary bytes anyway.
        return new Pubkey(bytes);
    }

    private static PublicKey ToSolSharp(Pubkey key)
    {
        Span<byte> bytes = stackalloc byte[Pubkey.Length];
        key.WriteTo(bytes);
        return new PublicKey(bytes);
    }

    [Theory]
    [InlineData("bonding-curve")]
    [InlineData("creator-vault")]
    [InlineData("global")]
    [InlineData("")]
    public void Derivation_agrees_with_an_independent_implementation(string literal)
    {
        var programId = Key(0x11);

        var mintBytes = new byte[Pubkey.Length];

        for (byte mintSeed = 1; mintSeed <= 32; mintSeed++)
        {
            var mint = Key(mintSeed);
            mint.WriteTo(mintBytes);

            ReadOnlyMemory<byte>[] seeds =
                [Encoding.UTF8.GetBytes(literal), mintBytes.ToArray()];

            Assert.True(
                ProgramAddress.TryFind(seeds, programId, out var ours, out var bump),
                $"no bump produced an off-curve address for seed {mintSeed}");

            var (theirs, theirBump) = ProgramDerivedAddress.FindProgramAddress(
                [.. seeds.Select(s => s.ToArray())],
                ToSolSharp(programId));

            Assert.Equal(theirs.ToString(), ours.ToString());
            Assert.Equal(theirBump, bump);
        }
    }

    [Fact]
    public void A_derived_address_is_never_on_the_curve()
    {
        var programId = Key(0x22);

        for (byte seed = 1; seed <= 64; seed++)
        {
            ReadOnlyMemory<byte>[] seeds = [new byte[] { seed }];

            Assert.True(ProgramAddress.TryFind(seeds, programId, out var address, out _));
            Assert.False(
                ProgramAddress.IsOnCurve(address),
                "find_program_address returned an address with a possible private key");
        }
    }

    [Fact]
    public void The_curve_check_rejects_about_half_of_arbitrary_byte_strings()
    {
        // A property of the curve, not of this code. A check that called
        // everything "off curve" would pass every other test here. If this count
        // drops to zero or rises to the sample size, the Legendre symbol is not
        // being computed.
        var onCurve = 0;

        var bytes = new byte[Pubkey.Length];

        for (var seed = 0; seed < 256; seed++)
        {
            System.Security.Cryptography.SHA256.HashData(
                BitConverter.GetBytes(seed),
                bytes);

            if (ProgramAddress.IsOnCurve(new Pubkey(bytes)))
            {
                onCurve++;
            }
        }

        Assert.InRange(onCurve, 90, 166);
    }

    [Fact]
    public void A_known_ed25519_public_key_is_on_the_curve()
    {
        // The system program's address is all zeroes, which is not a curve point.
        // The Token program's address is a published address and must be one. A
        // check that could not tell them apart would be doing nothing.
        Assert.True(
            Pubkey.TryParse("TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA", out var token));

        Assert.True(ProgramAddress.IsOnCurve(token));
    }

    [Fact]
    public void An_oversized_seed_is_refused_rather_than_truncated()
    {
        ReadOnlyMemory<byte>[] seeds = [new byte[ProgramAddress.MaxSeedLength + 1]];

        Assert.False(ProgramAddress.TryFind(seeds, Key(0x33), out _, out _));
        Assert.False(ProgramAddress.TryCreate(seeds, Key(0x33), out _));
    }

    [Fact]
    public void Too_many_seeds_are_refused()
    {
        // TryFind needs a slot for the bump, so it refuses one seed earlier than
        // TryCreate. Both limits are the runtime's.
        var atTheLimit = Enumerable
            .Range(0, ProgramAddress.MaxSeeds)
            .Select(_ => (ReadOnlyMemory<byte>)new byte[] { 1 })
            .ToArray();

        var pastTheLimit = atTheLimit.Append((ReadOnlyMemory<byte>)new byte[] { 1 }).ToArray();

        // A full seed set leaves TryFind no room for the bump. TryCreate takes the
        // seeds as given and only refuses past the runtime's limit.
        Assert.False(ProgramAddress.TryFind(atTheLimit, Key(0x44), out _, out _));
        Assert.False(ProgramAddress.TryCreate(pastTheLimit, Key(0x44), out _));
    }

    [Fact]
    public void Derivation_is_deterministic()
    {
        ReadOnlyMemory<byte>[] seeds = ["bonding-curve"u8.ToArray()];

        Assert.True(ProgramAddress.TryFind(seeds, Key(0x55), out var first, out var firstBump));
        Assert.True(ProgramAddress.TryFind(seeds, Key(0x55), out var second, out var secondBump));

        Assert.Equal(first, second);
        Assert.Equal(firstBump, secondBump);
    }
}
