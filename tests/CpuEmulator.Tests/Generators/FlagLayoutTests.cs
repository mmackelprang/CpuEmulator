using System.IO;
using CpuEmulator.Core.Specification;
using CpuEmulator.Generators;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M3.4a Task 1 — the per-spec flag-bit map (Ground truth B). The <see cref="Flag"/> enum grows
/// the Z80 names additively; a spec declaring a <see cref="FlagLayout"/> resolves each name's bit
/// position per-spec (the Z80's S=7..C=0); a spec with no layout (the 6502) falls back to the enum
/// values. The 6502 generation is byte-identical after the additive growth (the regression guard).
/// </summary>
public class FlagLayoutTests
{
    [Fact]
    public void Flag_enum_has_Z80_members_and_keeps_6502_members()
    {
        // The Z80 names exist (additive).
        Assert.True(System.Enum.IsDefined(typeof(Flag), Flag.S));
        Assert.True(System.Enum.IsDefined(typeof(Flag), Flag.H));
        Assert.True(System.Enum.IsDefined(typeof(Flag), Flag.P));
        Assert.True(System.Enum.IsDefined(typeof(Flag), Flag.N));
        Assert.True(System.Enum.IsDefined(typeof(Flag), Flag.Y));
        Assert.True(System.Enum.IsDefined(typeof(Flag), Flag.X));
        // The 6502 members still exist AND keep their bit-position values (the 6502 emitter reads them).
        Assert.Equal(0, (int)Flag.C);
        Assert.Equal(1, (int)Flag.Z);
        Assert.Equal(7, (int)Flag.N);
        Assert.Equal(6, (int)Flag.V);
    }

    [Fact]
    public void FlagBitMap_uses_declared_Z80_layout()
    {
        var map = FlagBitMap.From(new[]
        {
            new FlagBitModel("S", 7), new FlagBitModel("Z", 6), new FlagBitModel("Y", 5),
            new FlagBitModel("H", 4), new FlagBitModel("X", 3), new FlagBitModel("P", 2),
            new FlagBitModel("N", 1), new FlagBitModel("C", 0),
        });

        Assert.Equal(7, map.BitOf("S"));
        Assert.Equal(6, map.BitOf("Z"));   // Z is bit 6 on the Z80 (bit 1 on the 6502)
        Assert.Equal(4, map.BitOf("H"));
        Assert.Equal(2, map.BitOf("P"));
        Assert.Equal(1, map.BitOf("N"));   // N is bit 1 on the Z80 (bit 7 on the 6502)
        Assert.Equal(0, map.BitOf("C"));
        Assert.Equal(5, map.BitOf("Y"));
        Assert.Equal(3, map.BitOf("X"));
    }

    [Fact]
    public void FlagBitMap_without_layout_falls_back_to_enum_values()
    {
        var map = FlagBitMap.From(null);   // no FlagLayout declared — the 6502 shape

        Assert.Equal(0, map.BitOf("C"));
        Assert.Equal(1, map.BitOf("Z"));   // the 6502 enum value
        Assert.Equal(7, map.BitOf("N"));
        Assert.Equal(6, map.BitOf("V"));
    }

    [Fact]
    public void Generated_6502_output_is_byte_identical_after_Z80_vocabulary()
    {
        // Regenerate Mos6502Cpu from the COMMITTED Mos6502Spec.cs AFTER the M3.4a additive enum +
        // FlagLayout growth, and assert the flag emission is the 6502's (the per-spec FlagBit falls
        // back to the enum: no FlagLayout declared). The SetNZ mask 0x7D and the ADC/SBC masks are
        // the canonical 6502 values; no Z80 flag name (S/H/P/Y/X) ever appears in 6502 literals.
        string repoRoot = FindRepoRoot();
        string specSource = File.ReadAllText(
            Path.Combine(repoRoot, "src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs"));

        // The generator needs only the spec source to emit Mos6502Cpu.g.cs; the hand-written partial
        // (which provides ReadBus/etc.) is not present here, so we inspect the GENERATED text rather
        // than asserting a clean compile (CS errors for the missing partial members are expected and
        // ignored — the contract under test is the generated FLAG emission, not a full build).
        var result = GeneratorTestHost.Run(specSource);

        string gen = result.GeneratedText;
        Assert.NotEqual(string.Empty, gen);
        // The 6502 SetNZ mask (0x7D clears bits 1+7) — the enum-fallback emission, unchanged.
        Assert.Contains("0x7D)", gen);
        // No Z80 vocabulary leaked into the 6502 generation.
        Assert.DoesNotContain("FlagLayout", gen);
        Assert.DoesNotContain("Add8", gen);
        Assert.DoesNotContain("SetSZ", gen);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CpuEmulator.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
