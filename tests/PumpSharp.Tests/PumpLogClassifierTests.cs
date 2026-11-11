using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// The event classifier.
///
/// These are not parity tests. They check that the Anchor discriminator
/// convention is applied as documented, that decoding is bounded against
/// hostile input, and that an unknown discriminator is counted as
/// unrecognised instead of being attributed to another kind.
///
/// Malformed event bytes must never cause unbounded allocation or a crash,
/// because this code runs on every notification of a public feed.
/// </summary>
public class PumpLogClassifierTests
{
    private static int[] NewCounts() => new int[PumpEventKinds.Count];

    private static string PayloadFor(string eventName)
    {
        var bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes, PumpLogClassifier.EventDiscriminator(eventName));
        return Convert.ToBase64String(bytes);
    }

    [Fact]
    public void The_discriminator_follows_the_anchor_convention()
    {
        // First 8 bytes of sha256("event:" + Name), recomputed here so the test
        // fails if the derivation changes.
        var expected = BitConverter.ToUInt64(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("event:TradeEvent")).AsSpan(0, 8));

        Assert.Equal(expected, PumpLogClassifier.EventDiscriminator("TradeEvent"));
    }

    [Fact]
    public void Different_events_have_different_discriminators()
    {
        var discriminators = PumpLogClassifier.MappedEventNames
            .Select(PumpLogClassifier.EventDiscriminator)
            .ToList();

        Assert.Equal(discriminators.Count, discriminators.Distinct().Count());
    }

    [Theory]
    [InlineData("CreateEvent", PumpEventKind.Create)]
    [InlineData("TradeEvent", PumpEventKind.Trade)]
    [InlineData("CompleteEvent", PumpEventKind.Complete)]
    [InlineData("CompletePumpAmmMigrationEvent", PumpEventKind.MigrationComplete)]
    public void A_mapped_event_payload_is_classified_as_its_kind(string eventName, PumpEventKind expected) =>
        Assert.Equal(expected, PumpLogClassifier.ClassifyPayload(PayloadFor(eventName)));

    [Fact]
    public void An_unknown_discriminator_is_unrecognised_rather_than_mis_attributed()
    {
        // Drift detection needs unmapped discriminators to stay visible. Putting
        // one in the nearest known bucket would hide the change.
        var bytes = new byte[32];
        BitConverter.TryWriteBytes(bytes, 0xDEADBEEFCAFEF00DUL);

        Assert.Equal(
            PumpEventKind.Unrecognised,
            PumpLogClassifier.ClassifyPayload(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Counts_are_accumulated_across_the_log_lines_of_one_transaction()
    {
        var logs = new[]
        {
            "Program 6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P invoke [1]",
            "Program log: Instruction: Buy",
            "Program data: " + PayloadFor("TradeEvent"),
            "Program data: " + PayloadFor("TradeEvent"),
            "Program data: " + PayloadFor("CreateEvent"),
            "Program 6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P success",
        };

        var counts = NewCounts();
        var payloads = PumpLogClassifier.Classify(logs, counts);

        Assert.Equal(3, payloads);
        Assert.Equal(2, counts[PumpEventKinds.Ordinal(PumpEventKind.Trade)]);
        Assert.Equal(1, counts[PumpEventKinds.Ordinal(PumpEventKind.Create)]);
    }

    [Fact]
    public void Lines_that_are_not_event_payloads_are_ignored()
    {
        var logs = new[]
        {
            "Program 6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P invoke [1]",
            "Program log: Instruction: Buy",
            "Program consumed 12345 of 200000 compute units",
            "Program 6EF8rrecthR5Dkzon8Nwu78hRvfCKubJ14M5uBEwF6P success",
        };

        var counts = NewCounts();

        Assert.Equal(0, PumpLogClassifier.Classify(logs, counts));
        Assert.All(counts, c => Assert.Equal(0, c));
    }

    [Fact]
    public void A_counts_buffer_that_is_too_small_is_rejected()
    {
        // A buffer smaller than the enum needs is a programming error. Writing
        // past it would corrupt neighbouring counters.
        Assert.Throws<ArgumentException>(() =>
        {
            Span<int> tooSmall = stackalloc int[2];
            PumpLogClassifier.Classify([], tooSmall);
        });
    }

    // Bounded decoding against malformed input.

    [Theory]
    [InlineData("")]
    [InlineData("!!!!not base64!!!!")]
    [InlineData("AAAA")]
    [InlineData("A")]
    public void Malformed_or_short_payloads_are_unrecognised_rather_than_throwing(string payload) =>
        Assert.Equal(PumpEventKind.Unrecognised, PumpLogClassifier.ClassifyPayload(payload));

    [Fact]
    public void An_absurdly_long_payload_is_rejected_without_allocating_for_it()
    {
        // The decoder uses a fixed stack buffer. A megabyte of base64 must be
        // refused by length, not decoded and then discarded.
        var absurd = new string('A', 4 * 1024 * 1024);

        Assert.Equal(PumpEventKind.Unrecognised, PumpLogClassifier.ClassifyPayload(absurd));
    }

    [Fact]
    public void A_payload_at_the_size_limit_is_still_handled()
    {
        // 1024 bytes is the cap. A payload just inside it must not be rejected by
        // an off-by-one.
        var bytes = new byte[1024];
        BitConverter.TryWriteBytes(bytes, PumpLogClassifier.EventDiscriminator("TradeEvent"));

        Assert.Equal(
            PumpEventKind.Trade,
            PumpLogClassifier.ClassifyPayload(Convert.ToBase64String(bytes)));
    }

    [Fact]
    public void Random_bytes_never_throw()
    {
        // Random input over the decoder's input space. A crash here could be
        // triggered by anyone able to emit a Pump log line.
        var random = new Random(20261025);

        for (var i = 0; i < 2_000; i++)
        {
            var bytes = new byte[random.Next(0, 200)];
            random.NextBytes(bytes);

            var kind = PumpLogClassifier.ClassifyPayload(Convert.ToBase64String(bytes));

            Assert.True(
                Enum.IsDefined(kind),
                $"classifier returned an undefined kind for {bytes.Length} random bytes");
        }
    }

    [Fact]
    public void Random_text_never_throws()
    {
        var random = new Random(20261026);
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/= \t\n";

        for (var i = 0; i < 2_000; i++)
        {
            var length = random.Next(0, 300);
            var text = string.Create(length, random, static (span, rng) =>
            {
                for (var j = 0; j < span.Length; j++)
                {
                    span[j] = Alphabet[rng.Next(Alphabet.Length)];
                }
            });

            Assert.True(Enum.IsDefined(PumpLogClassifier.ClassifyPayload(text)));
        }
    }

    [Fact]
    public void A_null_log_line_does_not_break_the_scan()
    {
        // The log array comes from another library, and a null element must not
        // crash the caller.
        var counts = NewCounts();

        Assert.Equal(0, PumpLogClassifier.Classify([null!, "Program log: x"], counts));
    }
}
