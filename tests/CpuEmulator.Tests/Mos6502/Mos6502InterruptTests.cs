using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>
/// Unit tests for IRQ/NMI servicing at instruction boundaries.
/// Ground truth: the 64doc cycle table; the 7-cycle B=0 push sequence is verified
/// cycle-exact below. No TomHarte vectors cover the hardware-interrupt service sequence.
///
/// Setup convention: NewCpu places a NOP (0xEA) at 0x0200; vectors seed IRQ handler at
/// 0x8000 (RAM[$FFFE/$FFFF] = 0x00/0x80) and NMI handler at 0x9000
/// (RAM[$FFFA/$FFFB] = 0x00/0x90); a NOP (0xEA) is placed at both handler addresses.
/// S=0xFD, P from each test.
/// </summary>
public class Mos6502InterruptTests
{
    private static (Mos6502Cpu Cpu, TracingAddressSpace Bus, AddressSpace Inner) NewCpu()
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);

        // NOP at the start address
        inner.Write8(0x0200, 0xEA);

        // IRQ vector → 0x8000
        inner.Write8(0xFFFE, 0x00);
        inner.Write8(0xFFFF, 0x80);
        // NOP at IRQ handler
        inner.Write8(0x8000, 0xEA);

        // NMI vector → 0x9000
        inner.Write8(0xFFFA, 0x00);
        inner.Write8(0xFFFB, 0x90);
        // NOP at NMI handler
        inner.Write8(0x9000, 0xEA);

        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.PC = 0x0200;
        cpu.S  = 0xFD;
        return (cpu, bus, inner);
    }

    [Fact]
    public void Irq_masked_by_I_flag_executes_normally()
    {
        var (cpu, _, _) = NewCpu();
        cpu.P = 0x04; // I set → IRQ masked
        cpu.SetIrqLine(true);

        cpu.Step();

        Assert.Equal(0x0201ul, cpu.PC); // NOP ran, PC advanced by 1
        Assert.Equal(2, cpu.CycleCount);
    }

    [Fact]
    public void Irq_serviced_when_I_clear()
    {
        var (cpu, _, _) = NewCpu();
        cpu.P = 0x20; // I clear (bit5=1, no I)
        cpu.SetIrqLine(true);

        cpu.Step();

        Assert.Equal(0x8000ul, cpu.PC);
        Assert.Equal(0xFAul, cpu.S);     // 3 bytes pushed: 0xFD → 0xFA
        Assert.True((cpu.P & 0x04) != 0, "I flag should be set after IRQ service");
        Assert.Equal(7, cpu.CycleCount);
    }

    [Fact]
    public void Irq_service_bus_pattern_and_stacked_P()
    {
        // P=0x20 → stacked B=0 formula: (0x20 | 0x20) & 0xEF = 0x20
        var (cpu, bus, _) = NewCpu();
        cpu.P = 0x20;
        cpu.SetIrqLine(true);

        cpu.Step();

        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0xEA, true),   // dummy opcode fetch #1 (PC not incremented)
            new BusAccess(0x0200, 0xEA, true),   // dummy opcode fetch #2 (PC not incremented)
            new BusAccess(0x01FD, 0x02, false),  // push PCH (PC=$0200 → high byte = 0x02)
            new BusAccess(0x01FC, 0x00, false),  // push PCL (low byte = 0x00)
            new BusAccess(0x01FB, 0x20, false),  // push P with B=0: (0x20|0x20)&0xEF = 0x20
            new BusAccess(0xFFFE, 0x00, true),   // vector lo
            new BusAccess(0xFFFF, 0x80, true));  // vector hi
    }

    [Fact]
    public void Nmi_ignores_I_and_uses_FFFA()
    {
        var (cpu, _, _) = NewCpu();
        cpu.P = 0x04; // I set — NMI should ignore it
        cpu.SetNmiLine(true);

        cpu.Step();

        Assert.Equal(0x9000ul, cpu.PC);
        Assert.Equal(7, cpu.CycleCount);
    }

    [Fact]
    public void Nmi_edge_latched_serviced_once_held_line_no_refire()
    {
        var (cpu, _, _) = NewCpu();
        cpu.P = 0x20;
        cpu.SetNmiLine(true); // rising edge → latch set

        // First step: services NMI, goes to handler at 0x9000
        cpu.Step();
        Assert.Equal(0x9000ul, cpu.PC);

        // Second step: line still held high but latch was cleared → NOP at 0x9000 runs
        cpu.Step();
        Assert.Equal(0x9001ul, cpu.PC);
    }

    [Fact]
    public void Nmi_refires_after_release_and_reassert()
    {
        var (cpu, _, _) = NewCpu();
        cpu.P = 0x20;
        cpu.SetNmiLine(true);  // rising edge → latch
        cpu.Step();             // service: PC → 0x9000
        cpu.Step();             // NOP at handler: PC → 0x9001 (I is set, but NMI ignores I)

        // Release and re-assert → new rising edge, new latch
        cpu.SetNmiLine(false);
        cpu.SetNmiLine(true);

        long cyclesBefore = cpu.CycleCount;
        cpu.Step();

        Assert.Equal(cyclesBefore + 7, cpu.CycleCount); // serviced again
    }

    [Fact]
    public void Nmi_beats_irq_and_service_masks_held_irq()
    {
        var (cpu, _, _) = NewCpu();
        cpu.P = 0x20; // I clear
        cpu.SetIrqLine(true);
        cpu.SetNmiLine(true); // both pending; NMI wins

        // First step: NMI serviced → PC = 0x9000, I set
        cpu.Step();
        Assert.Equal(0x9000ul, cpu.PC);

        // Second step: IRQ still held but I is now set → NOP at 0x9000 runs
        cpu.Step();
        Assert.Equal(0x9001ul, cpu.PC);
    }

    [Fact]
    public void Reset_clears_pending_nmi()
    {
        var (cpu, _, inner) = NewCpu();
        cpu.P = 0x20;
        cpu.SetNmiLine(true); // latch set

        // Point reset vector to 0x0200 so after reset, PC = 0x0200 again
        inner.Write8(0xFFFC, 0x00);
        inner.Write8(0xFFFD, 0x02);

        cpu.Reset(); // should clear _nmiPending

        // Step: no interrupt service — NOP at 0x0200 runs
        cpu.Step();
        Assert.Equal(0x0201ul, cpu.PC);

        // Confirm the latch works again: release and reassert, then step → serviced
        cpu.SetNmiLine(false);
        cpu.SetNmiLine(true);
        long cyclesBefore = cpu.CycleCount;
        cpu.Step();
        Assert.Equal(cyclesBefore + 7, cpu.CycleCount);
    }

    [Fact]
    public void Service_costs_exactly_7_cycles()
    {
        var (cpu, _, _) = NewCpu();
        cpu.P = 0x20;
        cpu.SetIrqLine(true);

        long before = cpu.CycleCount;
        cpu.Step();

        Assert.Equal(7, cpu.CycleCount - before);
    }

    [Fact]
    public void Interrupts_checked_between_instructions_during_Run()
    {
        var (cpu, _, _) = NewCpu();
        cpu.P = 0x20; // I clear
        cpu.SetIrqLine(true);

        // budget=9: service (7) + NOP at handler (2) = 9 total
        long budget = 9;
        cpu.Run(ref budget);

        Assert.Equal(0x8001ul, cpu.PC);  // NOP at handler executed
        Assert.Equal(0L, budget);
    }
}
