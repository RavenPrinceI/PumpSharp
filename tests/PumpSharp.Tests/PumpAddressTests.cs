using System.Text.Json;
using PumpSharp.Chain;
using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// The Pump PDA seeds, checked against the IDL.
///
/// <see cref="PumpAddresses"/> writes its seeds as UTF-8 literals because
/// they are easier to read than byte arrays. A copying mistake would still
/// derive a valid-looking address, and callers would read an account that
/// does not exist for every token, which looks the same as a quiet market.
///
/// So the literals are checked against the byte arrays in the checked-in
/// IDL, read from the embedded resource the same way
/// <see cref="PumpIdl"/> reads it, so the bytes checked are the bytes
/// shipped.
///
/// This also catches drift: if Pump renames a seed in a future IDL, this
/// test fails when the IDL is updated.
/// </summary>
public class PumpAddressTests
{
    private static readonly string[] PoolSeedPaths = ["index", "creator", "base_mint", "quote_mint"];
    [Fact]
    public void Pool_derivation_matches_an_executed_official_sdk_2_2_0_example()
    {
        // Generated on 2026-10-31 with pumpPoolAuthorityPda and poolPda from the
        // pinned official SDK. The mint is from the SDK's buyQuoteSimulation.spec.ts.
        Assert.True(Pubkey.TryParse("8XrqT9yLg3Bef1epT1a1J9VqoNN7GKXmy3Wxc1u4pump", out var mint));
        Assert.True(Pubkey.TryParse(PumpQuoteMints.WrappedSol, out var quote));
        Assert.True(PumpAddresses.TryPoolAuthority(mint, out var authority, out _));
        Assert.Equal("4VjWQPEvt7sPhsMwFzSYWssmwFD77PTo4JxxTCaSutZZ", authority.ToString());
        Assert.True(PumpAddresses.TryPool(0, authority, mint, quote, out var pool, out _));
        Assert.Equal("GVgCzr2jt8XHSs3XZxfqhUA2gt23UjF6m6FhK1id5Cd1", pool.ToString());
    }
    [Fact]
    public void Pool_and_configuration_derivations_match_the_checked_in_swap_idl()
    {
        using var stream = typeof(PumpIdl).Assembly.GetManifestResourceStream("PumpSharp.protocol.pump_amm.json");
        using var document = JsonDocument.Parse(stream!);
        var instructions = document.RootElement.GetProperty("instructions").EnumerateArray().ToArray();
        var pool = instructions.Single(i => i.GetProperty("name").GetString() == "create_pool")
            .GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "pool")
            .GetProperty("pda").GetProperty("seeds").EnumerateArray().ToArray();
        Assert.Equal("pool", System.Text.Encoding.UTF8.GetString(pool[0].GetProperty("value").EnumerateArray().Select(b => b.GetByte()).ToArray()));
        Assert.Equal(PoolSeedPaths, pool.Skip(1).Select(s => s.GetProperty("path").GetString()));
        Assert.Equal("arg", pool[1].GetProperty("kind").GetString());
        Assert.Equal("u16", instructions.Single(i => i.GetProperty("name").GetString() == "create_pool")
            .GetProperty("args").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "index").GetProperty("type").GetString());
        var creator = PumpAddresses.PumpProgram;
        var baseMint = PumpAddresses.PumpSwapProgram;
        var quoteMint = new Pubkey(Enumerable.Repeat((byte)17, Pubkey.Length).ToArray());
        byte[] Bytes(Pubkey key) { var bytes = new byte[Pubkey.Length]; key.WriteTo(bytes); return bytes; }
        Assert.True(ProgramAddress.TryFind(["pool"u8.ToArray(), new byte[] { 0x34, 0x12 }, Bytes(creator), Bytes(baseMint), Bytes(quoteMint)],
            PumpAddresses.PumpSwapProgram, out var expected, out var expectedBump));
        Assert.True(PumpAddresses.TryPool(0x1234, creator, baseMint, quoteMint, out var actual, out var actualBump));
        Assert.Equal(expected, actual);
        Assert.Equal(expectedBump, actualBump);

        var globalSeed = instructions.Single(i => i.GetProperty("name").GetString() == "admin_update_token_incentives")
            .GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "global_config")
            .GetProperty("pda").GetProperty("seeds")[0].GetProperty("value").EnumerateArray().Select(b => b.GetByte()).ToArray();
        Assert.Equal("global_config", System.Text.Encoding.UTF8.GetString(globalSeed));
        Assert.True(ProgramAddress.TryFind([globalSeed], PumpAddresses.PumpSwapProgram, out expected, out expectedBump));
        Assert.True(PumpAddresses.TrySwapGlobalConfig(out actual, out actualBump));
        Assert.Equal(expected, actual);
        Assert.Equal(expectedBump, actualBump);

        var buy = instructions.Single(i => i.GetProperty("name").GetString() == "buy").GetProperty("accounts").EnumerateArray().ToArray();
        var feePda = buy.Single(a => a.GetProperty("name").GetString() == "fee_config").GetProperty("pda");
        var feeSeeds = feePda.GetProperty("seeds").EnumerateArray().Select(s => s.GetProperty("value").EnumerateArray().Select(b => b.GetByte()).ToArray()).ToArray();
        Assert.Equal("fee_config", System.Text.Encoding.UTF8.GetString(feeSeeds[0]));
        Assert.Equal(Bytes(PumpAddresses.PumpSwapProgram), feeSeeds[1]);
        Assert.Equal("fee_program", feePda.GetProperty("program").GetProperty("path").GetString());
        Assert.Equal(PumpAddresses.PumpFeesProgram.ToString(), buy.Single(a => a.GetProperty("name").GetString() == "fee_program").GetProperty("address").GetString());
        Assert.True(ProgramAddress.TryFind(feeSeeds.Select(s => (ReadOnlyMemory<byte>)s).ToArray(), PumpAddresses.PumpFeesProgram, out expected, out expectedBump));
        Assert.True(PumpAddresses.TrySwapFeeConfig(out actual, out actualBump));
        Assert.Equal(expected, actual);
        Assert.Equal(expectedBump, actualBump);
    }

    /// <summary>
    /// Reads the constant seed bytes the IDL attaches to one account in one
    /// instruction.
    ///
    /// Returns only the <c>const</c> seeds. An <c>account</c> seed (the mint or
    /// the creator) is supplied at runtime, so the IDL has nothing to compare it
    /// against.
    /// </summary>
    private static List<byte[]> ConstantSeeds(string instruction, string account)
    {
        using var stream = typeof(PumpIdl).Assembly
            .GetManifestResourceStream("PumpSharp.protocol.pump.json")
            ?? throw new InvalidOperationException("the Pump IDL is not embedded");

        using var document = JsonDocument.Parse(stream);

        foreach (var declared in document.RootElement.GetProperty("instructions").EnumerateArray())
        {
            if (declared.GetProperty("name").GetString() != instruction)
            {
                continue;
            }

            foreach (var declaredAccount in declared.GetProperty("accounts").EnumerateArray())
            {
                if (declaredAccount.GetProperty("name").GetString() != account
                    || !declaredAccount.TryGetProperty("pda", out var pda))
                {
                    continue;
                }

                var seeds = new List<byte[]>();

                foreach (var seed in pda.GetProperty("seeds").EnumerateArray())
                {
                    if (seed.GetProperty("kind").GetString() != "const")
                    {
                        continue;
                    }

                    seeds.Add([.. seed.GetProperty("value").EnumerateArray()
                        .Select(b => (byte)b.GetInt32())]);
                }

                return seeds;
            }
        }

        throw new InvalidOperationException(
            $"the IDL declares no pda for {instruction}/{account}");
    }

    [Theory]
    [InlineData("admin_cto", "bonding_curve", "bonding-curve")]
    [InlineData("admin_cto", "creator_vault", "creator-vault")]
    [InlineData("add_quote_control_mint", "global", "global")]
    public void The_seed_literals_match_the_idl(string instruction, string account, string literal)
    {
        var seeds = ConstantSeeds(instruction, account);

        Assert.Single(seeds);
        Assert.Equal(literal, System.Text.Encoding.UTF8.GetString(seeds[0]));
    }

    [Fact]
    public void The_program_ids_parse_and_match_the_idl()
    {
        Assert.Equal(PumpProgramIds.Pump, PumpAddresses.PumpProgram.ToString());
        Assert.Equal(PumpProgramIds.PumpSwap, PumpAddresses.PumpSwapProgram.ToString());
        Assert.Equal(PumpProgramIds.Pump, PumpIdl.Pump.ProgramAddress);
    }

    [Fact]
    public void A_bonding_curve_address_is_derived_deterministically_and_off_curve()
    {
        Assert.True(Pubkey.TryParse("So11111111111111111111111111111111111111112", out var mint));

        Assert.True(PumpAddresses.TryBondingCurve(mint, out var first, out var firstBump));
        Assert.True(PumpAddresses.TryBondingCurve(mint, out var second, out var secondBump));

        Assert.Equal(first, second);
        Assert.Equal(firstBump, secondBump);
        Assert.False(ProgramAddress.IsOnCurve(first));
    }

    [Fact]
    public void Two_mints_derive_two_curves()
    {
        Assert.True(Pubkey.TryParse("So11111111111111111111111111111111111111112", out var one));
        Assert.True(Pubkey.TryParse("TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA", out var two));

        Assert.True(PumpAddresses.TryBondingCurve(one, out var first, out _));
        Assert.True(PumpAddresses.TryBondingCurve(two, out var second, out _));

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void A_creator_vault_differs_from_a_bonding_curve_for_the_same_key()
    {
        // Both are two-seed derivations under the same program, so a bug that
        // dropped the literal seed would make them collide and read a plausible
        // but wrong account.
        Assert.True(Pubkey.TryParse("So11111111111111111111111111111111111111112", out var key));

        Assert.True(PumpAddresses.TryBondingCurve(key, out var curve, out _));
        Assert.True(PumpAddresses.TryCreatorVault(key, out var vault, out _));

        Assert.NotEqual(curve, vault);
    }

    [Fact]
    public void The_global_account_has_one_address()
    {
        Assert.True(PumpAddresses.TryGlobal(out var address, out _));
        Assert.False(address.IsDefault);
        Assert.False(ProgramAddress.IsOnCurve(address));
    }

    [Fact]
    public void Pool_authority_uses_the_mint_and_program_declared_by_the_swap_idl()
    {
        using var stream = typeof(PumpIdl).Assembly.GetManifestResourceStream("PumpSharp.protocol.pump_amm.json");
        using var document = JsonDocument.Parse(stream!);
        var pda = document.RootElement.GetProperty("instructions").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString() == "admin_cto_pool")
            .GetProperty("accounts").EnumerateArray()
            .Single(a => a.GetProperty("name").GetString() == "pool_authority").GetProperty("pda");
        var declaredSeeds = pda.GetProperty("seeds").EnumerateArray().ToArray();
        var literal = declaredSeeds[0].GetProperty("value").EnumerateArray().Select(b => b.GetByte()).ToArray();
        Assert.Equal("pool-authority", System.Text.Encoding.UTF8.GetString(literal));
        Assert.Equal(2, declaredSeeds.Length);
        Assert.Equal("pool.base_mint", declaredSeeds[1].GetProperty("path").GetString());
        var program = new Pubkey(pda.GetProperty("program").GetProperty("value").EnumerateArray()
            .Select(b => b.GetByte()).ToArray());
        Assert.Equal(PumpAddresses.PumpProgram, program);

        foreach (var mint in new[] { PumpAddresses.PumpProgram, PumpAddresses.PumpSwapProgram })
        {
            var mintBytes = new byte[Pubkey.Length];
            mint.WriteTo(mintBytes);
            ReadOnlyMemory<byte>[] seeds = [literal, mintBytes];
            Assert.True(ProgramAddress.TryFind(seeds, program, out var expected, out var expectedBump));
            Assert.True(PumpAddresses.TryPoolAuthority(mint, out var actual, out var actualBump));
            Assert.Equal(expected, actual);
            Assert.Equal(expectedBump, actualBump);
            Assert.False(ProgramAddress.IsOnCurve(actual));
        }
    }
}
