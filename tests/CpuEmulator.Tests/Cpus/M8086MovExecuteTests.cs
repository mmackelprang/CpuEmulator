using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using Xunit;

namespace CpuEmulator.Tests.Cpus;

/// <summary>
/// M5.5a — vector-free synthetic proof of the 8086 MOV-family execute pipeline (decode → ModR/M → EA →
/// segment → bus). Each test hand-assembles a tiny MOV program into the 20-bit RAM at CS:IP, Steps the real
/// <see cref="M8086Cpu"/> once, and asserts the register/memory/IP result. Covers: reg→reg byte (88, with the
/// AL-write-preserves-AH partial-write hazard); reg→reg word (89); reg←mem with a displacement EA (8A/8B);
/// mem←reg (88/89 to memory); imm→reg8 (B0) and imm→reg16 (B8); imm→mem (C6/C7); segment-register moves
/// (8C/8E); accumulator-direct (A0-A3); and a segment-override prefix changing the effective segment. These
/// are fast and exercise the same paths the 8088 TomHarte sweep drives.
/// </summary>
public class M8086MovExecuteTests
{
    // A CPU over a fully-mapped 1 MB little-endian space. Code + data both live in RAM.
    private static M8086Cpu NewCpu(out AddressSpace bus)
    {
        bus = new AddressSpace(AddressSpaceKind.Program, 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        return new M8086Cpu(bus);
    }

    // Load instruction bytes at the CS:IP physical address (CS<<4)+IP, leaving the registers for the caller.
    private static void LoadCode(AddressSpace bus, ushort cs, ushort ip, params byte[] code)
    {
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (uint i = 0; i < code.Length; i++)
            bus.Write8((phys + i) & 0xFFFFF, code[i]);
    }

    [Fact]
    public void Mov_88_reg_to_reg_byte_preserves_the_other_half()
    {
        // 88 C4 = MOV AH, AL  (mod=11, reg=000(AL), r/m=100(AH)).  AL→AH; AL unchanged.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x1234);   // AH=0x12, AL=0x34
        LoadCode(bus, 0x0000, 0x0000, 0x88, 0xC4);
        cpu.Step();
        Assert.Equal(0x3434u, cpu.GetRegister("AX"));   // AH←AL (0x34), AL preserved (0x34)
        Assert.Equal(0x02u, cpu.GetRegister("IP"));
    }

    [Fact]
    public void Mov_88_partial_write_into_AL_preserves_AH()
    {
        // 88 D8 = MOV AL, BL  (mod=11, reg=011(BL), r/m=000(AL)).  BL→AL; AH preserved.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0xAA00);   // AH=0xAA, AL=0x00
        cpu.SetRegister("BX", 0x0055);   // BL=0x55
        LoadCode(bus, 0x0000, 0x0000, 0x88, 0xD8);
        cpu.Step();
        Assert.Equal(0xAA55u, cpu.GetRegister("AX"));   // AH preserved, AL←BL
    }

    [Fact]
    public void Mov_89_reg_to_reg_word()
    {
        // 89 D8 = MOV AX, BX  (mod=11, reg=011(BX), r/m=000(AX)).  BX→AX.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("BX", 0xBEEF);
        LoadCode(bus, 0x0000, 0x0000, 0x89, 0xD8);
        cpu.Step();
        Assert.Equal(0xBEEFu, cpu.GetRegister("AX"));
    }

    [Fact]
    public void Mov_8B_reg_from_mem_with_disp16_direct()
    {
        // 8B 1E 34 12 = MOV BX, [0x1234]  (mod=00, reg=011(BX), r/m=110 disp16-direct).  DS:0x1234 → BX.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x2000);   // DS base = 0x20000
        uint dataPhys = (uint)((0x2000 << 4) + 0x1234);
        bus.Write8(dataPhys, 0xCD);
        bus.Write8(dataPhys + 1, 0xAB);   // LE word 0xABCD
        LoadCode(bus, 0x0000, 0x0000, 0x8B, 0x1E, 0x34, 0x12);
        cpu.Step();
        Assert.Equal(0xABCDu, cpu.GetRegister("BX"));
        Assert.Equal(0x04u, cpu.GetRegister("IP"));   // 1 opcode + 1 modrm + 2 disp
    }

    [Fact]
    public void Mov_8A_reg8_from_mem_with_disp8()
    {
        // 8A 47 05 = MOV AL, [BX+0x05]  (mod=01, reg=000(AL), r/m=111([BX]+disp8)).  DS:(BX+5) → AL.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x3000);
        cpu.SetRegister("BX", 0x0010);
        uint dataPhys = (uint)((0x3000 << 4) + 0x0010 + 0x05);
        bus.Write8(dataPhys, 0x7E);
        LoadCode(bus, 0x0000, 0x0000, 0x8A, 0x47, 0x05);
        cpu.Step();
        Assert.Equal(0x7Eu, cpu.GetRegister("AL"));
    }

    [Fact]
    public void Mov_89_reg_to_mem()
    {
        // 89 1E 00 10 = MOV [0x1000], BX  (mod=00, reg=011(BX), r/m=110 disp16).  BX → DS:0x1000.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x4000);
        cpu.SetRegister("BX", 0x1289);
        LoadCode(bus, 0x0000, 0x0000, 0x89, 0x1E, 0x00, 0x10);
        cpu.Step();
        uint dataPhys = (uint)((0x4000 << 4) + 0x1000);
        Assert.Equal((byte)0x89, bus.Read8(dataPhys));        // LE low byte
        Assert.Equal((byte)0x12, bus.Read8(dataPhys + 1));    // LE high byte
    }

    [Fact]
    public void Mov_88_reg8_to_mem()
    {
        // 88 0E 00 20 = MOV [0x2000], CL  (mod=00, reg=001(CL), r/m=110 disp16).  CL → DS:0x2000.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x5000);
        cpu.SetRegister("CX", 0x009A);   // CL=0x9A
        LoadCode(bus, 0x0000, 0x0000, 0x88, 0x0E, 0x00, 0x20);
        cpu.Step();
        Assert.Equal((byte)0x9A, bus.Read8((uint)((0x5000 << 4) + 0x2000)));
    }

    [Fact]
    public void Mov_B0_imm_to_reg8()
    {
        // B3 7F = MOV BL, 0x7F.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("BX", 0xFF00);   // BH preserved
        LoadCode(bus, 0x0000, 0x0000, 0xB3, 0x7F);
        cpu.Step();
        Assert.Equal(0xFF7Fu, cpu.GetRegister("BX"));
    }

    [Fact]
    public void Mov_B8_imm_to_reg16()
    {
        // B9 CD AB = MOV CX, 0xABCD.
        var cpu = NewCpu(out var bus);
        LoadCode(bus, 0x0000, 0x0000, 0xB9, 0xCD, 0xAB);
        cpu.Step();
        Assert.Equal(0xABCDu, cpu.GetRegister("CX"));
        Assert.Equal(0x03u, cpu.GetRegister("IP"));
    }

    [Fact]
    public void Mov_C6_imm_to_mem8()
    {
        // C6 06 00 30 5A = MOV byte [0x3000], 0x5A  (group reg=0, mod=00, r/m=110 disp16, imm8).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x6000);
        LoadCode(bus, 0x0000, 0x0000, 0xC6, 0x06, 0x00, 0x30, 0x5A);
        cpu.Step();
        Assert.Equal((byte)0x5A, bus.Read8((uint)((0x6000 << 4) + 0x3000)));
    }

    [Fact]
    public void Mov_C7_imm_to_mem16()
    {
        // C7 06 00 30 EF BE = MOV word [0x3000], 0xBEEF  (group reg=0, mod=00, r/m=110 disp16, imm16).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x6000);
        LoadCode(bus, 0x0000, 0x0000, 0xC7, 0x06, 0x00, 0x30, 0xEF, 0xBE);
        cpu.Step();
        uint dataPhys = (uint)((0x6000 << 4) + 0x3000);
        Assert.Equal((byte)0xEF, bus.Read8(dataPhys));
        Assert.Equal((byte)0xBE, bus.Read8(dataPhys + 1));
    }

    [Fact]
    public void Mov_8E_loads_a_segment_register_then_8C_stores_it()
    {
        // 8E D8 = MOV DS, AX  (mod=11, reg=011(DS via reg&3=3), r/m=000(AX)).  AX → DS.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("AX", 0x1357);
        LoadCode(bus, 0x0000, 0x0000, 0x8E, 0xD8);
        cpu.Step();
        Assert.Equal(0x1357u, cpu.GetRegister("DS"));

        // 8C C3 = MOV BX, ES  (mod=11, reg=000(ES), r/m=011(BX)).  ES → BX.
        var cpu2 = NewCpu(out var bus2);
        cpu2.SetRegister("ES", 0x2468);
        LoadCode(bus2, 0x0000, 0x0000, 0x8C, 0xC3);
        cpu2.Step();
        Assert.Equal(0x2468u, cpu2.GetRegister("BX"));
    }

    [Fact]
    public void Mov_A0_A1_accumulator_load_from_moffs()
    {
        // A1 00 10 = MOV AX, [0x1000]  (moffs16, default DS).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x7000);
        uint dataPhys = (uint)((0x7000 << 4) + 0x1000);
        bus.Write8(dataPhys, 0x21);
        bus.Write8(dataPhys + 1, 0x43);   // LE word 0x4321
        LoadCode(bus, 0x0000, 0x0000, 0xA1, 0x00, 0x10);
        cpu.Step();
        Assert.Equal(0x4321u, cpu.GetRegister("AX"));

        // A0 00 10 = MOV AL, [0x1000]  (moffs8).
        var cpuB = NewCpu(out var busB);
        cpuB.SetRegister("DS", 0x7000);
        busB.Write8((uint)((0x7000 << 4) + 0x1000), 0x99);
        LoadCode(busB, 0x0000, 0x0000, 0xA0, 0x00, 0x10);
        cpuB.Step();
        Assert.Equal(0x99u, cpuB.GetRegister("AL"));
    }

    [Fact]
    public void Mov_A2_A3_accumulator_store_to_moffs()
    {
        // A3 00 10 = MOV [0x1000], AX  (moffs16, default DS).
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x8000);
        cpu.SetRegister("AX", 0xC0DE);
        LoadCode(bus, 0x0000, 0x0000, 0xA3, 0x00, 0x10);
        cpu.Step();
        uint dataPhys = (uint)((0x8000 << 4) + 0x1000);
        Assert.Equal((byte)0xDE, bus.Read8(dataPhys));
        Assert.Equal((byte)0xC0, bus.Read8(dataPhys + 1));

        // A2 00 10 = MOV [0x1000], AL  (moffs8).
        var cpuB = NewCpu(out var busB);
        cpuB.SetRegister("DS", 0x8000);
        cpuB.SetRegister("AX", 0x00B5);   // AL=0xB5
        LoadCode(busB, 0x0000, 0x0000, 0xA2, 0x00, 0x10);
        cpuB.Step();
        Assert.Equal((byte)0xB5, busB.Read8((uint)((0x8000 << 4) + 0x1000)));
    }

