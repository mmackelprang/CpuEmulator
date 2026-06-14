using System.Text.Json;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M3.4a Task 7 — the Z80 TomHarte harness self-tests (mirrors TomHarteRunnerSelfTests). Exercises
/// the loader (alt pairs / ports / null-bus cycles) and the runner (F with X/Y, ports diff, internal
/// T-states) against a small INLINE fixture — NO live vectors required. The skip-when-absent attribute
/// is proven separately.
/// </summary>
public class Z80TomHarteRunnerSelfTests
{
    // A NOP (0x00) at PC=0x1000: R increments, 4 T-states (one r-m- fetch + 3 null-bus internal).
    private const string NopCase = """
        { "name": "00 test",
          "initial": { "pc": 4096, "sp": 65520, "a": 1, "b": 2, "c": 3, "d": 4, "e": 5, "f": 0,
                       "h": 6, "l": 7, "i": 8, "r": 9, "wz": 0, "ix": 100, "iy": 200,
                       "af_": 4369, "bc_": 8738, "de_": 13107, "hl_": 17476,
                       "im": 0, "iff1": 1, "iff2": 1, "ei": 0, "p": 0, "q": 0,
                       "ram": [[4096, 0]] },
          "final":   { "pc": 4097, "sp": 65520, "a": 1, "b": 2, "c": 3, "d": 4, "e": 5, "f": 0,
                       "h": 6, "l": 7, "i": 8, "r": 10, "wz": 0, "ix": 100, "iy": 200,
                       "af_": 4369, "bc_": 8738, "de_": 13107, "hl_": 17476,
                       "im": 0, "iff1": 1, "iff2": 1, "ei": 0, "p": 0, "q": 0,
                       "ram": [[4096, 0]] },
          "cycles":  [ [4096, null, "----"], [4096, 0, "r-m-"], [4096, null, "----"], [4096, null, "----"] ] }
        """;

    [Fact]
    public void Loader_parses_alt_pairs_and_null_bus_cycles()
    {
        using var doc = JsonDocument.Parse(NopCase);
        var c = Z80TomHarteLoader.Parse(doc.RootElement);
        Assert.Equal("00 test", c.Name);
        Assert.Equal(0x2222, c.Initial.Bc_);     // packed alt pair 8738 = 0x2222
        Assert.Equal(0x1111, c.Initial.Af_);     // 4369 = 0x1111
        Assert.Equal(4, c.Cycles.Length);
        // Cycle[0] is a null-bus (internal) T-state; cycle[1] is the opcode fetch (r-m-).
        Assert.False(c.Cycles[0].HasData);
        Assert.True(c.Cycles[1].HasData);
        Assert.True(c.Cycles[1].IsMemRead);
    }

    [Fact]
    public void Runner_passes_a_NOP_with_R_refresh_and_4_T_states()
    {
        using var doc = JsonDocument.Parse(NopCase);
        var c = Z80TomHarteLoader.Parse(doc.RootElement);
        // Full gate (registers + RAM + ports + per-T-state bus trace).
        string? failure = Z80TomHarteRunner.RunCase(c, registersOnly: false);
        Assert.Null(failure);
    }

    [Fact]
    public void Runner_sets_F_with_XY_bits()
    {
        // SCF (0x37): X/Y come from A. With A=0x28 (bits 5+3 set), F must end with Y(0x20)+X(0x08)+C(0x01).
        const string scf = """
            { "name": "37 xy",
              "initial": { "pc": 0, "sp": 0, "a": 40, "b": 0, "c": 0, "d": 0, "e": 0, "f": 0,
                           "h": 0, "l": 0, "i": 0, "r": 0, "wz": 0, "ix": 0, "iy": 0,
                           "af_": 0, "bc_": 0, "de_": 0, "hl_": 0,
                           "im": 0, "iff1": 0, "iff2": 0, "ei": 0, "p": 0, "q": 0,
                           "ram": [[0, 55]] },
              "final":   { "pc": 1, "sp": 0, "a": 40, "b": 0, "c": 0, "d": 0, "e": 0, "f": 41,
                           "h": 0, "l": 0, "i": 0, "r": 1, "wz": 0, "ix": 0, "iy": 0,
                           "af_": 0, "bc_": 0, "de_": 0, "hl_": 0,
                           "im": 0, "iff1": 0, "iff2": 0, "ei": 0, "p": 0, "q": 41,
                           "ram": [[0, 55]] },
              "cycles":  [ [0, null, "----"], [0, 55, "r-m-"], [0, null, "----"], [0, null, "----"] ] }
            """;
        using var doc = JsonDocument.Parse(scf);
        var c = Z80TomHarteLoader.Parse(doc.RootElement);
        Assert.Equal(0x29, c.Final.F);   // Y(0x20)+X(0x08)+C(0x01) = 0x29
        // Run registers-only (the SCF body's null-bus order is the same; full also passes).
        Assert.Null(Z80TomHarteRunner.RunCase(c, registersOnly: true));
    }

