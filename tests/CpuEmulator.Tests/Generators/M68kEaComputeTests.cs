using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kEaComputeTests
{
    private const string Source = M68kEaTestSpecs.EaProbeCpu;   // the grammar CPU + an emitted ComputeEaProbe

    private static (object Cpu, System.Type T) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        var bus = new CpuEmulator.Core.AddressSpace(
            CpuEmulator.Core.AddressSpaceKind.Program, addressBits: 24);   // confirm the M4.2 ctor shape
        bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
        var cpu = System.Activator.CreateInstance(t, new object[] { bus })!;
        return (cpu, t);
    }
    private static void SetReg(object c, System.Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(c, new object[] { r, v });
    private static ulong GetReg(object c, System.Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(c, new object[] { r })!;
    private static uint Ea(object c, System.Type t, uint mode, uint reg, uint size,
                           CpuEmulator.Core.Jit.ExtensionWords ext, bool pureEa = false) =>
        (uint)t.GetMethod("ComputeEaProbe")!.Invoke(c, new object[] { mode, reg, size, ext, pureEa })!;

    [Fact]
    public void Address_register_indirect_uses_An()
    {
        var (c, t) = Build();
        SetReg(c, t, "A3", 0x00102000);
        Assert.Equal(0x00102000u, Ea(c, t, mode: 2, reg: 3, size: 1, default));   // (A3)
    }

    [Fact]
    public void Displacement_d16_An_adds_signed_displacement()
    {
        var (c, t) = Build();
        SetReg(c, t, "A2", 0x00001000);
        var ext = new CpuEmulator.Core.Jit.ExtensionWords(0xFFFE, 0, 0, 0, 1);    // d16 = -2 (signed)
        Assert.Equal(0x00000FFEu, Ea(c, t, mode: 5, reg: 2, size: 1, ext));        // d16(A2) = A2 - 2
    }

    [Fact]
    public void Abs_l_uses_the_two_extension_words()
    {
        var (c, t) = Build();
        var ext = new CpuEmulator.Core.Jit.ExtensionWords(0x0012, 0x3456, 0, 0, 2);
        Assert.Equal(0x00123456u, Ea(c, t, mode: 7, reg: 1, size: 1, ext));        // abs.l
    }

    [Fact]
    public void PostIncrement_reads_An_then_adds_by_size()
    {
        var (c, t) = Build();
        SetReg(c, t, "A1", 0x00002000);
        uint ea = Ea(c, t, mode: 3, reg: 1, size: 2, default);   // (A1)+ at .l
        Assert.Equal(0x00002000u, ea);                            // the EA is the CURRENT A1 (D3 ordering)
        Assert.Equal(0x00002004u, (uint)GetReg(c, t, "A1"));      // A1 += 4 (.l magnitude)
    }

    [Fact]
    public void PreDecrement_subtracts_by_size_then_reads_An()
    {
        var (c, t) = Build();
        SetReg(c, t, "A4", 0x00002000);
        uint ea = Ea(c, t, mode: 4, reg: 4, size: 1, default);   // -(A4) at .w
        Assert.Equal(0x00001FFEu, ea);                            // A4 -= 2 FIRST, then the EA is the new A4
        Assert.Equal(0x00001FFEu, (uint)GetReg(c, t, "A4"));      // A4 == new value (D3 ordering)
    }

    [Fact]
    public void A7_postincrement_byte_moves_by_two()   // D4: the stack stays word-aligned
    {
        var (c, t) = Build();
        SetReg(c, t, "A7", 0x00003000);
        uint ea = Ea(c, t, mode: 3, reg: 7, size: 0, default);   // (A7)+ at .b
        Assert.Equal(0x00003000u, ea);
        Assert.Equal(0x00003002u, (uint)GetReg(c, t, "A7"));      // +2 even for .b (NOT +1)
    }

    [Fact]
    public void Pure_ea_compute_does_not_mutate_for_postinc()   // LEA/PEA: compute, no write-back
    {
        var (c, t) = Build();
        SetReg(c, t, "A0", 0x00004000);
        uint ea = Ea(c, t, mode: 3, reg: 0, size: 2, default, pureEa: true);   // LEA (A0)+ is illegal in HW,
        // but the pure-EA path proves "compute the address, do NOT perform the side effect" for LEA/PEA on
        // the legal control modes; here we assert the pure-EA flag suppresses the write-back.
        Assert.Equal(0x00004000u, ea);
        Assert.Equal(0x00004000u, (uint)GetReg(c, t, "A0"));      // unchanged — pure-EA suppresses write-back
    }
}
