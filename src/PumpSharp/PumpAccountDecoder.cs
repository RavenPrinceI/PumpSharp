namespace PumpSharp;

/// <summary>Why an account payload did not decode. <see cref="None"/> is success.</summary>
public enum PumpAccountDecodeFailure
{
    /// <summary>Decoded cleanly.</summary>
    None = 0,

    /// <summary>Larger than <see cref="PumpAccountDecoder.MaxAccountBytes"/>. Refused before decoding.</summary>
    AccountTooLarge = 1,

    /// <summary>
    /// The IDL does not declare this 8-byte discriminator: either Pump shipped a
    /// new account layout or the IDL is out of date.
    /// </summary>
    UnknownDiscriminator = 2,

    /// <summary>A known account, but not the one this call was asked to decode.</summary>
    WrongAccount = 3,

    /// <summary>Ran out of bytes mid-field: the account is shorter than the layout.</summary>
    Truncated = 4,

    /// <summary>A value was not valid Borsh, such as a bool outside 0/1.</summary>
    MalformedValue = 5,
}

/// <summary>
/// Decodes Pump and PumpSwap account data into typed records.
///
/// Unlike <see cref="PumpEventDecoder"/>, trailing bytes are counted, not
/// refused. An event payload is exactly the serialised struct, so extra
/// bytes mean a layout mismatch. An account's length is its allocated
/// space, and a program may allocate extra room for later fields, so extra
/// bytes are normal. Too few bytes is still a refusal. The trailing count is
/// returned so a layout that grew shows up as a changed number.
///
/// Covers Pump's <c>BondingCurve</c> and <c>Global</c> and PumpSwap's
/// <c>Pool</c>, <c>FeeConfig</c> and <c>GlobalConfig</c>. Volume
/// accumulators are not decoded. The curve and the pool are the same token
/// before and after migration, so both decoders live here.
/// </summary>
public static class PumpAccountDecoder
{
    public static PumpAccountDecodeFailure DecodeGlobal(ReadOnlySpan<byte> account,
        out PumpGlobalConfig config, out int trailingBytes)
    {
        config = null!;
        trailingBytes = 0;
        if (account.Length > MaxAccountBytes) return PumpAccountDecodeFailure.AccountTooLarge;
        if (account.Length < DiscriminatorLength) return PumpAccountDecodeFailure.Truncated;
        if (!PumpIdl.Pump.Accounts.TryGetValue(BitConverter.ToUInt64(account[..DiscriminatorLength]), out var name))
            return PumpAccountDecodeFailure.UnknownDiscriminator;
        if (name != "Global") return PumpAccountDecodeFailure.WrongAccount;
        var reader = new BorshReader(account[DiscriminatorLength..]);
        reader.ReadBool(); // initialized (unused)
        SkipPubkeys(ref reader, 2); // authority, fee_recipient
        for (var index = 0; index < 4; index++) reader.ReadU64(); // initial reserves and supply
        var protocol = reader.ReadU64();
        reader.ReadPubkey(); reader.ReadBool(); // withdraw authority, enable_migrate
        var migration = reader.ReadU64();
        var creator = reader.ReadU64();
        SkipPubkeys(ref reader, 9); // fee_recipients, creator authorities
        reader.ReadBool(); // create_v2_enabled
        SkipPubkeys(ref reader, 2); // whitelist_pda, reserved_fee_recipient
        var mayhem = reader.ReadBool();
        SkipPubkeys(ref reader, 7);
        reader.ReadBool(); // is_cashback_enabled
        SkipPubkeys(ref reader, 8);
        var buyback = reader.ReadU64();
        reader.ReadU64(); // initial_virtual_quote_reserves
        reader.ReadPubkey(); // fixed one-element whitelisted_quote_mints
        var configurable = reader.ReadBool();
        var maxCreator = reader.ReadU64();
        reader.ReadPubkey(); reader.ReadBool(); reader.ReadU8(); // holder rewards, max_curve_depth
        if (reader.Failed) return reader.Failure is BorshFailure.Underrun
            ? PumpAccountDecodeFailure.Truncated : PumpAccountDecodeFailure.MalformedValue;
        trailingBytes = reader.Remaining;
        config = new(protocol, creator, buyback, configurable, maxCreator, migration, mayhem);
        return PumpAccountDecodeFailure.None;
    }
    /// <summary>Anchor account discriminators are 8 bytes.</summary>
    private const int DiscriminatorLength = 8;

