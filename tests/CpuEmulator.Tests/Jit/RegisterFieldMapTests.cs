using System.Collections.Generic;
using System.Reflection;
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M3.1a Task 4 (J2): the JIT's six baked FieldInfos + the RegField index switch are
/// replaced by a per-compile name→FieldInfo map resolved from the CPU's declared register names.
/// These pins prove the map resolves the 6502 register set BY NAME and throws clearly on a name
/// the CPU type does not declare. The end-to-end behavioral proof (the field map works through a
/// compiled block) is the kept JIT parity battery — Compiled_TAX_block_matches_the_interpreter
/// here is a focused spot pin of that.</summary>
public class RegisterFieldMapTests
{
    private static AddressSpace NewRamSpace()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    private static BlockCompiler NewCompiler(AddressSpace space)
    {
        var opts = new JitOptions();
        var inner = new Mos6502Cpu(space);
        return new BlockCompiler(inner, space, new Fastmem(space, opts), opts);
    }

    private static IReadOnlyDictionary<string, FieldInfo> RegFieldsOf(BlockCompiler bc) =>
        (IReadOnlyDictionary<string, FieldInfo>)typeof(BlockCompiler)
            .GetField("_regFields", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(bc)!;

    private static FieldInfo InvokeRegField(BlockCompiler bc, string name)
    {
        var m = typeof(BlockCompiler).GetMethod("RegField",
            BindingFlags.NonPublic | BindingFlags.Instance, [typeof(string)])!;
        try
        {
            return (FieldInfo)m.Invoke(bc, [name])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;   // surface the real EmulationException to the test
        }
    }

    [Fact]
    public void FieldInfo_map_resolves_the_6502_registers_by_name()
    {
        var bc = NewCompiler(NewRamSpace());
        var map = RegFieldsOf(bc);

        // Every declared 6502 register name resolves to the right field on the CPU type.
        foreach (string name in new[] { "A", "X", "Y", "S", "P", "PC" })
        {
            Assert.True(map.ContainsKey(name), $"map is missing register '{name}'");
            Assert.Equal(typeof(Mos6502Cpu).GetField(name), map[name]);
            Assert.Equal(typeof(Mos6502Cpu).GetField(name), InvokeRegField(bc, name));
        }
    }

    [Fact]
    public void RegField_throws_on_an_undeclared_register_name()
    {
        var bc = NewCompiler(NewRamSpace());
        var ex = Assert.Throws<EmulationException>(() => InvokeRegField(bc, "ZZ"));
        Assert.Contains("ZZ", ex.Message);
        Assert.Contains("does not declare", ex.Message);
    }

    [Fact]
    public void Compiled_TAX_block_matches_the_interpreter()
    {
        // LDA #$42; TAX; (then a JMP self to end the block). The field map must resolve A and X by
        // name for the Load + Transfer arms — a spot pin that the map works end to end.
        static void Poke(AddressSpace s)
        {
            s.Write8(0x0200, 0xA9); s.Write8(0x0201, 0x42);   // LDA #$42
            s.Write8(0x0202, 0xAA);                           // TAX
            s.Write8(0x0203, 0x4C); s.Write8(0x0204, 0x03); s.Write8(0x0205, 0x02);  // JMP $0203 (self)
        }

        var refSpace = NewRamSpace();
        Poke(refSpace);
        var refCpu = new Mos6502Cpu(refSpace) { PC = 0x0200, S = 0xFD, P = 0x24 };
        long refBudget = 8;
        refCpu.Run(ref refBudget);

        var jitSpace = NewRamSpace();
        Poke(jitSpace);
        var inner = new Mos6502Cpu(jitSpace) { PC = 0x0200, S = 0xFD, P = 0x24 };
        var jit = new JittedCpu(inner, jitSpace);
        long jitBudget = 8;
        jit.Run(ref jitBudget);

        Assert.Equal(0x42, inner.A);
        Assert.Equal(0x42, inner.X);
        Assert.Equal(refCpu.A, inner.A);
        Assert.Equal(refCpu.X, inner.X);
        Assert.Equal(refCpu.P, inner.P);
        Assert.Equal(refCpu.CycleCount, inner.CycleCount);
    }
}
