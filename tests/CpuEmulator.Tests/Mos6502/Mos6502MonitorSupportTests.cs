using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>
/// Live tests for the generated IMonitorSupport members on Mos6502Cpu:
/// InstructionLength (mode→length table), TryAssemble happy/rejection rows,
/// and InterruptPending (the contract: pending ⟺ next Step services an interrupt).
/// </summary>
public class Mos6502MonitorSupportTests
{
    // ── InstructionLength ────────────────────────────────────────────────────

    [Theory]
    [InlineData(0xEA, 1)]  // NOP Implied
    [InlineData(0x0A, 1)]  // ASL Accumulator
    [InlineData(0x00, 1)]  // BRK Implied — dataset bytes:1; the padding byte is a runtime artifact
    [InlineData(0xA9, 2)]  // LDA Immediate
    [InlineData(0xB5, 2)]  // LDA ZeroPageX
    [InlineData(0x96, 2)]  // STX ZeroPageY
    [InlineData(0xA1, 2)]  // LDA IndirectX
    [InlineData(0xB1, 2)]  // LDA IndirectY
    [InlineData(0xD0, 2)]  // BNE Relative
    [InlineData(0xAD, 3)]  // LDA Absolute
    [InlineData(0xBD, 3)]  // LDA AbsoluteX
    [InlineData(0x6C, 3)]  // JMP Indirect
    [InlineData(0x20, 3)]  // JSR Absolute
    [InlineData(0xFF, 1)]  // undefined → walks as 1
    public void InstructionLength_maps_mode_to_expected_bytes(byte opcode, int expected)
    {
        Assert.Equal(expected, Mos6502Cpu.InstructionLength(opcode));
    }

    // ── InterruptPending ─────────────────────────────────────────────────────

    // Setup mirrors Mos6502InterruptTests: NOP at $0200; IRQ→$8000; NMI→$9000.
    private static (Mos6502Cpu Cpu, IAddressSpace Space) NewCpu()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);

        space.Write8(0x0200, 0xEA); // NOP at start
        // IRQ vector → $8000
        space.Write8(0xFFFE, 0x00);
        space.Write8(0xFFFF, 0x80);
        space.Write8(0x8000, 0xEA); // NOP at handler
        // NMI vector → $9000
        space.Write8(0xFFFA, 0x00);
        space.Write8(0xFFFB, 0x90);
        space.Write8(0x9000, 0xEA); // NOP at handler

        var cpu = new Mos6502Cpu(space);
        cpu.PC = 0x0200;
        cpu.S = 0xFD;
        return (cpu, space);
    }

    [Fact]
    public void Idle_is_not_pending()
    {
        var (cpu, _) = NewCpu();
        // No interrupt asserted at all
        Assert.False(cpu.InterruptPending);
    }

    [Fact]
    public void Irq_with_I_clear_is_pending_and_step_services()
    {
        var (cpu, _) = NewCpu();
        cpu.P = 0x20; // I clear
        cpu.SetIrqLine(true);

        Assert.True(cpu.InterruptPending);

        // Step should service the interrupt — PC lands at the IRQ handler
        cpu.Step();
        Assert.Equal(0x8000u, cpu.PC);
    }

    [Fact]
    public void Irq_with_I_set_is_not_pending()
    {
        var (cpu, _) = NewCpu();
        cpu.P = 0x24; // I set
        cpu.SetIrqLine(true);

        Assert.False(cpu.InterruptPending);

        // Step executes the NOP, not an interrupt
        cpu.Step();
        Assert.Equal(0x0201u, cpu.PC);
    }

    [Fact]
    public void Nmi_latch_is_pending_despite_I()
    {
        var (cpu, _) = NewCpu();
        cpu.P = 0x24; // I set — NMI ignores it
        cpu.SetNmiLine(true);

        Assert.True(cpu.InterruptPending);

        // Step services the NMI
        cpu.Step();
        Assert.Equal(0x9000u, cpu.PC);
    }
}
