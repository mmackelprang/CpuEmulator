using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

public class RemapNoRegressionTests
{
    [Fact]
    public void A_JittedCpu_with_no_remap_produces_identical_output()
    {
        // A minimal program that stores a marker, run on a JIT that has the listener registered but
        // never receives an OnRemap. The result must match a run with no listener machinery involved
        // — i.e. the registration is inert until a remap happens.
        var bus = new AddressSpace(AddressSpaceKind.Program, 16);
        bus.MapMemory(0x0000, new byte[0x0100], writable: true);
        var code = new byte[0x0100];
        code[0x00] = 0xA9; code[0x01] = 0x7C;   // LDA #$7C
        code[0x02] = 0x85; code[0x03] = 0x20;   // STA $20
        code[0x04] = 0x4C; code[0x05] = 0x04; code[0x06] = 0xD0; // JMP $D004
        bus.MapMemory(0xD000, code, writable: false);
        var vec = new byte[0x0100]; vec[0xFC] = 0x00; vec[0xFD] = 0xD0;
        bus.MapMemory(0xFF00, vec, writable: false);

        var cpu = new JittedCpu<Mos6502Cpu>(new Mos6502Cpu(bus), Mos6502Cpu.JitTarget, bus);
        cpu.Reset();
        long budget = 50; cpu.Run(ref budget);

        Assert.Equal(0x7C, bus.Read8(0x0020));  // ran exactly as before the seam existed
    }
}
