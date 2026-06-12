using CpuEmulator.Tests.Mos6502;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Vector-free self-tests for TomHarteRunner and TomHarteLoader.
/// These run even without the SingleStepTests vectors — they pin the differ
/// using synthetic cases built inline.
///
/// Synthetic case: LDA #$42 (opcode A9 42) at 0x0200.
///   Initial: PC=0x0200, A=0x00, all others=0; RAM: [0x0200]=0xA9, [0x0201]=0x42
///   Final  : PC=0x0202, A=0x42, N=0 Z=0; same RAM
///   Cycles : [0x0200, 0xA9, read], [0x0201, 0x42, read]
/// </summary>
public class TomHarteRunnerSelfTests
{
    // ─── synthetic case builder ───────────────────────────────────────────

    /// <summary>Builds the canonical LDA #$42 case with correct final state + 2-cycle trace.</summary>
    private static TomHarteCase LdaImmediateCase(
        ushort? overrideFinalA    = null,
        ushort? overrideFinalPc   = null,
        byte?   overrideFinalP    = null,
        TomHarteCycle[]? overrideCycles = null)
    {
        // Initial: all regs zero, opcode + operand in RAM
        var initial = new TomHarteState(
            Pc: 0x0200, S: 0xFD, A: 0x00, X: 0x00, Y: 0x00, P: 0x00,
            Ram: [new(0x0200, 0xA9), new(0x0201, 0x42)]);

        // Final: PC advanced 2, A=0x42, P has N=0 Z=0 (0x42 non-zero)
        var final = new TomHarteState(
            Pc: overrideFinalPc ?? 0x0202,
            S: 0xFD,
            A: (byte)(overrideFinalA ?? 0x42),
            X: 0x00, Y: 0x00,
            P: overrideFinalP ?? 0x00,
            Ram: [new(0x0200, 0xA9), new(0x0201, 0x42)]);

        var cycles = overrideCycles ??
        [
            new TomHarteCycle(0x0200, 0xA9, IsRead: true),
            new TomHarteCycle(0x0201, 0x42, IsRead: true),
        ];

        return new TomHarteCase("a9 42", initial, final, cycles);
    }

    // ─── pass case ───────────────────────────────────────────────────────

    [Fact]
    public void Correct_LDA_Immediate_passes()
    {
        var testCase = LdaImmediateCase();
        string? result = TomHarteRunner.RunCase(testCase);
        Assert.Null(result);
    }

    // ─── register mismatch ───────────────────────────────────────────────

    [Fact]
    public void Tampered_final_A_reports_register_mismatch()
    {
        // Expect A=0x99 but emulator produces 0x42 — report must say so
        var testCase = LdaImmediateCase(overrideFinalA: 0x99);
        string? result = TomHarteRunner.RunCase(testCase);
        Assert.NotNull(result);
        Assert.Contains("A: expected", result);
    }

    // ─── cycle trace divergence ──────────────────────────────────────────

    [Fact]
    public void Tampered_expected_cycle_address_reports_bus_trace_diverges()
    {
        // The expected cycle 2 has wrong address — report must mention "bus trace diverges at cycle 2"
        // and render the side-by-side table.
        var wrongCycles = new TomHarteCycle[]
        {
            new(0x0200, 0xA9, IsRead: true),
            new(0x9999, 0x42, IsRead: true), // wrong address
        };
        var testCase = LdaImmediateCase(overrideCycles: wrongCycles);
        string? result = TomHarteRunner.RunCase(testCase);
        Assert.NotNull(result);
        Assert.Contains("bus trace diverges at cycle 2", result);
        // Table should render both columns
        Assert.Contains("expected", result);
        Assert.Contains("actual", result);
    }

    // ─── cycle count mismatch ─────────────────────────────────────────────

    [Fact]
    public void Tampered_expected_cycle_count_reports_count_mismatch()
    {
        // 3 expected cycles but instruction takes 2 — report must say "cycle count"
        var extraCycles = new TomHarteCycle[]
        {
            new(0x0200, 0xA9, IsRead: true),
            new(0x0201, 0x42, IsRead: true),
            new(0x0202, 0x00, IsRead: true), // ghost third cycle
        };
        var testCase = LdaImmediateCase(overrideCycles: extraCycles);
        string? result = TomHarteRunner.RunCase(testCase);
        Assert.NotNull(result);
        Assert.Contains("cycle count", result);
    }

    // ─── disassembly in failure output ──────────────────────────────────

    [Fact]
    public void Failure_report_contains_disassembled_instruction()
    {
        // Any failure should include the disassembly of the opcode (LDA #$42 → "LDA #$42")
        var testCase = LdaImmediateCase(overrideFinalA: 0x99);
        string? result = TomHarteRunner.RunCase(testCase);
        Assert.NotNull(result);
        Assert.Contains("LDA", result);
    }

    // ─── loader round-trip (self-contained JSON) ─────────────────────────

    [Fact]
    public void Loader_parses_minimal_JSON_correctly()
    {
        // A minimal single-case JSON that matches the real format
        const string json = """
            [
              { "name": "a9 42",
                "initial": { "pc": 512, "s": 253, "a": 0, "x": 0, "y": 0, "p": 0,
                             "ram": [ [512, 169], [513, 66] ] },
                "final":   { "pc": 514, "s": 253, "a": 66, "x": 0, "y": 0, "p": 0,
                             "ram": [ [512, 169], [513, 66] ] },
                "cycles":  [ [512, 169, "read"], [513, 66, "read"] ] }
            ]
            """;

        using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        // Write to temp file (TomHarteLoader.LoadFile takes a path)
        string tmp = System.IO.Path.GetTempFileName();
        try
        {
            System.IO.File.WriteAllText(tmp, json);
            var cases = TomHarteLoader.LoadFile(tmp);
            Assert.Single(cases);
            var c = cases[0];
            Assert.Equal("a9 42", c.Name);
            Assert.Equal(0x0200, c.Initial.Pc);
            Assert.Equal(0x42, c.Final.A);
            Assert.Equal(2, c.Cycles.Length);
            Assert.True(c.Cycles[0].IsRead);
            Assert.Equal(0x0200u, c.Cycles[0].Address);
            Assert.Equal(0xA9, c.Cycles[0].Value);
        }
        finally
        {
            System.IO.File.Delete(tmp);
        }
    }
}
