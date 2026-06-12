using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

/// <summary>
/// Cycle-exact trace tests for the stack class (PHA/PLA/PHP/PLP), flag class (CLC/SEC/etc.),
/// and flow class (JSR/RTS). Silicon ground truth: plan cycle-template table.
/// Key invariants: JSR pushes return-address = call+2 (the last byte of the 3-byte instruction);
/// RTS pops that return-address and increments by 1; PHP pushes P|0x30 (phantom bits set);
/// PLP forces bit5=1 and bit4=0 in the live register (real-hardware convention, trued in 3b-i;
/// TomHarte vectors confirm — pre-authorized by plan amendment).
/// </summary>
public class Mos6502StackFlowTraceTests
{
    // Initial S = 0xFD throughout unless stated otherwise (matches hardware after reset + 2 pushes)

    private static (Mos6502Cpu Cpu, TracingAddressSpace Bus, AddressSpace Inner)
        NewCpuAt(ushort origin, params byte[] program)
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        for (uint i = 0; i < program.Length; i++)
            inner.Write8((uint)(origin + i), program[i]);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", origin);
        return (cpu, bus, inner);
    }

    // ── Stack push / pull ─────────────────────────────────────────────────────

    [Fact]
    public void PHA_3_cycles()
    {
        // PHA (0x48) @0200; A=0x42; S=0xFD => push to $01FD; S becomes 0xFC
        var (cpu, bus, inner) = NewCpuAt(0x0200, 0x48);
        cpu.SetRegister("A", 0x42);
        cpu.SetRegister("S", 0xFD);

        cpu.Step();

        Assert.Equal(3, cpu.CycleCount);
        Assert.Equal(0xFCul, cpu.GetRegister("S"));
        Assert.Equal(0x42, inner.Read8(0x01FD));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x48, true),
            new BusAccess(0x0201, 0x00, true),  // dummy read at PC (no increment)
            new BusAccess(0x01FD, 0x42, false)); // write to stack
    }

    [Fact]
    public void PHP_pushes_P_or_0x30()
    {
        // PHP (0x08); P=0x00 => pushed value = 0x00|0x30 = 0x30 (phantom bits 4+5 forced set)
        var (cpu, bus, inner) = NewCpuAt(0x0200, 0x08);
        cpu.SetRegister("P", 0x00);
        cpu.SetRegister("S", 0xFD);

        cpu.Step();

        Assert.Equal(3, cpu.CycleCount);
        Assert.Equal(0xFCul, cpu.GetRegister("S"));
        Assert.Equal(0x30, inner.Read8(0x01FD)); // bits 5,4 set
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x08, true),
            new BusAccess(0x0201, 0x00, true),
            new BusAccess(0x01FD, 0x30, false));
    }

    [Fact]
    public void PLA_4_cycles_sets_NZ()
    {
        // PLA (0x68); S=0xFC; RAM[$01FD]=0x80 => pull A=0x80; N=1; S becomes 0xFD
        var (cpu, bus, inner) = NewCpuAt(0x0200, 0x68);
        inner.Write8(0x01FD, 0x80);
        cpu.SetRegister("S", 0xFC);

        cpu.Step();

        Assert.Equal(4, cpu.CycleCount);
        Assert.Equal(0x80ul, cpu.GetRegister("A"));
        Assert.Equal(0xFDul, cpu.GetRegister("S"));
        Mos6502TestHarness.AssertNZ(cpu, n: true, z: false);
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x68, true),
            new BusAccess(0x0201, 0x00, true),  // dummy read at PC
            new BusAccess(0x01FC, 0x00, true),  // dummy read at old S (increment cycle)
            new BusAccess(0x01FD, 0x80, true)); // real pull
    }

    [Fact]
    public void PLP_forces_bit5_set_bit4_cleared_real_hardware_convention()
    {
        // PLP (0x28); S=0xFC; RAM[$01FD]=0xCF
        // Stacked byte: 0xCF = 1100_1111 (bit5=0, bit4=0)
        // Real hardware forces bit5=1, bit4=0 in the live register: (0xCF | 0x20) & 0xEF = 0xEF
        // TomHarte vectors confirm this convention; trued in 3b-i (pre-authorized).
        var (cpu, bus, inner) = NewCpuAt(0x0200, 0x28);
        inner.Write8(0x01FD, 0xCF);
        cpu.SetRegister("S", 0xFC);

        cpu.Step();

        Assert.Equal(4, cpu.CycleCount);
        Assert.Equal(0xEFul, cpu.GetRegister("P")); // bit5 forced set, bit4 forced cleared
        Assert.Equal(0xFDul, cpu.GetRegister("S"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, 0x28, true),
            new BusAccess(0x0201, 0x00, true),
            new BusAccess(0x01FC, 0x00, true),  // dummy read at old S
            new BusAccess(0x01FD, 0xCF, true));
    }

    // ── Flag ops ──────────────────────────────────────────────────────────────

    // (opcode, pBefore, changedMask, setNotClear)
    // changedMask = the bit being modified; setNotClear = true if the op SETS the bit
    [Theory]
    [InlineData(0x18, 0x01, 0x01, false)] // CLC: C=1 → C=0 (mask=0x01)
    [InlineData(0x38, 0x00, 0x01, true)]  // SEC: C=0 → C=1
    [InlineData(0xB8, 0x40, 0x40, false)] // CLV: V=1 → V=0 (mask=0x40)
    [InlineData(0xD8, 0x08, 0x08, false)] // CLD: D=1 → D=0 (mask=0x08)
    [InlineData(0xF8, 0x00, 0x08, true)]  // SED: D=0 → D=1
    [InlineData(0x58, 0x04, 0x04, false)] // CLI: I=1 → I=0 (mask=0x04)
    [InlineData(0x78, 0x00, 0x04, true)]  // SEI: I=0 → I=1
    public void Flag_ops_2_cycles(byte opcode, byte pBefore, byte changedMask, bool setNotClear)
    {
        // Each flag op: 2 cycles (opcode + dummy read); only the target bit changes
        var (cpu, bus) = Mos6502TestHarness.NewCpu(opcode);
        cpu.SetRegister("P", pBefore);

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        ulong p = cpu.GetRegister("P");
        if (setNotClear)
            Assert.True((p & changedMask) != 0, $"Expected bit 0x{changedMask:X2} to be set");
        else
            Assert.True((p & changedMask) == 0, $"Expected bit 0x{changedMask:X2} to be clear");
        // Other bits must be unchanged
        ulong unchangedMask = (ulong)(0xFF & ~changedMask);
        Assert.Equal(pBefore & unchangedMask, p & unchangedMask);
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0200, opcode, true),
            new BusAccess(0x0201, 0x00, true)); // dummy read
    }

    // ── JSR and RTS ────────────────────────────────────────────────────────────

    [Fact]
    public void JSR_6_cycles_canonical_order()
    {
        // JSR $8000 @0x0234; S=0xFD
        // fetch 0x20 at 0x0234; fetch lo=0x00 at 0x0235; dummy read at $01FD;
        // push PCH=0x02 at $01FD; S=0xFC; push PCL=0x36 at $01FC; S=0xFB;
        // fetch hi=0x80 at 0x0236; PC=0x8000
        // Pushed address = 0x0236 = return-address (next-PC − 1)
        var (cpu, bus, inner) = NewCpuAt(0x0234, 0x20, 0x00, 0x80);
        cpu.SetRegister("S", 0xFD);

        cpu.Step();

        Assert.Equal(6, cpu.CycleCount);
        Assert.Equal(0x8000ul, cpu.GetRegister("PC"));
        Assert.Equal(0xFBul, cpu.GetRegister("S"));
        // Verify pushed return address = 0x0236
        Assert.Equal(0x02, inner.Read8(0x01FD)); // PCH
        Assert.Equal(0x36, inner.Read8(0x01FC)); // PCL
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x0234, 0x20, true),
            new BusAccess(0x0235, 0x00, true),
            new BusAccess(0x01FD, 0x00, true),  // stack dummy read
            new BusAccess(0x01FD, 0x02, false), // push PCH
            new BusAccess(0x01FC, 0x36, false), // push PCL
            new BusAccess(0x0236, 0x80, true)); // fetch hi byte
    }

    [Fact]
    public void RTS_6_cycles()
    {
        // RTS (0x60) @0x8000; stack has PCL=0x36 @$01FC, PCH=0x02 @$01FD; S=0xFB
        // Pull: dummy at PC; dummy at $01FB; S=0xFC; pull PCL=0x36 from $01FC; S=0xFD;
        // pull PCH=0x02 from $01FD; PC=0x0236; dummy read at 0x0236; PC=0x0237
        var (cpu, bus, inner) = NewCpuAt(0x8000, 0x60);
        inner.Write8(0x01FC, 0x36);
        inner.Write8(0x01FD, 0x02);
        cpu.SetRegister("S", 0xFB);

        cpu.Step();

        Assert.Equal(6, cpu.CycleCount);
        Assert.Equal(0x0237ul, cpu.GetRegister("PC")); // pulled 0x0236 + 1
        Assert.Equal(0xFDul, cpu.GetRegister("S"));
        Mos6502TestHarness.AssertTrace(bus,
            new BusAccess(0x8000, 0x60, true),
            new BusAccess(0x8001, 0x00, true),  // dummy read at PC
            new BusAccess(0x01FB, 0x00, true),  // dummy read at old S
            new BusAccess(0x01FC, 0x36, true),  // pull PCL
            new BusAccess(0x01FD, 0x02, true),  // pull PCH
            new BusAccess(0x0236, 0x00, true)); // dummy read at new PC before increment
    }
}
