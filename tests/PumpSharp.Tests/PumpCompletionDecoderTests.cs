using System.Text.Json;
using PumpSharp;

namespace PumpSharp.Tests;

public class PumpCompletionDecoderTests
{
    private static readonly string[] Fields = ["user", "mint", "bonding_curve", "timestamp", "quote_mint"];

    [Fact]
    public void Completion_is_field_decoded_with_exact_consumption_and_dispatch()
    {
        var mint = PumpAddresses.PumpProgram;
        var quote = PumpAddresses.PumpSwapProgram;
        var bytes = new BorshTestWriter()
            .Discriminator(PumpIdl.Pump.Events.Single(e => e.Value == "CompleteEvent").Key)
            .Key(quote).Key(mint).Key(mint).I64(-123).Key(quote).ToArray();
        Assert.Equal(144, bytes.Length);
        Assert.Equal(PumpDecodeFailure.None, PumpEventDecoder.Decode(bytes, out var decoded, out var diagnostics));
        Assert.True(decoded.HasTypedEvent);
        Assert.Equal(PumpEventKind.Complete, decoded.Kind);
        var completion = Assert.IsType<PumpCompletionEvent>(decoded.Completion);
        Assert.Equal(quote, completion.User);
        Assert.Equal(mint, completion.Mint);
        Assert.Equal(mint, completion.BondingCurve);
        Assert.Equal(-123, completion.Timestamp);
        Assert.Equal(quote, completion.QuoteMint);
        Assert.Equal(144, diagnostics.ConsumedBytes);
    }

    [Fact]
    public void Every_truncated_prefix_and_every_extra_byte_is_refused()
    {
        var bytes = PumpEventFixtures.CompletionPayload(PumpAddresses.PumpProgram);
        for (var length = 0; length < bytes.Length; length++)
        {
            Assert.Equal(PumpDecodeFailure.Truncated,
                PumpEventDecoder.DecodeCompletion(bytes.AsSpan(0, length), out var completion, out _));
            Assert.Null(completion);
        }

        Assert.Equal(PumpDecodeFailure.TrailingBytes,
            PumpEventDecoder.DecodeCompletion(bytes.Concat(new byte[1]).ToArray(), out var extra, out var consumed));
        Assert.Null(extra);
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void Wrong_unknown_and_oversized_payloads_are_distinct()
    {
        Assert.Equal(PumpDecodeFailure.WrongEvent, PumpEventDecoder.DecodeCompletion(
            PumpEventFixtures.KnownEventPayload("CollectCreatorFeeEvent"), out _, out _));
        Assert.Equal(PumpDecodeFailure.UnknownDiscriminator, PumpEventDecoder.DecodeCompletion(new byte[8], out _, out _));
        Assert.Equal(PumpDecodeFailure.PayloadTooLarge,
            PumpEventDecoder.DecodeCompletion(new byte[PumpEventDecoder.MaxPayloadBytes + 1], out _, out _));
    }

    [Fact]
    public void The_completion_layout_is_pinned_to_the_embedded_idl()
    {
        using var stream = typeof(PumpIdl).Assembly.GetManifestResourceStream("PumpSharp.protocol.pump.json");
        using var document = JsonDocument.Parse(stream!);
        var fields = document.RootElement.GetProperty("types").EnumerateArray()
            .Single(t => t.GetProperty("name").GetString() == "CompleteEvent")
            .GetProperty("type").GetProperty("fields").EnumerateArray().ToArray();
        Assert.Equal(Fields, fields.Select(f => f.GetProperty("name").GetString()));
        Assert.Equal("i64", fields[3].GetProperty("type").GetString());
        Assert.All(fields.Where((_, i) => i != 3), f => Assert.Equal("pubkey", f.GetProperty("type").GetString()));
    }
}
