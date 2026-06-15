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

    [Fact]
    public void NMI_services_to_0x0066_saving_IFF1_into_IFF2_and_clearing_IFF1()
    {
        var (cpu, mem) = BuildCpu();
        cpu.SetRegister("PC", 0x4000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.SetRegister("R", 0x00);
        cpu.Iff1 = true; cpu.Iff2 = true;     // both enabled before NMI
        cpu.SetNmiLine(true);                  // edge → pending (non-maskable)

        long before = cpu.CycleCount;
        cpu.Step();

        Assert.Equal(0x0066u, (uint)cpu.GetRegister("PC"));   // NMI vector
        Assert.Equal(0xFFEEu, (uint)cpu.GetRegister("SP"));
        Assert.Equal(0x00, mem.Read8(0xFFEE));                // PCL
        Assert.Equal(0x40, mem.Read8(0xFFEF));                // PCH
        Assert.False(cpu.Iff1);                                // IFF1 cleared
        Assert.True(cpu.Iff2);                                 // IFF2 = saved old IFF1 (was true)
        Assert.Equal(0x0066u, (uint)cpu.GetRegister("WZ"));
        Assert.Equal(0x01u, (uint)cpu.GetRegister("R"));      // R bumped by 1
        Assert.Equal(11L, cpu.CycleCount - before);           // NMI = 11 T-states
    }

    [Fact]
    public void NMI_then_RETN_restores_IFF1_from_saved_IFF2()
    {
        var (cpu, mem) = BuildCpu();
        // Handler at 0x0066: RETN (ED 45).
        mem.Write8(0x0066, 0xED); mem.Write8(0x0067, 0x45);
        cpu.SetRegister("PC", 0x4000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.Iff1 = true; cpu.Iff2 = true;
        cpu.SetNmiLine(true);

        cpu.Step();                 // service NMI → IFF1=0, IFF2=1, PC=0x0066
        Assert.False(cpu.Iff1);
        cpu.Step();                 // execute RETN at 0x0066 → IFF1 = IFF2 = 1, PC = 0x4000 (popped)
        Assert.True(cpu.Iff1);       // restored from the saved IFF2
        Assert.Equal(0x4000u, (uint)cpu.GetRegister("PC"));
    }

    [Fact]
    public void IM0_defaults_to_RST_38h()
    {
        var (cpu, mem) = BuildCpu();
        cpu.SetRegister("PC", 0x2000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.Im = 0;
        cpu.Iff1 = true; cpu.Iff2 = true;
        cpu.SetIrqLine(true);
        // InterruptData defaults to 0xFF (RST 38h opcode) → vector 0x0038.

        long before = cpu.CycleCount;
        cpu.Step();

        Assert.Equal(0x0038u, (uint)cpu.GetRegister("PC"));
        Assert.Equal(0x00, mem.Read8(0xFFEE));                // PCL
        Assert.Equal(0x20, mem.Read8(0xFFEF));                // PCH
        Assert.False(cpu.Iff1); Assert.False(cpu.Iff2);
        Assert.Equal(13L, cpu.CycleCount - before);           // IM0 RST = 13 T
    }

    [Fact]
    public void IM0_decodes_the_device_RST_opcode()
    {
        var (cpu, _) = BuildCpu();
        cpu.SetRegister("PC", 0x2000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.Im = 0;
        cpu.Iff1 = true;
        cpu.InterruptData = 0xDF;   // RST 18h opcode (0xDF) → vector 0x0018
        cpu.SetIrqLine(true);

        cpu.Step();
        Assert.Equal(0x0018u, (uint)cpu.GetRegister("PC"));
    }

    [Fact]
    public void IM2_reads_the_vector_from_the_I_register_table()
    {
        var (cpu, mem) = BuildCpu();
        // I = 0x12, device byte = 0x80 → table pointer 0x1280; vector stored there = 0x9ABC.
        mem.Write8(0x1280, 0xBC);   // vector lo
        mem.Write8(0x1281, 0x9A);   // vector hi
        cpu.SetRegister("PC", 0x3000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.SetRegister("I", 0x12);
        cpu.Im = 2;
        cpu.Iff1 = true;
        cpu.InterruptData = 0x80;
        cpu.SetIrqLine(true);

        long before = cpu.CycleCount;
        cpu.Step();

        Assert.Equal(0x9ABCu, (uint)cpu.GetRegister("PC"));   // vector from the table
        Assert.Equal(0x9ABCu, (uint)cpu.GetRegister("WZ"));
        Assert.Equal(0x00, mem.Read8(0xFFEE));                // PCL of the return address (0x3000)
        Assert.Equal(0x30, mem.Read8(0xFFEF));                // PCH
        Assert.False(cpu.Iff1); Assert.False(cpu.Iff2);
        Assert.Equal(19L, cpu.CycleCount - before);           // IM2 = 19 T-states
    }

    [Fact]
    public void IM2_masks_the_device_byte_low_bit()
    {
        var (cpu, mem) = BuildCpu();
        // Device byte 0x81 → masked to 0x80 (the table is word-aligned: bit 0 cleared).
        mem.Write8(0x1280, 0x11); mem.Write8(0x1281, 0x22);   // vector 0x2211 at 0x1280
        cpu.SetRegister("PC", 0x3000); cpu.SetRegister("SP", 0xFFF0);
        cpu.SetRegister("I", 0x12); cpu.Im = 2; cpu.Iff1 = true;
        cpu.InterruptData = 0x81;   // low bit set → masked off
        cpu.SetIrqLine(true);
        cpu.Step();
        Assert.Equal(0x2211u, (uint)cpu.GetRegister("PC"));   // read from 0x1280, not 0x1281
    }
}
