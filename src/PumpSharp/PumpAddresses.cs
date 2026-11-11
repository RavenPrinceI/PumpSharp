using PumpSharp.Chain;
using System.Buffers.Binary;

namespace PumpSharp;

/// <summary>
/// Pump program-derived addresses, from the seeds the checked-in official
/// IDL declares.
///
/// Each seed is copied from the <c>pda.seeds</c> block the IDL attaches to
/// the account in an instruction's account list. The IDL writes seeds as
/// byte arrays; the UTF-8 text is shown beside each constant, and
/// <c>PumpAddressTests</c> checks every constant against the IDL, so a
/// copying mistake fails the build instead of producing a plausible wrong
/// address.
///
/// No event carries a bonding curve's address; it is derived from the mint.
/// Derivation needs no RPC call.
///
/// Associated token accounts and volume accumulators are not derived here.
/// </summary>
public static class PumpAddresses
{
    /// <summary>IDL seed <c>[98,111,110,100,105,110,103,45,99,117,114,118,101]</c>: "bonding-curve".</summary>
    private static ReadOnlyMemory<byte> BondingCurveSeed { get; } = "bonding-curve"u8.ToArray();

    /// <summary>IDL seed <c>[99,114,101,97,116,111,114,45,118,97,117,108,116]</c>: "creator-vault".</summary>
    private static ReadOnlyMemory<byte> CreatorVaultSeed { get; } = "creator-vault"u8.ToArray();

    /// <summary>IDL seed <c>[103,108,111,98,97,108]</c>: "global".</summary>
    private static ReadOnlyMemory<byte> GlobalSeed { get; } = "global"u8.ToArray();

    /// <summary>From pump_amm.json admin_cto_pool/pool_authority: derived under Pump, not PumpSwap.</summary>
    private static ReadOnlyMemory<byte> PoolAuthoritySeed { get; } = "pool-authority"u8.ToArray();

    /// <summary>The Pump program id as a parsed address, derived once.</summary>
    public static Pubkey PumpProgram { get; } = Parse(PumpProgramIds.Pump);

    /// <summary>The PumpSwap program id as a parsed address, derived once.</summary>
    public static Pubkey PumpSwapProgram { get; } = Parse(PumpProgramIds.PumpSwap);
    public static Pubkey PumpFeesProgram { get; } = Parse(PumpIdl.PumpFees.ProgramAddress);

    /// <summary>pump_amm.json create_pool: ["pool", index:u16 LE, creator, base mint, quote mint].</summary>
    public static bool TryPool(ushort index, Pubkey creator, Pubkey baseMint, Pubkey quoteMint,
        out Pubkey address, out byte bump)
    {
        var indexBytes = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(indexBytes, index);
        ReadOnlyMemory<byte>[] seeds = ["pool"u8.ToArray(), indexBytes, Bytes(creator), Bytes(baseMint), Bytes(quoteMint)];
        return ProgramAddress.TryFind(seeds, PumpSwapProgram, out address, out bump);
    }

    /// <summary>pump_amm.json admin_update_token_incentives: ["global_config"] under PumpSwap.</summary>
    public static bool TrySwapGlobalConfig(out Pubkey address, out byte bump) =>
        ProgramAddress.TryFind(["global_config"u8.ToArray()], PumpSwapProgram, out address, out bump);

    /// <summary>pump_amm.json buy/fee_config: ["fee_config", PumpSwap program] under Pump Fees.</summary>
    public static bool TrySwapFeeConfig(out Pubkey address, out byte bump) =>
        ProgramAddress.TryFind(["fee_config"u8.ToArray(), Bytes(PumpSwapProgram)], PumpFeesProgram, out address, out bump);

    /// <summary>Pump buy_v2/fee_config: ["fee_config", Pump program] under Pump Fees.</summary>
    public static bool TryCurveFeeConfig(out Pubkey address, out byte bump) =>
        ProgramAddress.TryFind(["fee_config"u8.ToArray(), Bytes(PumpProgram)], PumpFeesProgram, out address, out bump);

    private static byte[] Bytes(Pubkey key)
    {
        var bytes = new byte[Pubkey.Length];
        key.WriteTo(bytes);
        return bytes;
    }

    /// <summary>
    /// The bonding curve account for a mint: seeds <c>["bonding-curve", mint]</c>.
    /// It holds the reserves every curve price is computed from.
    /// </summary>
    public static bool TryBondingCurve(Pubkey mint, out Pubkey address, out byte bump)
    {
        Span<byte> mintBytes = stackalloc byte[Pubkey.Length];
        mint.WriteTo(mintBytes);

        ReadOnlyMemory<byte>[] seeds = [BondingCurveSeed, mintBytes.ToArray()];
        return ProgramAddress.TryFind(seeds, PumpProgram, out address, out bump);
    }

    /// <summary>
    /// A creator's fee vault: seeds <c>["creator-vault", creator]</c>.
    ///
    /// There is one vault per creator, not per token, so its balance covers
    /// everything that creator has launched.
    /// </summary>
    public static bool TryCreatorVault(Pubkey creator, out Pubkey address, out byte bump)
    {
        Span<byte> creatorBytes = stackalloc byte[Pubkey.Length];
        creator.WriteTo(creatorBytes);

        ReadOnlyMemory<byte>[] seeds = [CreatorVaultSeed, creatorBytes.ToArray()];
        return ProgramAddress.TryFind(seeds, PumpProgram, out address, out bump);
    }

    /// <summary>
    /// The program's global configuration account: seed <c>["global"]</c>.
    ///
    /// Its initial virtual reserves are the baseline for curve progress, so
    /// reading it gives a progress figure for tokens first seen mid-life. It
    /// changes rarely and can be cached.
    /// </summary>
    public static bool TryGlobal(out Pubkey address, out byte bump)
    {
        ReadOnlyMemory<byte>[] seeds = [GlobalSeed];
        return ProgramAddress.TryFind(seeds, PumpProgram, out address, out bump);
    }

    /// <summary>
    /// A canonical pool's creator: seeds ["pool-authority", base mint] under
    /// Pump. See isPumpPool in Pump's FEE_PROGRAM_README.md.
    /// </summary>
    public static bool TryPoolAuthority(Pubkey baseMint, out Pubkey address, out byte bump)
    {
        Span<byte> mintBytes = stackalloc byte[Pubkey.Length];
        baseMint.WriteTo(mintBytes);
        ReadOnlyMemory<byte>[] seeds = [PoolAuthoritySeed, mintBytes.ToArray()];
        return ProgramAddress.TryFind(seeds, PumpProgram, out address, out bump);
    }

    /// <summary>
    /// Parses a program id that is a compile-time constant of this assembly.
    ///
    /// Throws instead of returning a failure: <see cref="PumpProgramIds"/> is
    /// fixed at build time, so a value that does not parse is a broken build,
    /// not something a caller could handle.
    /// </summary>
    private static Pubkey Parse(string base58) =>
        Pubkey.TryParse(base58, out var parsed)
            ? parsed
            : throw new InvalidOperationException(
                "A checked-in Pump program id is not a valid base58 address. "
                + "Protocol addresses cannot be inferred.");
}