    /// <summary>
    /// Largest account this decoder will read.
    ///
    /// A <c>BondingCurve</c> has 158 bytes of fields after the discriminator.
    /// Ten kilobytes leaves room for much larger layouts and limits what a
    /// hostile or misconfigured RPC response can cost. A larger account is
    /// refused, not decoded from its prefix.
    /// </summary>
    public const int MaxAccountBytes = 10 * 1024;

    /// <summary>The IDL's name for the bonding curve account.</summary>
    public const string BondingCurveAccountName = "BondingCurve";

    /// <summary>
    /// Decodes a <c>BondingCurve</c> account.
    ///
    /// Fields are read by hand in IDL order, as elsewhere in this library, so a
    /// protocol change shows up as a diff next to the IDL.
    /// </summary>
    public static PumpAccountDecodeFailure DecodeBondingCurve(
        ReadOnlySpan<byte> account,
        out PumpBondingCurveAccount decoded,
        out int trailingBytes)
    {
        decoded = null!;
        trailingBytes = 0;

        if (account.Length > MaxAccountBytes)
        {
            return PumpAccountDecodeFailure.AccountTooLarge;
        }

        if (account.Length < DiscriminatorLength)
        {
            return PumpAccountDecodeFailure.Truncated;
        }

        var discriminator = BitConverter.ToUInt64(account[..DiscriminatorLength]);
        if (!PumpIdl.Pump.Accounts.TryGetValue(discriminator, out var name))
        {
            return PumpAccountDecodeFailure.UnknownDiscriminator;
        }

        if (!string.Equals(name, BondingCurveAccountName, StringComparison.Ordinal))
        {
            return PumpAccountDecodeFailure.WrongAccount;
        }

        var reader = new BorshReader(account[DiscriminatorLength..]);

        var virtualTokenReserves = reader.ReadU64();
        var virtualQuoteReserves = reader.ReadU64();
        var realTokenReserves = reader.ReadU64();
        var realQuoteReserves = reader.ReadU64();
        var tokenTotalSupply = reader.ReadU64();
        var complete = reader.ReadBool();
        var creator = reader.ReadPubkey();
        var isMayhemMode = reader.ReadBool();
        var isCashbackCoin = reader.ReadBool();
        var quoteMint = reader.ReadPubkey();
        var creatorFeeBasisPoints = reader.ReadU64();
        var canEditCreatorFee = reader.ReadBool();
        var isHolderReward = reader.ReadBool();
        var creatorFee = reader.ReadU64();
        var protocolFees = reader.ReadU64();
        var depth = reader.ReadU8();
        var initialVirtualQuoteReserves = reader.ReadU64();
        var postCompleteBaseOut = reader.ReadU64();
        var postCompleteQuoteIn = reader.ReadU64();

        if (reader.Failed)
        {
            return reader.Failure is BorshFailure.Underrun
                ? PumpAccountDecodeFailure.Truncated
                : PumpAccountDecodeFailure.MalformedValue;
        }

        trailingBytes = reader.Remaining;

        decoded = new PumpBondingCurveAccount
        {
            VirtualTokenReserves = virtualTokenReserves,
            VirtualQuoteReserves = virtualQuoteReserves,
            RealTokenReserves = realTokenReserves,
            RealQuoteReserves = realQuoteReserves,
            TokenTotalSupply = tokenTotalSupply,
            Complete = complete,
            Creator = creator,
            IsMayhemMode = isMayhemMode,
            IsCashbackCoin = isCashbackCoin,
            QuoteMint = quoteMint,
            CreatorFeeBasisPoints = creatorFeeBasisPoints,
            CanEditCreatorFee = canEditCreatorFee,
            IsHolderReward = isHolderReward,
            CreatorFee = creatorFee,
            ProtocolFees = protocolFees,
            Depth = depth,
            InitialVirtualQuoteReserves = initialVirtualQuoteReserves,
            PostCompleteBaseOut = postCompleteBaseOut,
            PostCompleteQuoteIn = postCompleteQuoteIn,
        };

        return PumpAccountDecodeFailure.None;
    }

