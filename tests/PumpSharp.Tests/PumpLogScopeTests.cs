using PumpSharp;

namespace PumpSharp.Tests;

/// <summary>
/// Attributing a log line to the program that emitted it.
///
/// A <c>logsSubscribe</c> filtered on <c>mentions</c> returns the whole
/// transaction's logs. Without attribution, every <c>Program data:</c> line
/// in a Pump-mentioning transaction would be read as a possible Pump event,
/// and on a live feed about 6,500 foreign payloads per ninety seconds
/// reached the Pump decoder, some of them not Anchor payloads at all.
///
/// This is a correctness test. A foreign line whose first eight bytes
/// matched a Pump discriminator would decode as a Pump event, with only the
/// exact-length check to stop it.
/// </summary>
public class PumpLogScopeTests
{
    private const string Pump = PumpProgramIds.Pump;
    private const string PumpSwap = PumpProgramIds.PumpSwap;
    private const string Foreign = "JUP6LkbZbjS1jKKwapdHNy74zcZ3tLUZoi5QNyVTaV4";

    /// <summary>
    /// Runs a log stream and returns which <c>Program data:</c> lines were
    /// attributed to Pump.
    /// </summary>
    private static List<string> Attributed(params string[] logs)
    {
        var scope = default(PumpLogScope);
        var attributed = new List<string>();

        foreach (var line in logs)
        {
            if (scope.Consume(line))
            {
                continue;
            }

            if (scope.InPumpProgram
                && line.StartsWith(PumpLogClassifier.ProgramDataPrefix, StringComparison.Ordinal))
            {
                attributed.Add(line);
            }
        }

        return attributed;
    }

    [Fact]
    public void A_data_line_inside_a_pump_invocation_is_attributed_to_pump()
    {
        var attributed = Attributed(
            $"Program {Pump} invoke [1]",
            "Program log: Instruction: Buy",
            "Program data: AAAA",
            $"Program {Pump} consumed 12345 of 200000 compute units",
            $"Program {Pump} success");

        Assert.Equal(["Program data: AAAA"], attributed);
    }

    [Fact]
    public void A_data_line_from_another_program_in_the_same_transaction_is_not()
    {
        // The simplest case: an aggregator's own log data inside a
        // Pump-mentioning transaction.
        var attributed = Attributed(
            $"Program {Foreign} invoke [1]",
            "Program data: U0NRVAEAAQE=",
            $"Program {Foreign} success",
            $"Program {Pump} invoke [1]",
            "Program data: AAAA",
            $"Program {Pump} success");

        Assert.Equal(["Program data: AAAA"], attributed);
    }

    [Fact]
    public void A_foreign_program_invoked_by_pump_owns_its_own_data_lines()
    {
        // Nesting breaks a naive "is Pump anywhere on the stack" check. Pump is
        // still lower on the stack, but the innermost program is not Pump, so the
        // line is not Pump's.
        var attributed = Attributed(
            $"Program {Pump} invoke [1]",
            "Program data: PUMP",
            $"Program {Foreign} invoke [2]",
            "Program data: FOREIGN",
            $"Program {Foreign} success",
            "Program data: PUMPAGAIN",
            $"Program {Pump} success");

        Assert.Equal(["Program data: PUMP", "Program data: PUMPAGAIN"], attributed);
    }

    [Fact]
    public void Pump_invoked_by_a_foreign_program_is_still_pump()
    {
        // The common routed trade: an aggregator calls Pump, and Pump's event
        // belongs to Pump.
        var attributed = Attributed(
            $"Program {Foreign} invoke [1]",
            "Program data: FOREIGN",
            $"Program {Pump} invoke [2]",
            "Program data: PUMP",
            $"Program {Pump} success",
            $"Program {Foreign} success");

        Assert.Equal(["Program data: PUMP"], attributed);
    }

    [Fact]
    public void PumpSwap_counts_as_a_pump_family_program()
    {
        var attributed = Attributed(
            $"Program {PumpSwap} invoke [1]",
            "Program data: SWAP",
            $"Program {PumpSwap} success");

        Assert.Equal(["Program data: SWAP"], attributed);
    }

    [Fact]
    public void A_failed_invocation_closes_its_scope()
    {
        var attributed = Attributed(
            $"Program {Pump} invoke [1]",
            $"Program {Foreign} invoke [2]",
            "Program data: FOREIGN",
            $"Program {Foreign} failed: custom program error: 0x1",
            "Program data: PUMPAGAIN",
            $"Program {Pump} success");

        Assert.Equal(["Program data: PUMPAGAIN"], attributed);
    }

    [Fact]
    public void A_compute_units_line_does_not_close_a_scope()
    {
        // "Program <id> consumed N of M compute units" looks like an invocation
        // line but is not one. Treating it as a close would unbalance the stack and
        // drop the transaction's remaining Pump events.
        var attributed = Attributed(
            $"Program {Pump} invoke [1]",
            $"Program {Pump} consumed 5000 of 200000 compute units",
            "Program data: AFTERCOMPUTE",
            $"Program {Pump} success");

        Assert.Equal(["Program data: AFTERCOMPUTE"], attributed);
    }

    [Fact]
    public void A_stream_with_no_invocation_markers_is_reported_rather_than_discarded()
    {
        // If a provider omits the markers, every payload would be dropped. Callers
        // fall back to reading every data line, so this state must be
        // distinguishable from "the current program is foreign".
        var scope = default(PumpLogScope);

        Assert.False(scope.Consume("Program data: AAAA"));
        Assert.True(scope.SawNoInvocation);
        Assert.False(scope.InPumpProgram);

        // Once any invocation is seen, the fallback stays off.
        scope.Consume($"Program {Foreign} invoke [1]");

        Assert.False(scope.SawNoInvocation);
        Assert.False(scope.InPumpProgram);
    }

    [Fact]
    public void An_unbalanced_stream_cannot_drive_the_depth_without_bound()
    {
        // A malformed or truncated log stream is untrusted input. It may confuse
        // attribution, but it must not make anything grow.
        var scope = default(PumpLogScope);

        for (var i = 0; i < 10_000; i++)
        {
            scope.Consume($"Program {Pump} invoke [1]");
        }

        for (var i = 0; i < 10_000; i++)
        {
            scope.Consume($"Program {Pump} success");
        }

        Assert.False(scope.InPumpProgram);
    }

    [Fact]
    public void The_magic_tagged_lines_seen_live_are_excluded()
    {
        // Copied from a live capture. Payloads starting with four-character ASCII
        // tags showed these were never Anchor payloads.
        var attributed = Attributed(
            $"Program {Foreign} invoke [1]",
            "Program data: U0NRVAEAAQE=",   // SCQT
            "Program data: UFJTTQQIAAA=",   // PRSM
            "Program data: UVJUMgHhYx4=",   // QRT2
            $"Program {Foreign} success");

        Assert.Empty(attributed);
    }
}
