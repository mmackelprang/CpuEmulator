using System.IO;
using CpuEmulator.SpecImporter;

namespace CpuEmulator.Tests.Importer;

/// <summary>
/// Tests for the vendored 6502 opcode dataset and its validating loader.
/// The dataset covers all 151 documented MOS 6502 opcodes (no illegal/undocumented).
/// </summary>
public class OpcodeDatasetTests
{
    // Strategy: data files are content-copied to the output directory.
    // Tests locate them via DataPath.Get(), which walks from AppContext.BaseDirectory.
    private static string DatasetPath => DataPath.Get("mos6502-opcodes.json");

    // ─── count + uniqueness ──────────────────────────────────────────────

    [Fact]
    public void Loads_Exactly_151_Entries()
    {
        var entries = OpcodeDataset.Load(DatasetPath);
        Assert.Equal(151, entries.Length);
    }

    [Fact]
    public void All_Opcodes_Are_Unique()
    {
        var entries = OpcodeDataset.Load(DatasetPath);
        var distinct = entries.Select(e => e.Opcode).Distinct().Count();
        Assert.Equal(entries.Length, distinct);
    }

    // ─── spot rows ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("0xA9", "LDA", "Immediate",  2, 2, false)]   // canonical load-immediate
    [InlineData("0xBD", "LDA", "AbsoluteX",  3, 4, true)]    // page-cross penalty present
    [InlineData("0x4C", "JMP", "Absolute",   3, 3, false)]   // JMP absolute
    [InlineData("0xEA", "NOP", "Implied",    1, 2, false)]   // NOP
    [InlineData("0x00", "BRK", "Implied",    1, 7, false)]   // BRK
    [InlineData("0x6C", "JMP", "Indirect",   3, 5, false)]   // JMP Indirect — 5 cycles (not 6)
    public void SpotRow(string opcode, string mnemonic, string mode, int bytes, int cycles, bool pageCross)
    {
        var entries = OpcodeDataset.Load(DatasetPath);
        var row = Assert.Single(entries, e => e.Opcode.Equals(opcode, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(mnemonic, row.Mnemonic);
        Assert.Equal(mode, row.Mode);
        Assert.Equal(bytes, row.Bytes);
        Assert.Equal(cycles, row.Cycles);
        Assert.Equal(pageCross, row.PageCrossPenalty);
    }

    // ─── mode vocabulary ─────────────────────────────────────────────────

    private static readonly HashSet<string> ValidModes =
    [
        "Implied", "Accumulator", "Immediate",
        "ZeroPage", "ZeroPageX", "ZeroPageY",
        "Absolute", "AbsoluteX", "AbsoluteY",
        "IndirectX", "IndirectY", "Indirect", "Relative"
    ];

    [Fact]
    public void All_Mode_Strings_Are_In_Vocabulary()
    {
        var entries = OpcodeDataset.Load(DatasetPath);
        foreach (var entry in entries)
            Assert.Contains(entry.Mode, ValidModes);
    }

    // ─── byte-count consistency ──────────────────────────────────────────
    // Implied/Accumulator = 1 byte (opcode only)
    // Immediate/ZeroPage*/IndirectX/IndirectY/Relative = 2 bytes
    // Absolute*/Indirect = 3 bytes

    [Fact]
    public void Byte_Count_Consistent_With_Mode()
    {
        var entries = OpcodeDataset.Load(DatasetPath);
        foreach (var entry in entries)
        {
            var expected = entry.Mode switch
            {
                "Implied" or "Accumulator" => 1,
                "Immediate" or "ZeroPage" or "ZeroPageX" or "ZeroPageY"
                    or "IndirectX" or "IndirectY" or "Relative" => 2,
                "Absolute" or "AbsoluteX" or "AbsoluteY" or "Indirect" => 3,
                _ => throw new InvalidOperationException($"Unknown mode: {entry.Mode}")
            };
            Assert.True(entry.Bytes == expected,
                $"{entry.Opcode} {entry.Mnemonic} {entry.Mode}: expected {expected} bytes, got {entry.Bytes}");
        }
    }

    // ─── validation rejection tests ─────────────────────────────────────

    [Fact]
    public void Rejects_Duplicate_Opcode()
    {
        // Two entries with the same opcode value
        var json = """
            [
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "Immediate", "bytes": 2, "cycles": 2, "pageCrossPenalty": false },
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "ZeroPage",  "bytes": 2, "cycles": 3, "pageCrossPenalty": false }
            ]
            """;
        Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
    }

    [Fact]
    public void Rejects_Unknown_Mode()
    {
        var json = """
            [
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "SuperMode", "bytes": 2, "cycles": 2, "pageCrossPenalty": false }
            ]
            """;
        Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
    }

    [Fact]
    public void Rejects_Wrong_Byte_Count()
    {
        // Immediate should be 2 bytes, not 3
        var json = """
            [
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "Immediate", "bytes": 3, "cycles": 2, "pageCrossPenalty": false }
            ]
            """;
        Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
    }
}