    /// <summary>The PumpSwap account name this decoder reads, as the IDL spells it.</summary>
    public const string PoolAccountName = "Pool";

    /// <summary>The PumpSwap fee-config account name, as the IDL spells it.</summary>
    public const string FeeConfigAccountName = "FeeConfig";

    /// <summary>Swap-global configuration; distinct from Pump's Global account.</summary>
    public const string SwapGlobalConfigAccountName = "GlobalConfig";

    /// <summary>
    /// Reads every field of the checked-in GlobalConfig layout and keeps the
    /// values needed for pricing. Extra trailing bytes are counted.
    /// </summary>
    public static PumpAccountDecodeFailure DecodeSwapGlobalConfig(
        ReadOnlySpan<byte> account,
        out PumpSwapGlobalConfig decoded,
        out int trailingBytes)
    {
        decoded = null!;
        trailingBytes = 0;

        if (account.Length > MaxAccountBytes)
        {
            return PumpAccountDecodeFailure.AccountTooLarge;
        }

        if (account.Length < DiscriminatorLength)
        {
            return PumpAccountDecodeFailure.Truncated;
        }

        var discriminator = BitConverter.ToUInt64(account[..DiscriminatorLength]);
        if (!PumpIdl.PumpSwap.Accounts.TryGetValue(discriminator, out var name))
        {
            return PumpAccountDecodeFailure.UnknownDiscriminator;
        }

        if (!string.Equals(name, SwapGlobalConfigAccountName, StringComparison.Ordinal))
        {
            return PumpAccountDecodeFailure.WrongAccount;
        }

        var reader = new BorshReader(account[DiscriminatorLength..]);
        reader.ReadPubkey(); // admin
        var lpFee = reader.ReadU64();
        var protocolFee = reader.ReadU64();
        var disableFlags = reader.ReadU8();
        SkipPubkeys(ref reader, 8); // protocol_fee_recipients
        var creatorFee = reader.ReadU64();
        SkipPubkeys(ref reader, 3); // admin_set_coin_creator_authority, whitelist_pda, reserved_fee_recipient
        var mayhem = reader.ReadBool();
        SkipPubkeys(ref reader, 7); // reserved_fee_recipients
        var cashback = reader.ReadBool();
        SkipPubkeys(ref reader, 8); // buyback_fee_recipients
        var buyback = reader.ReadU64();
        reader.ReadPubkey(); // boost_authority
        var boost = reader.ReadBool();
        var configurable = reader.ReadBool();
        var maxCreatorFee = reader.ReadU64();

        if (reader.Failed)
        {
            return reader.Failure is BorshFailure.Underrun
                ? PumpAccountDecodeFailure.Truncated
                : PumpAccountDecodeFailure.MalformedValue;
        }

        trailingBytes = reader.Remaining;
        decoded = new PumpSwapGlobalConfig
        {
            LpFeeBasisPoints = lpFee,
            ProtocolFeeBasisPoints = protocolFee,
            CoinCreatorFeeBasisPoints = creatorFee,
            DisableFlags = disableFlags,
            MayhemModeEnabled = mayhem,
            IsCashbackEnabled = cashback,
            BuybackBasisPoints = buyback,
            BoostEnabled = boost,
            CreatorFeeConfigurable = configurable,
            MaxConfigurableCreatorFeeBps = maxCreatorFee,
        };
        return PumpAccountDecodeFailure.None;
    }

    private static void SkipPubkeys(ref BorshReader reader, int count)
    {
        for (var i = 0; i < count; i++)
        {
            reader.ReadPubkey();
        }
    }

    /// <summary>
    /// Largest tier list read per schedule.
    ///
    /// The count comes from the account's own bytes, so a damaged or hostile
    /// account could claim millions of tiers. Mainnet had 25 tiers per schedule
    /// on 2026-10-31. A longer list is refused rather than truncated, because a
    /// truncated list would select a plausible wrong rate.
    /// </summary>
    public const int MaximumFeeTiers = 64;
    // Matches SDK 2.2.0 offlinePumpAmm.ts and FeeConfig::deserialize_versioned.
    public const int FeeConfigPreStableSize = 2512;
    public const int FeeConfigPostStableSize = 4073;
    public const int FeeConfigPostExoticSize = 4097;

