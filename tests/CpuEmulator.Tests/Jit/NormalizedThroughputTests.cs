using CpuEmulator.Benchmarks;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task M1: pins the normalization layer (<see cref="NormalizedThroughput.From"/>). A result
/// that reports an instruction count maps to a non-null guest-MIPS (= instructions/sec ÷ 1e6); a
/// cycle-only result (InstructionsPerSecond == 0, "not reported") maps to GuestMips == null so the
/// comparison table ranks it by cycles/sec within its CPU only. CyclesPerSecond passes through both ways.</summary>
public class NormalizedThroughputTests
{
    [Fact]
    public void From_a_result_with_instructions_yields_non_null_guest_mips()
    {
        // 12,500,000 instructions/sec over the window => 12.5 guest-MIPS; cycles/sec passes through.
        var r = AdapterResult.MeasuredWithInstructions(cycles: 90_000_000, instructions: 12_500_000, wallSeconds: 1.0, note: "x");
        var n = NormalizedThroughput.From(r);

        Assert.NotNull(n.GuestMips);
        Assert.Equal(r.InstructionsPerSecond / 1_000_000.0, n.GuestMips!.Value, 9);
        Assert.Equal(12.5, n.GuestMips!.Value, 9);
        Assert.Equal(r.CyclesPerSecond, n.CyclesPerSecond, 9);
    }

    [Fact]
    public void From_a_cycle_only_result_yields_null_guest_mips_but_passes_cycles_through()
    {
        // A measured-but-no-instructions subject (InstructionsPerSecond == 0 => "not reported").
        var r = AdapterResult.Measured(cycles: 100_000_000, wallSeconds: 2.0, note: "y");
        Assert.Equal(0, r.InstructionsPerSecond);   // pin the precondition

        var n = NormalizedThroughput.From(r);
        Assert.Null(n.GuestMips);
        Assert.Equal(r.CyclesPerSecond, n.CyclesPerSecond, 9);
        Assert.Equal(50_000_000.0, n.CyclesPerSecond, 9);
    }
}
