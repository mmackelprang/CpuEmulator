using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Tests.Mos6502;   // TracingAddressSpace
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M3.5-1 — the dedicated, deterministic interrupt-servicing UAT (decision D5). Interrupt SERVICING is
/// NOT single-step-vector-testable (the SingleStepTests vectors cover instruction execution, not the
/// CPU's response to an asserted IRQ/NMI line), so this UAT hand-constructs each case: set IM mode + IFF
/// state + memory + a pending line, Step() (which services), and assert the serviced vector PC, the
/// pushed return address, IFF1/IFF2 after, the cycle cost, R, WZ, and the HALT wake. ZEXALL (M3.5-2) is
/// the integration confirmation; this UAT is the primary gate.
/// </summary>
public class Z80InterruptServicingTests
{
    /// <summary>Build a Z80 with 64KiB program RAM + a 16-bit I/O space (both tracing), like the TomHarte
    /// runner — but constructed directly (no vector case). Returns the CPU + the inner program space so a
    /// test can seed/read memory.</summary>
    private static (Z80Cpu Cpu, AddressSpace Mem) BuildCpu()
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        var bus = new TracingAddressSpace(inner);
        var ioInner = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
        ioInner.MapMemory(0x0000, new byte[0x10000], writable: true);
        var io = new TracingAddressSpace(ioInner);
        var cpu = new Z80Cpu(bus, io);
        return (cpu, inner);
    }

    [Fact]
    public void InterruptPending_is_gated_by_IFF1_for_maskable_IRQ()
    {
        var (cpu, _) = BuildCpu();
        cpu.Iff1 = false;
        cpu.SetIrqLine(true);
        Assert.False(cpu.InterruptPending);   // IRQ asserted but IFF1 clear → masked
        cpu.Iff1 = true;
        Assert.True(cpu.InterruptPending);     // IRQ asserted + IFF1 set → pending
        cpu.SetIrqLine(false);
        Assert.False(cpu.InterruptPending);
    }

    [Fact]
    public void InterruptPending_is_set_by_NMI_regardless_of_IFF1()
    {
        var (cpu, _) = BuildCpu();
        cpu.Iff1 = false;             // NMI is non-maskable
        cpu.SetNmiLine(true);          // rising edge latches
        Assert.True(cpu.InterruptPending);
    }

    [Fact]
    public void SetNmiLine_is_edge_triggered()
    {
        var (cpu, _) = BuildCpu();
        cpu.SetNmiLine(true);          // rising edge → pending
        Assert.True(cpu.InterruptPending);
        cpu.SetNmiLine(true);          // held high, no new edge — still pending (not double-latched)
        Assert.True(cpu.InterruptPending);
        cpu.SetNmiLine(false);         // falling edge does NOT clear the pending latch
        Assert.True(cpu.InterruptPending);
    }

    [Fact]
    public void IM1_services_to_0x0038_pushing_PC_clearing_IFF_bumping_R()
    {
        var (cpu, mem) = BuildCpu();
        // Place a NOP at 0x0038 (the IM1 handler) — not executed here; we only assert the service vector.
        mem.Write8(0x0038, 0x00);
        cpu.SetRegister("PC", 0x1234);   // the instruction that WOULD run next → pushed return address
        cpu.SetRegister("SP", 0xFFF0);
        cpu.SetRegister("R", 0x10);
        cpu.SetRegister("WZ", 0x0000);
        cpu.Im = 1;
        cpu.Iff1 = true; cpu.Iff2 = true;
        cpu.SetIrqLine(true);

        long before = cpu.CycleCount;
        cpu.Step();   // services the interrupt (does NOT fetch an opcode)

        Assert.Equal(0x0038u, (uint)cpu.GetRegister("PC"));   // IM1 → RST 38h
        Assert.Equal(0xFFEEu, (uint)cpu.GetRegister("SP"));   // SP -= 2 (two pushes)
        Assert.Equal(0x34, mem.Read8(0xFFEE));                // PCL pushed
        Assert.Equal(0x12, mem.Read8(0xFFEF));                // PCH pushed
        Assert.Equal(0x0038u, (uint)cpu.GetRegister("WZ"));   // WZ = vector
        Assert.False(cpu.Iff1);                                // maskable ack clears IFF1
        Assert.False(cpu.Iff2);                                // ...and IFF2
        Assert.Equal(0x11u, (uint)cpu.GetRegister("R"));      // R low-7 bumped by 1 (0x10 → 0x11)
        Assert.Equal(13L, cpu.CycleCount - before);           // IM1 = 13 T-states
    }
}