    /// <summary>
    /// Decodes a PumpSwap <c>FeeConfig</c> account: every schedule a pool
    /// might pay. Pass the result to <see cref="PumpSwapFeeSchedule"/> to
    /// resolve a pool's rates.
    /// </summary>
    public static PumpAccountDecodeFailure DecodeFeeConfig(
        ReadOnlySpan<byte> account,
        out PumpSwapFeeConfig decoded,
        out int trailingBytes)
    {
        decoded = null!;
        trailingBytes = 0;

        if (account.Length > MaxAccountBytes)
        {
            return PumpAccountDecodeFailure.AccountTooLarge;
        }

        if (account.Length < DiscriminatorLength)
        {
            return PumpAccountDecodeFailure.Truncated;
        }

        var discriminator = BitConverter.ToUInt64(account[..DiscriminatorLength]);
        if (!PumpIdl.PumpSwap.Accounts.TryGetValue(discriminator, out var name))
        {
            return PumpAccountDecodeFailure.UnknownDiscriminator;
        }

        if (!string.Equals(name, FeeConfigAccountName, StringComparison.Ordinal))
        {
            return PumpAccountDecodeFailure.WrongAccount;
        }

        if (account.Length < FeeConfigPreStableSize) return PumpAccountDecodeFailure.Truncated;

        var reader = new BorshReader(account[DiscriminatorLength..]);

        var bump = reader.ReadU8();
        var admin = reader.ReadPubkey();
        var flatFees = ReadFees(ref reader);

        var feeTiers = ReadFeeTiers(ref reader, out var tierFailure);
        if (tierFailure != PumpAccountDecodeFailure.None)
        {
            return tierFailure;
        }

        // Empty on accounts written before the field existed, as the IDL notes.
        // That says how old the account is, not that the schedule has no tiers, so
        // it is kept, and the resolver refuses if asked to use it.
        PumpSwapFeeTier[] stableFeeTiers = [];
        if (account.Length >= FeeConfigPostStableSize)
        {
            stableFeeTiers = ReadFeeTiers(ref reader, out var stableFailure);
            if (stableFailure != PumpAccountDecodeFailure.None) return stableFailure;
        }

        var exoticFlatFees = account.Length >= FeeConfigPostExoticSize ? ReadFees(ref reader) : default;

        if (reader.Failed)
        {
            return reader.Failure is BorshFailure.Underrun
                ? PumpAccountDecodeFailure.Truncated
                : PumpAccountDecodeFailure.MalformedValue;
        }

        trailingBytes = reader.Remaining;

        decoded = new PumpSwapFeeConfig
        {
            Bump = bump,
            Admin = admin,
            FlatFees = flatFees,
            FeeTiers = feeTiers,
            StableFeeTiers = stableFeeTiers,
            ExoticFlatFees = exoticFlatFees,
        };

        return PumpAccountDecodeFailure.None;
    }

    /// <summary>Three u64 rates, in IDL order.</summary>
    private static PumpSwapFees ReadFees(ref BorshReader reader) => new()
    {
        LpFeeBps = reader.ReadU64(),
        ProtocolFeeBps = reader.ReadU64(),
        CreatorFeeBps = reader.ReadU64(),

        // Whether a creator fee applies depends on the pool having a coin
        // creator, which this account does not know. It defaults to false here and
        // the resolver sets it.
        HasCoinCreator = false,
    };

    /// <summary>
    /// A Borsh vector of tiers: a u32 count, then that many threshold and fee
    /// pairs. The count is checked against <see cref="MaximumFeeTiers"/> before
    /// anything is allocated.
    /// </summary>
    private static PumpSwapFeeTier[] ReadFeeTiers(
        ref BorshReader reader,
        out PumpAccountDecodeFailure failure)
    {
        failure = PumpAccountDecodeFailure.None;

        var count = reader.ReadU32();

        if (reader.Failed)
        {
            failure = PumpAccountDecodeFailure.Truncated;
            return [];
        }

        if (count > MaximumFeeTiers)
        {
            failure = PumpAccountDecodeFailure.MalformedValue;
            return [];
        }

        if (count == 0)
        {
            return [];
        }

        var tiers = new PumpSwapFeeTier[count];
        for (var i = 0; i < count; i++)
        {
            tiers[i] = new PumpSwapFeeTier
            {
                MarketCapThreshold = reader.ReadU128(),
                Fees = ReadFees(ref reader),
            };
        }

        return tiers;
    }

