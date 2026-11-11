using System.Text.Json;
using PumpSharp;

namespace PumpSharp.Tests;

public class PumpSwapFeeAccountDecoderTests
{
    [Fact]
    public void Recorded_mainnet_fee_accounts_match_every_tier_decoded_by_the_official_sdk()
    {
        using var stream = typeof(PumpSwapFeeAccountDecoderTests).Assembly
            .GetManifestResourceStream("PumpSharp.Tests.parity.pump-swap-sdk-2.2.0-account-vectors.json");
        using var document = JsonDocument.Parse(stream!);
        Assert.Equal("2.2.0", document.RootElement.GetProperty("version").GetString());
        var mismatches = new List<string>();
        foreach (var vector in document.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var bytes = Convert.FromBase64String(vector.GetProperty("inputBase64").GetString()!);
            Assert.Equal(PumpAccountDecodeFailure.None, PumpAccountDecoder.DecodeFeeConfig(bytes, out var config, out _));
            Assert.Equal(vector.GetProperty("bump").GetByte(), config.Bump);
            Assert.Equal(vector.GetProperty("admin").GetString(), config.Admin.ToString());
            void CompareRates(JsonElement expected, PumpSwapFees actual, string name)
            {
                if (ulong.Parse(expected.GetProperty("lp").GetString()!, System.Globalization.CultureInfo.InvariantCulture) != actual.LpFeeBps
                    || ulong.Parse(expected.GetProperty("protocol").GetString()!, System.Globalization.CultureInfo.InvariantCulture) != actual.ProtocolFeeBps
                    || ulong.Parse(expected.GetProperty("creator").GetString()!, System.Globalization.CultureInfo.InvariantCulture) != actual.CreatorFeeBps)
                    mismatches.Add($"allocation {bytes.Length}: {name}");
            }
            void CompareTiers(string name, IReadOnlyList<PumpSwapFeeTier> actual)
            {
                var expected = vector.GetProperty(name).EnumerateArray().ToArray();
                Assert.Equal(expected.Length, actual.Count);
                for (var i = 0; i < actual.Count; i++)
                {
                    if (UInt128.Parse(expected[i].GetProperty("threshold").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
                        != actual[i].MarketCapThreshold) mismatches.Add($"allocation {bytes.Length}: {name}[{i}] threshold");
                    CompareRates(expected[i], actual[i].Fees, $"{name}[{i}]");
                }
            }
            CompareRates(vector.GetProperty("flat"), config.FlatFees, "flat");
            CompareRates(vector.GetProperty("exotic"), config.ExoticFlatFees, "exotic");
            CompareTiers("tiers", config.FeeTiers);
            CompareTiers("stableTiers", config.StableFeeTiers);
        }
        Assert.Empty(mismatches);
    }

    [Theory]
    [InlineData(2512, false, false)]
    [InlineData(4072, false, false)]
    [InlineData(4073, true, false)]
    [InlineData(4096, true, false)]
    [InlineData(4097, true, true)]
    public void Allocation_version_gates_ignore_stale_bytes_where_new_fields_would_be(int size, bool stable, bool exotic)
    {
        var bytes = FeeBytes();
        Assert.Equal(PumpAccountDecodeFailure.None, PumpAccountDecoder.DecodeFeeConfig(bytes.AsSpan(0, size), out var config, out _));
        Assert.Single(config.FeeTiers);
        Assert.Equal(stable ? 1 : 0, config.StableFeeTiers.Count);
        Assert.Equal(exotic ? Rates(31, 32, 33) : Rates(0, 0, 0), config.ExoticFlatFees);
    }
    private static readonly string[] FeeFields = ["bump", "admin", "flat_fees", "fee_tiers", "stable_fee_tiers", "exotic_flat_fees"];
    private static readonly string[] RateFields = ["lp_fee_bps", "protocol_fee_bps", "creator_fee_bps"];
    private static readonly string[] GlobalFields =
    [
        "admin", "lp_fee_basis_points", "protocol_fee_basis_points", "disable_flags", "protocol_fee_recipients",
        "coin_creator_fee_basis_points", "admin_set_coin_creator_authority", "whitelist_pda", "reserved_fee_recipient",
        "mayhem_mode_enabled", "reserved_fee_recipients", "is_cashback_enabled", "buyback_fee_recipients",
        "buyback_basis_points", "boost_authority", "boost_enabled", "creator_fee_configurable", "max_configurable_creator_fee_bps",
    ];
    private static readonly int[] RecipientCounts = [8, 7, 8];

    private static PumpSwapFees Rates(ulong lp, ulong protocol, ulong creator) => new()
    {
        LpFeeBps = lp, ProtocolFeeBps = protocol, CreatorFeeBps = creator, HasCoinCreator = false,
    };

    private static byte[] FeeBytes() => PumpSwapAccountFixtures.FeeConfig(
        Rates(71, 72, 73),
        [new() { MarketCapThreshold = (UInt128)ulong.MaxValue + 1, Fees = Rates(11, 12, 13) }],
        [new() { MarketCapThreshold = UInt128.MaxValue, Fees = Rates(21, 22, 23) }],
        Rates(31, 32, 33)).ToArray();

    [Fact]
    public void Fee_config_preserves_both_u128_thresholds_and_every_rate()
    {
        var bytes = FeeBytes();
        Assert.Equal(4097, bytes.Length); // Allocated version selects both schedules and exotic fees.
        Assert.Equal(PumpAccountDecodeFailure.None,
            PumpAccountDecoder.DecodeFeeConfig(bytes, out var config, out var trailing));
        Assert.Equal(4097 - 177, trailing);
        Assert.Equal(254, config.Bump);
        Assert.True(config.Admin.IsDefault);
        Assert.Equal(Rates(71, 72, 73), config.FlatFees);
        Assert.Equal((UInt128)ulong.MaxValue + 1, Assert.Single(config.FeeTiers).MarketCapThreshold);
        Assert.Equal(Rates(11, 12, 13), config.FeeTiers[0].Fees);
        Assert.Equal(UInt128.MaxValue, Assert.Single(config.StableFeeTiers).MarketCapThreshold);
        Assert.Equal(Rates(21, 22, 23), config.StableFeeTiers[0].Fees);
        Assert.Equal(Rates(31, 32, 33), config.ExoticFlatFees);
        Assert.True(config.ExoticFeesSet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Prefixes_below_the_minimum_allocation_are_refused_without_a_partial_account(bool global)
    {
        var bytes = global ? PumpSwapAccountFixtures.GlobalConfig().ToArray() : FeeBytes();
        var minimum = global ? bytes.Length : PumpAccountDecoder.FeeConfigPreStableSize;
        for (var length = 0; length < minimum; length++)
        {
            if (global)
            {
                Assert.Equal(PumpAccountDecodeFailure.Truncated,
                    PumpAccountDecoder.DecodeSwapGlobalConfig(bytes.AsSpan(0, length), out var config, out _));
                Assert.Null(config);
            }
            else
            {
                Assert.Equal(PumpAccountDecodeFailure.Truncated,
                    PumpAccountDecoder.DecodeFeeConfig(bytes.AsSpan(0, length), out var config, out _));
                Assert.Null(config);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tier_counts_are_bounded_before_allocation(bool stable)
    {
        var writer = new BorshTestWriter().Discriminator(PumpSwapAccountFixtures.Discriminator("FeeConfig"))
            .U8(1).Key(default).U64(0).U64(0).U64(0);
        if (stable)
        {
            writer.U32(0);
        }
        writer.U32(uint.MaxValue);
        writer.PadTo(stable ? PumpAccountDecoder.FeeConfigPostStableSize : PumpAccountDecoder.FeeConfigPreStableSize);

        Assert.Equal(PumpAccountDecodeFailure.MalformedValue,
            PumpAccountDecoder.DecodeFeeConfig(writer.ToArray(), out var config, out _));
        Assert.Null(config);
    }

    [Fact]
    public void Empty_tiers_are_preserved_without_inventing_a_schedule()
    {
        var bytes = PumpSwapAccountFixtures.FeeConfig(Rates(1, 2, 3), [], [], Rates(0, 0, 0)).ToArray();
        Assert.Equal(PumpAccountDecodeFailure.None,
            PumpAccountDecoder.DecodeFeeConfig(bytes, out var config, out _));
        Assert.Empty(config.FeeTiers);
        Assert.Empty(config.StableFeeTiers);
        Assert.False(config.ExoticFeesSet);
    }

    [Fact]
    public void Both_tier_lists_accept_their_maximum_size()
    {
        var tiers = Enumerable.Range(0, PumpAccountDecoder.MaximumFeeTiers)
            .Select(i => new PumpSwapFeeTier { MarketCapThreshold = (UInt128)i, Fees = Rates(1, 2, 3) }).ToArray();
        var bytes = PumpSwapAccountFixtures.FeeConfig(Rates(1, 2, 3), tiers, tiers, Rates(4, 5, 6)).ToArray();
        Assert.Equal(PumpAccountDecodeFailure.None, PumpAccountDecoder.DecodeFeeConfig(bytes, out var config, out _));
        Assert.Equal(PumpAccountDecoder.MaximumFeeTiers, config.FeeTiers.Count);
        Assert.Equal(PumpAccountDecoder.MaximumFeeTiers, config.StableFeeTiers.Count);
    }

    [Fact]
    public void Global_config_reads_flags_after_all_fixed_address_arrays()
    {
        var bytes = PumpSwapAccountFixtures.GlobalConfig(disableFlags: 24).ToArray();
        Assert.Equal(949, bytes.Length);
        Assert.Equal(PumpAccountDecodeFailure.None,
            PumpAccountDecoder.DecodeSwapGlobalConfig(bytes, out var config, out var trailing));
        Assert.Equal(0, trailing);
        Assert.Equal(21ul, config.LpFeeBasisPoints);
        Assert.Equal(6ul, config.ProtocolFeeBasisPoints);
        Assert.Equal(8ul, config.CoinCreatorFeeBasisPoints);
        Assert.Equal(9ul, config.BuybackBasisPoints);
        Assert.Equal(200ul, config.MaxConfigurableCreatorFeeBps);
        Assert.True(config.MayhemModeEnabled);
        Assert.True(config.IsCashbackEnabled);
        Assert.True(config.BoostEnabled);
        Assert.True(config.CreatorFeeConfigurable);
        Assert.True(config.BuyDisabled);
        Assert.True(config.SellDisabled);
    }

    [Theory]
    [InlineData(417)]
    [InlineData(642)]
    [InlineData(939)]
    [InlineData(940)]
    public void A_malformed_global_bool_is_refused_even_when_it_is_not_used_for_fees(int offset)
    {
        var bytes = PumpSwapAccountFixtures.GlobalConfig().ToArray();
        bytes[offset] = 2;
        Assert.Equal(PumpAccountDecodeFailure.MalformedValue,
            PumpAccountDecoder.DecodeSwapGlobalConfig(bytes, out var config, out _));
        Assert.Null(config);
    }

    [Fact]
    public void Account_allocation_padding_is_counted_for_both_configs()
    {
        Assert.Equal(PumpAccountDecodeFailure.None,
            PumpAccountDecoder.DecodeFeeConfig(FeeBytes().Concat(new byte[17]).ToArray(), out _, out var feeTail));
        Assert.Equal(4097 - 177 + 17, feeTail);
        Assert.Equal(PumpAccountDecodeFailure.None,
            PumpAccountDecoder.DecodeSwapGlobalConfig(
                PumpSwapAccountFixtures.GlobalConfig().ToArray().Concat(new byte[19]).ToArray(), out _, out var globalTail));
        Assert.Equal(19, globalTail);
    }

    [Fact]
    public void Wrong_unknown_and_oversized_accounts_have_distinct_refusals()
    {
        var fees = FeeBytes();
        var global = PumpSwapAccountFixtures.GlobalConfig().ToArray();
        Assert.Equal(PumpAccountDecodeFailure.WrongAccount, PumpAccountDecoder.DecodeFeeConfig(global, out _, out _));
        Assert.Equal(PumpAccountDecodeFailure.WrongAccount, PumpAccountDecoder.DecodeSwapGlobalConfig(fees, out _, out _));
        fees[0] ^= 0xff;
        global[0] ^= 0xff;
        Assert.Equal(PumpAccountDecodeFailure.UnknownDiscriminator, PumpAccountDecoder.DecodeFeeConfig(fees, out _, out _));
        Assert.Equal(PumpAccountDecodeFailure.UnknownDiscriminator, PumpAccountDecoder.DecodeSwapGlobalConfig(global, out _, out _));
        var oversized = new byte[PumpAccountDecoder.MaxAccountBytes + 1];
        Assert.Equal(PumpAccountDecodeFailure.AccountTooLarge, PumpAccountDecoder.DecodeFeeConfig(oversized, out _, out _));
        Assert.Equal(PumpAccountDecodeFailure.AccountTooLarge, PumpAccountDecoder.DecodeSwapGlobalConfig(oversized, out _, out _));
    }

    [Fact]
    public void The_checked_in_idl_pins_the_fee_and_global_layouts()
    {
        using var stream = typeof(PumpIdl).Assembly.GetManifestResourceStream("PumpSharp.protocol.pump_amm.json");
        using var document = JsonDocument.Parse(stream!);
        var types = document.RootElement.GetProperty("types").EnumerateArray()
            .Where(t => t.GetProperty("type").TryGetProperty("fields", out _)).ToDictionary(
            t => t.GetProperty("name").GetString()!, t => t.GetProperty("type").GetProperty("fields"));
        Assert.Equal(FeeFields,
            types["FeeConfig"].EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        Assert.Equal("u128", types["FeeTier"][0].GetProperty("type").GetString());
        Assert.Equal(RateFields,
            types["Fees"].EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        Assert.All(types["Fees"].EnumerateArray(), f => Assert.Equal("u64", f.GetProperty("type").GetString()));
        Assert.Equal(GlobalFields, types["GlobalConfig"].EnumerateArray().Select(f => f.GetProperty("name").GetString()));
        var addressArrays = types["GlobalConfig"].EnumerateArray()
            .Where(f => f.GetProperty("type").ValueKind == JsonValueKind.Object).ToArray();
        Assert.Equal(RecipientCounts, addressArrays.Select(f => f.GetProperty("type").GetProperty("array")[1].GetInt32()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(123)]
    public void An_unknown_schedule_does_not_fall_through_to_sol_tiers(int schedule)
    {
        PumpAccountDecoder.DecodeFeeConfig(FeeBytes(), out var config, out _);
        Assert.False(PumpSwapFeeSchedule.TryResolve(config, (PumpSwapQuoteSchedule)schedule, true,
            1, 1, 1, false, 0, false, true, out _, out var refusal));
        Assert.Equal(PumpSwapFeeScheduleRefusal.UnknownSchedule, refusal);
    }
}