    [Fact]
    public void Runner_diffs_ports_for_OUT()
    {
        // OUT (n),A (0xD3 nn): writes A to the I/O port (A<<8)|nn. A=0x12, nn=0x34 → port 0x1234, val 0x12.
        const string outCase = """
            { "name": "d3 out",
              "initial": { "pc": 0, "sp": 0, "a": 18, "b": 0, "c": 0, "d": 0, "e": 0, "f": 0,
                           "h": 0, "l": 0, "i": 0, "r": 0, "wz": 0, "ix": 0, "iy": 0,
                           "af_": 0, "bc_": 0, "de_": 0, "hl_": 0,
                           "im": 0, "iff1": 0, "iff2": 0, "ei": 0, "p": 0, "q": 0,
                           "ram": [[0, 211], [1, 52]] },
              "final":   { "pc": 2, "sp": 0, "a": 18, "b": 0, "c": 0, "d": 0, "e": 0, "f": 0,
                           "h": 0, "l": 0, "i": 0, "r": 1, "wz": 4661, "ix": 0, "iy": 0,
                           "af_": 0, "bc_": 0, "de_": 0, "hl_": 0,
                           "im": 0, "iff1": 0, "iff2": 0, "ei": 0, "p": 0, "q": 0,
                           "ram": [[0, 211], [1, 52]] },
              "cycles":  [ [0, null, "----"], [0, 211, "r-m-"], [0, null, "----"], [0, null, "----"],
                           [1, 52, "r-m-"], [1, null, "----"], [1, null, "----"],
                           [4660, 18, "----"], [4660, 18, "-w-i"], [4660, 18, "----"], [4660, 18, "----"] ],
              "ports":   [ [4660, 18, "w"] ] }
            """;
        using var doc = JsonDocument.Parse(outCase);
        var c = Z80TomHarteLoader.Parse(doc.RootElement);
        Assert.Single(c.Ports);
        Assert.Equal(0x1234, c.Ports[0].Address);
        Assert.False(c.Ports[0].IsRead);
        // Registers-only gate (ports + cycle count); the bus-trace order of the (n) read is exercised.
        Assert.Null(Z80TomHarteRunner.RunCase(c, registersOnly: true));
    }

    [Fact]
    public void Runner_reports_a_wrong_final_WZ_and_Q_and_IM_universally()
    {
        // M3.4c (Piece A): the universal Q/WZ/IM check. A NOP leaves WZ/Q/IM as the initial state; here
        // the final state DELIBERATELY mismatches each, so the runner must report all three (proving the
        // checkInternal scoping is retired and the check fires on EVERY case, not just the CB plane).
        const string badCase = """
            { "name": "00 bad-internal",
              "initial": { "pc": 0, "sp": 0, "a": 0, "b": 0, "c": 0, "d": 0, "e": 0, "f": 0,
                           "h": 0, "l": 0, "i": 0, "r": 0, "wz": 4660, "ix": 0, "iy": 0,
                           "af_": 0, "bc_": 0, "de_": 0, "hl_": 0,
                           "im": 1, "iff1": 0, "iff2": 0, "ei": 0, "p": 0, "q": 0,
                           "ram": [[0, 0]] },
              "final":   { "pc": 1, "sp": 0, "a": 0, "b": 0, "c": 0, "d": 0, "e": 0, "f": 0,
                           "h": 0, "l": 0, "i": 0, "r": 1, "wz": 9999, "ix": 0, "iy": 0,
                           "af_": 0, "bc_": 0, "de_": 0, "hl_": 0,
                           "im": 2, "iff1": 0, "iff2": 0, "ei": 0, "p": 0, "q": 255,
                           "ram": [[0, 0]] },
              "cycles":  [ [0, null, "----"], [0, 0, "r-m-"], [0, null, "----"], [0, null, "----"] ] }
            """;
        using var doc = JsonDocument.Parse(badCase);
        var c = Z80TomHarteLoader.Parse(doc.RootElement);
        string? failure = Z80TomHarteRunner.RunCase(c, registersOnly: true);
        Assert.NotNull(failure);
        Assert.Contains("WZ:", failure);   // NOP keeps WZ = 4660, expected 9999
        Assert.Contains("Q:", failure);    // NOP sets Q = 0, expected 255
        Assert.Contains("IM:", failure);   // NOP keeps IM = 1, expected 2
    }

    [Fact]
    public void Skip_attribute_skips_when_vectors_absent()
    {
        // When the z80/v1 dir is missing, the attribute records a Skip reason (the skip-when-absent
        // discipline). When present, no skip — either way the attribute constructs without throwing.
        var attr = new Z80TomHarteTheoryAttribute();
        bool present = Z80TomHarteVectors.TryGetVectorDirectory() is not null;
        if (present)
            Assert.Null(attr.Skip);
        else
            Assert.False(string.IsNullOrEmpty(attr.Skip));
    }
}
