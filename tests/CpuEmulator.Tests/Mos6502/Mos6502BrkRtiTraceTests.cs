using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>
/// Cycle-exact bus-trace tests for BRK (0x00) and RTI (0x40).
/// BRK: fetch(1) + padding(1) + pushes(3) + vector(2) = 7 cycles.
/// RTI: fetch(1) + dummy(1) + stack-dummy(1) + pulls(3) = 6 cycles.
/// </summary>
public class Mos6502BrkRtiTraceTests
{
    [Fact]
    public void BRK_7_cycles_pushes_PC_P_and_vectors()
    {
        // Arrange: P=0x00, PC=0x0234, "00 FF" program; IRQ vector → 0x8000
        var (cpu, bus, inner) = NewCpuWithInner(0x0234, 0x00, 0xFF);
        cpu.SetRegister("P", 0x00);
        cpu.SetRegister("S", 0xFD);
        inner.Write8(0xFFFE, 0x00); // vector lo
        inner.Write8(0xFFFF, 0x80); // vector hi → 0x8000

        cpu.Step();

        // PC after: 0x8000; S decremented 3 times: 0xFD → 0xFA; I set; cycles=7
        Assert.Equal(0x8000ul, cpu.GetRegister("PC"));
        Assert.Equal(0xFAul,   cpu.GetRegister("S"));
        Assert.Equal(0x04ul,   cpu.GetRegister("P") & 0x04ul); // I set
        Assert.Equal(7L, cpu.CycleCount);

        // Bus trace: opcode fetch, padding, 3 pushes, 2 vector reads
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0234, 0x00, true),  // opcode fetch
            new BusAccess(0x0235, 0xFF, true),  // padding byte (PC++)
            new BusAccess(0x01FD, 0x02, false), // push PCH (0x0236 >> 8 = 0x02)
            new BusAccess(0x01FC, 0x36, false), // push PCL (0x0236 & 0xFF = 0x36)
            new BusAccess(0x01FB, 0x30, false), // push P|0x30 (stacked B=1)
            new BusAccess(0xFFFE, 0x00, true),  // vector lo
            new BusAccess(0xFFFF, 0x80, true)); // vector hi
    }

    [Fact]
    public void RTI_6_cycles_restores_P_and_PC()
    {
        // Arrange: S=0xFA; stack has P=0xDF at 0x01FB, PCL=0x36 at 0x01FC, PCH=0x02 at 0x01FD
        var (cpu, bus, inner) = NewCpuWithInner(0x8000, 0x40);
        cpu.SetRegister("S", 0xFA);
        inner.Write8(0x01FB, 0xDF); // stacked P (phantom bits applied on pull: (0xDF|0x20)&0xEF = 0xEF)
        inner.Write8(0x01FC, 0x36); // PCL
        inner.Write8(0x01FD, 0x02); // PCH → PC=0x0236

        cpu.Step();

        // 0xDF = 1101_1111; (|0x20) sets bit5 → 0xFF; (&0xEF) clears bit4 → 0xEF.
        // Confirmed against all 10000 TomHarte RTI (0x40) vectors (vector-driven correction).
        Assert.Equal(0xEFul,   cpu.GetRegister("P"));  // (0xDF|0x20)&0xEF
        Assert.Equal(0x0236ul, cpu.GetRegister("PC")); // no +1 (≠ RTS)
        Assert.Equal(0xFDul,   cpu.GetRegister("S"));  // restored
        Assert.Equal(6L, cpu.CycleCount);

        // Bus trace: opcode, dummy at PC, dummy at old S (0x01FA), P pull, PCL pull, PCH pull
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x8000, 0x40, true),  // opcode fetch
            new BusAccess(0x8001, 0x00, true),  // dummy read at PC (no increment)
            new BusAccess(0x01FA, 0x00, true),  // dummy read at old S
            new BusAccess(0x01FB, 0xDF, true),  // P pull
            new BusAccess(0x01FC, 0x36, true),  // PCL pull
            new BusAccess(0x01FD, 0x02, true)); // PCH pull
    }

    private static (Mos6502Cpu Cpu, TracingAddressSpace Bus, AddressSpace Inner)
        NewCpuWithInner(ushort startAt, params byte[] program)
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        for (int i = 0; i < program.Length; i++)
            inner.Write8((uint)(startAt + i), program[i]);
        var tracing = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(tracing);
        cpu.SetRegister("PC", startAt);
        return (cpu, tracing, inner);
    }
}