    [Fact]
    public void Segment_override_prefix_changes_the_effective_segment()
    {
        // 26 A1 00 10 = ES: MOV AX, [0x1000]  — the ES override replaces the default DS for the moffs load.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x1000);   // the (wrong) default — must NOT be used
        cpu.SetRegister("ES", 0x9000);   // the override target
        uint dsPhys = (uint)((0x1000 << 4) + 0x1000);
        uint esPhys = (uint)((0x9000 << 4) + 0x1000);
        bus.Write8(dsPhys, 0x11); bus.Write8(dsPhys + 1, 0x11);     // DS data (decoy) = 0x1111
        bus.Write8(esPhys, 0x77); bus.Write8(esPhys + 1, 0x66);     // ES data = 0x6677
        LoadCode(bus, 0x0000, 0x0000, 0x26, 0xA1, 0x00, 0x10);
        cpu.Step();
        Assert.Equal(0x6677u, cpu.GetRegister("AX"));   // read from ES, not DS
        Assert.Equal(0x04u, cpu.GetRegister("IP"));     // 1 prefix + 1 opcode + 2 moffs
    }

    [Fact]
    public void Segment_override_on_a_modrm_memory_operand()
    {
        // 36 8B 1E 00 10 = SS: MOV BX, [0x1000]  — SS override on a disp16-direct (default DS) EA.
        var cpu = NewCpu(out var bus);
        cpu.SetRegister("DS", 0x1000);
        cpu.SetRegister("SS", 0xA000);
        uint ssPhys = (uint)((0xA000 << 4) + 0x1000);
        bus.Write8(ssPhys, 0x0D); bus.Write8(ssPhys + 1, 0xF0);     // SS data = 0xF00D
        LoadCode(bus, 0x0000, 0x0000, 0x36, 0x8B, 0x1E, 0x00, 0x10);
        cpu.Step();
        Assert.Equal(0xF00Du, cpu.GetRegister("BX"));
    }
}