    /// <summary>
    /// Decodes a PumpSwap <c>Pool</c> account: the venue a token trades on
    /// after its curve completes.
    ///
    /// The discriminator is looked up in the PumpSwap IDL. Anchor derives an
    /// account discriminator from <c>sha256("account:" + Name)</c>, so two IDLs
    /// always agree on an account they both declare, such as
    /// <c>BondingCurve</c>. <c>Pool</c> is declared only by PumpSwap, so looking
    /// in the Pump IDL would report drift on an ordinary account.
    /// </summary>
    public static PumpAccountDecodeFailure DecodePool(
        ReadOnlySpan<byte> account,
        out PumpSwapPoolAccount decoded,
        out int trailingBytes)
    {
        decoded = null!;
        trailingBytes = 0;

        if (account.Length > MaxAccountBytes)
        {
            return PumpAccountDecodeFailure.AccountTooLarge;
        }

        if (account.Length < DiscriminatorLength)
        {
            return PumpAccountDecodeFailure.Truncated;
        }

        var discriminator = BitConverter.ToUInt64(account[..DiscriminatorLength]);
        if (!PumpIdl.PumpSwap.Accounts.TryGetValue(discriminator, out var name))
        {
            return PumpAccountDecodeFailure.UnknownDiscriminator;
        }

        if (!string.Equals(name, PoolAccountName, StringComparison.Ordinal))
        {
            return PumpAccountDecodeFailure.WrongAccount;
        }

        var reader = new BorshReader(account[DiscriminatorLength..]);

        var poolBump = reader.ReadU8();
        var index = reader.ReadU16();
        var creator = reader.ReadPubkey();
        var baseMint = reader.ReadPubkey();
        var quoteMint = reader.ReadPubkey();
        var lpMint = reader.ReadPubkey();
        var poolBaseTokenAccount = reader.ReadPubkey();
        var poolQuoteTokenAccount = reader.ReadPubkey();
        var lpSupply = reader.ReadU64();
        var coinCreator = reader.ReadPubkey();
        var isMayhemMode = reader.ReadBool();
        var isCashbackCoin = reader.ReadBool();

        // Signed on purpose; see PumpSwapPoolAccount.
        var virtualQuoteReserves = reader.ReadI128();

        var creatorFeeBps = reader.ReadU64();
        var canEditCreatorFee = reader.ReadBool();
        var isHolderReward = reader.ReadBool();
        var protocolFees = reader.ReadU64();
        var creatorFees = reader.ReadU64();

        if (reader.Failed)
        {
            return reader.Failure is BorshFailure.Underrun
                ? PumpAccountDecodeFailure.Truncated
                : PumpAccountDecodeFailure.MalformedValue;
        }

        trailingBytes = reader.Remaining;

        decoded = new PumpSwapPoolAccount
        {
            PoolBump = poolBump,
            Index = index,
            Creator = creator,
            BaseMint = baseMint,
            QuoteMint = quoteMint,
            LpMint = lpMint,
            PoolBaseTokenAccount = poolBaseTokenAccount,
            PoolQuoteTokenAccount = poolQuoteTokenAccount,
            LpSupply = lpSupply,
            CoinCreator = coinCreator,
            IsMayhemMode = isMayhemMode,
            IsCashbackCoin = isCashbackCoin,
            VirtualQuoteReserves = virtualQuoteReserves,
            CreatorFeeBps = creatorFeeBps,
            CanEditCreatorFee = canEditCreatorFee,
            IsHolderReward = isHolderReward,
            ProtocolFees = protocolFees,
            CreatorFees = creatorFees,
        };

        return PumpAccountDecodeFailure.None;
    }
}
