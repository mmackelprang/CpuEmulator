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
    // Tests locate them via DataPath.Get() under AppContext.BaseDirectory/data/.
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
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
        Assert.Contains("Duplicate opcode", ex.Message);
    }

    [Fact]
    public void Rejects_Unknown_Mode()
    {
        var json = """
            [
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "SuperMode", "bytes": 2, "cycles": 2, "pageCrossPenalty": false }
            ]
            """;
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
        Assert.Contains("Unknown mode", ex.Message);
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
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
        Assert.Contains("Byte count mismatch", ex.Message);
    }

    [Fact]
    public void Rejects_Missing_Required_Field()
    {
        // No "opcode" field — must fail loudly, not deserialize to null and
        // sail through validation (curated hand-edited file; nulls must not
        // reach the Task 4 emitter).
        var json = """
            [
              { "mnemonic": "LDA", "mode": "Immediate", "bytes": 2, "cycles": 2, "pageCrossPenalty": false }
            ]
            """;
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
        Assert.Contains("opcode", ex.Message);
    }

    [Fact]
    public void Rejects_Unknown_Json_Member()
    {
        // A typo'd key ("pageCrossPenality") must be rejected, not silently
        // ignored leaving the real field at its default.
        var json = """
            [
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "Immediate", "bytes": 2, "cycles": 2, "pageCrossPenality": true }
            ]
            """;
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
        Assert.Contains("pageCrossPenality", ex.Message);
    }

    [Fact]
    public void Rejects_Invalid_Opcode_Format()
    {
        var json = """
            [
              { "opcode": "0xZZ", "mnemonic": "LDA", "mode": "Immediate", "bytes": 2, "cycles": 2, "pageCrossPenalty": false }
            ]
            """;
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
        Assert.Contains("format", ex.Message);
    }

    [Fact]
    public void Rejects_Empty_Array()
    {
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse("[]"));
        Assert.Contains("empty", ex.Message);
    }

    // ─── M3.1b: a computed-length row is NOT forbidden (Task 6) ──────────────

    [Fact]
    public void Existing_6502_rows_still_validate_byte_for_byte()
    {
        // The headline invariant: the 6502 dataset rules are UNCHANGED. All 151 rows load with the
        // same byte counts, zero byte-count errors. (Regression guard for the relaxation.)
        var entries = OpcodeDataset.Load(DatasetPath);

        Assert.Equal(151, entries.Length);
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
            Assert.Equal(expected, entry.Bytes);
        }
    }

    [Fact]
    public void A_computed_length_mode_is_not_forbidden()
    {
        // A ModR/M-tagged row (length computed by the decode walk from a mid-stream byte) carries a
        // BASE byte count and is accepted WITHOUT the fixed-length byte-count equality. This only
        // stops the schema from FORBIDDING the row — it does not compute the real tail (M3.3+).
        var json = """
            [
              { "opcode": "0x80", "mnemonic": "GRP", "mode": "ModRm", "bytes": 2, "cycles": 2, "pageCrossPenalty": false }
            ]
            """;
        var entries = OpcodeDataset.Parse(json);

        var row = Assert.Single(entries);
        Assert.Equal("ModRm", row.Mode);
        Assert.Equal(2, row.Bytes);   // the declared base; no equality enforced
    }

    [Fact]
    public void A_bare_unknown_mode_still_throws()
    {
        // A genuinely unknown mode (not a recognized computed-length marker) still throws — the
        // vocabulary gate is preserved; the relaxation is narrow.
        var json = """
            [
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "SuperMode", "bytes": 2, "cycles": 2, "pageCrossPenalty": false }
            ]
            """;
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
        Assert.Contains("Unknown mode", ex.Message);
    }

    [Fact]
    public void Rejects_Malformed_Json()
    {
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse("not json"));
        Assert.Contains("malformed", ex.Message);
    }

    // ─── provenance (source) field ───────────────────────────────────────

    [Fact]
    public void Source_Field_Roundtrips_When_Present()
    {
        var json = """
            [
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "Immediate", "bytes": 2, "cycles": 2, "pageCrossPenalty": false, "source": "datasheet p.1" }
            ]
            """;
        var entries = OpcodeDataset.Parse(json);
        Assert.Single(entries);
        Assert.Equal("datasheet p.1", entries[0].Source);
    }

    [Fact]
    public void Source_Field_Is_Null_When_Absent()
    {
        // Back-compat: rows without "source" (including the vendored 151-row file) must load with Source == null.
        var json = """
            [
              { "opcode": "0xA9", "mnemonic": "LDA", "mode": "Immediate", "bytes": 2, "cycles": 2, "pageCrossPenalty": false }
            ]
            """;
        var entries = OpcodeDataset.Parse(json);
        Assert.Single(entries);
        Assert.Null(entries[0].Source);
    }

    // ─── M3.3 Task 1: the Z80 prefix-keyed schema (prefix/subfield/Key) ──────

    [Fact]
    public void Base_plane_row_has_null_prefix_and_Key_equals_Opcode()
    {
        // A base-plane row (no prefix — the 6502/base shape) loads with Prefix == null,
        // SubField == null, Key == Opcode (byte-identical to a 6502 row).
        var json = """
            [
              { "opcode": "0xB0", "mnemonic": "OR", "mode": "Register", "bytes": 1, "cycles": 4, "pageCrossPenalty": false }
            ]
            """;
        var entries = OpcodeDataset.Parse(json);
        var row = Assert.Single(entries);
        Assert.Null(row.Prefix);
        Assert.Null(row.SubField);
        Assert.Equal("0xB0", row.Key);
    }

    [Fact]
    public void Prefixed_row_carries_prefix_and_plane_qualified_Key()
    {
        // An ED-plane row (LDIR) loads with Prefix == "0xED" and Key == "0xED:0xB0".
        var json = """
            [
              { "prefix": "0xED", "opcode": "0xB0", "mnemonic": "LDIR", "mode": "Implied", "bytes": 2, "cycles": 21, "pageCrossPenalty": false }
            ]
            """;
        var entries = OpcodeDataset.Parse(json);
        var row = Assert.Single(entries);
        Assert.Equal("0xED", row.Prefix);
        Assert.Equal("0xED:0xB0", row.Key);
    }

    [Fact]
    public void Compound_prefix_token_is_accepted()
    {
        // The DDCB compound form (DD CB dd op) — prefix token "0xDDCB", Key "0xDDCB:0x06".
        var json = """
            [
              { "prefix": "0xDDCB", "opcode": "0x06", "mnemonic": "RLC", "mode": "Indexed", "bytes": 4, "cycles": 23, "pageCrossPenalty": false }
            ]
            """;
        var entries = OpcodeDataset.Parse(json);
        var row = Assert.Single(entries);
        Assert.Equal("0xDDCB", row.Prefix);
        Assert.Equal("0xDDCB:0x06", row.Key);
    }

    [Fact]
    public void Prefix_must_be_a_recognized_token()
    {
        // The prefix vocabulary gate: only 0xCB/0xED/0xDD/0xFD/0xDDCB/0xFDCB are accepted.
        var json = """
            [
              { "prefix": "0xZZ", "opcode": "0x06", "mnemonic": "RLC", "mode": "Indexed", "bytes": 2, "cycles": 8, "pageCrossPenalty": false }
            ]
            """;
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
        Assert.Contains("prefix", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void All_6502_rows_have_null_prefix()
    {
        // The regression guard: the real 6502 dataset loads unchanged, every row Prefix == null.
        var entries = OpcodeDataset.Load(DatasetPath);
        Assert.Equal(151, entries.Length);
        foreach (var entry in entries)
        {
            Assert.Null(entry.Prefix);
            Assert.Null(entry.SubField);
            Assert.Equal(entry.Opcode, entry.Key);
        }
    }

    // ─── M3.3 Task 2: prefix-key uniqueness + Z80 mode/byte vocabulary ──────

    [Fact]
    public void ED_B0_and_base_B0_coexist()
    {
        // The headline non-collision: ED B0 (LDIR) and base B0 (OR B) are DISTINCT plane-qualified
        // keys — both load without a duplicate error.
        var json = """
            [
              { "prefix": "0xED", "opcode": "0xB0", "mnemonic": "LDIR", "mode": "Implied", "bytes": 2, "cycles": 21, "pageCrossPenalty": false },
              { "opcode": "0xB0", "mnemonic": "OR", "mode": "Register", "bytes": 1, "cycles": 4, "pageCrossPenalty": false }
            ]
            """;
        var entries = OpcodeDataset.Parse(json);
        Assert.Equal(2, entries.Length);
        Assert.Contains(entries, e => e.Key == "0xED:0xB0");
        Assert.Contains(entries, e => e.Key == "0xB0");
    }

    [Fact]
    public void Duplicate_plane_qualified_key_still_throws()
    {
        // Uniqueness is on the Key, not the bare Opcode: two ED B0 rows collide.
        var json = """
            [
              { "prefix": "0xED", "opcode": "0xB0", "mnemonic": "LDIR", "mode": "Implied", "bytes": 2, "cycles": 21, "pageCrossPenalty": false },
              { "prefix": "0xED", "opcode": "0xB0", "mnemonic": "LDDR", "mode": "Implied", "bytes": 2, "cycles": 21, "pageCrossPenalty": false }
            ]
            """;
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(json));
        Assert.Contains("Duplicate opcode 0xED:0xB0", ex.Message);
    }

    [Theory]
    [InlineData("Register")]
    [InlineData("RegisterIndirect")]
    [InlineData("Indexed")]
    [InlineData("ImmediateExtended")]
    [InlineData("ExtendedAddress")]
    [InlineData("IoPort")]
    [InlineData("RelativeJump")]
    [InlineData("Bit")]
    public void Z80_modes_are_accepted(string mode)
    {
        // Each Z80 mode loads without an "unknown mode" error. Bytes chosen to satisfy the mode rule.
        int bytes = mode switch
        {
            "Register" or "RegisterIndirect" => 1,
            "RelativeJump" or "IoPort" or "Bit" => 2,
            "ImmediateExtended" or "ExtendedAddress" => 3,
            "Indexed" => 3,   // computed-length seam — any declared count accepted
            _ => 1,
        };
        // The Bit mode reaches base=1; it is normally CB-prefixed (+1) — give it the CB prefix to make 2.
        string prefixField = mode == "Bit" ? "\"prefix\": \"0xCB\", " : "";
        var json = $$"""
            [
              { {{prefixField}}"opcode": "0x40", "mnemonic": "XX", "mode": "{{mode}}", "bytes": {{bytes}}, "cycles": 4, "pageCrossPenalty": false }
            ]
            """;
        var entries = OpcodeDataset.Parse(json);
        Assert.Single(entries);
        Assert.Equal(mode, entries[0].Mode);
    }

    [Fact]
    public void Z80_mode_byte_rules_enforced()
    {
        // ImmediateExtended (base 3) on a 2-byte row throws.
        var bad = """
            [
              { "opcode": "0x21", "mnemonic": "LD", "mode": "ImmediateExtended", "bytes": 2, "cycles": 10, "pageCrossPenalty": false }
            ]
            """;
        var ex = Assert.Throws<InvalidDataException>(() => OpcodeDataset.Parse(bad));
        Assert.Contains("Byte count mismatch", ex.Message);

        // Register (base 1) on a correct 1-byte row loads fine.
        var good = """
            [
              { "opcode": "0xB0", "mnemonic": "OR", "mode": "Register", "bytes": 1, "cycles": 4, "pageCrossPenalty": false }
            ]
            """;
        Assert.Single(OpcodeDataset.Parse(good));
    }

    [Fact]
    public void DDCB_row_uses_computed_length_marker()
    {
        // A DDCB compound row (4 bytes: DD CB dd op) is ACCEPTED via the Indexed computed-length seam —
        // the byte-count equality is skipped; the declared bytes carries the truth.
        var json = """
            [
              { "prefix": "0xDDCB", "opcode": "0x06", "mnemonic": "RLC", "mode": "Indexed", "bytes": 4, "cycles": 23, "pageCrossPenalty": false }
            ]
            """;
        var entries = OpcodeDataset.Parse(json);
        var row = Assert.Single(entries);
        Assert.Equal(4, row.Bytes);
        Assert.Equal("0xDDCB:0x06", row.Key);
    }

    [Fact]
    public void PageCrossPenalty_true_is_accepted_not_forced_false_for_Z80()
    {
        // Ground truth B judgement call: the loader ACCEPTS pageCrossPenalty rather than asserting it
        // false for Z80 rows (the field is 6502-shaped; forcing-false is a Z80-policy assertion the
        // loader does not bake). The Z80 dataset itself sets it false everywhere; the loader does not enforce.
        var json = """
            [
              { "prefix": "0xED", "opcode": "0xB0", "mnemonic": "LDIR", "mode": "Implied", "bytes": 2, "cycles": 21, "pageCrossPenalty": true }
            ]
            """;
        var entries = OpcodeDataset.Parse(json);
        Assert.True(entries[0].PageCrossPenalty);
    }
}
