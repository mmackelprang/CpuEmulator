using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M4.5c synthetic execute tests for the data-movement misc family (Tasks 16-20): SWAP/MOVEQ/EXG/LEA/PEA/TAS/
/// MOVEM/MOVEP. No vectors; the TomHarte sweep (Task 22) is the oracle. These pin the wiring + the trickier
/// MOVEM mask order / MOVEP byte-lane in isolation.
/// </summary>
public class M68000SystemMiscExecuteTests
{
    private static (M68000Cpu Cpu, AddressSpace Bus) Build(params (uint Addr, byte Val)[] mem)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        foreach (var (a, v) in mem) bus.Write8(a, v);
        return (new M68000Cpu(bus), bus);
    }

    // ── Task 16: SWAP + MOVEQ ───────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Swap_exchanges_halves_and_sets_NZ()
    {
        // SWAP D0 = 0x4840.
        var (cpu, _) = Build((0x1000, 0x48), (0x1001, 0x40));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x12345678);
        cpu.SetRegister("SR", 0x0011);   // X + C going in; X kept, C cleared by Logic
        cpu.Step();
        Assert.Equal(0x56781234u, (uint)cpu.GetRegister("D0"));
        uint sr = (uint)cpu.GetRegister("SR") & 0x1F;
        Assert.Equal(0x10u, sr & 0x10);   // X kept
        Assert.Equal(0x00u, sr & 0x03);   // V=C=0
    }

    [Fact]
    public void Moveq_sign_extends_imm8()
    {
        // MOVEQ #-1,D0 = 0x70FF. 0111 ddd=000 0 dddddddd=0xFF.
        var (cpu, _) = Build((0x1000, 0x70), (0x1001, 0xFF));
        cpu.SetRegister("PC", 0x1000);
        cpu.Step();
        Assert.Equal(0xFFFFFFFFu, (uint)cpu.GetRegister("D0"));   // sign-extended
        Assert.Equal(0x08u, (uint)cpu.GetRegister("SR") & 0x08); // N set
    }

    // ── Task 17: EXG ────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Exg_data_data()
    {
        // EXG D0,D1 = 0xC141. 1100 rx=000 1 01000 ry=001.
        var (cpu, _) = Build((0x1000, 0xC1), (0x1001, 0x41));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0xAAAAAAAA);
        cpu.SetRegister("D1", 0xBBBBBBBB);
        cpu.Step();
        Assert.Equal(0xBBBBBBBBu, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0xAAAAAAAAu, (uint)cpu.GetRegister("D1"));
    }

    [Fact]
    public void Exg_data_addr()
    {
        // EXG D0,A1 = 0xC189. 1100 rx=000 1 10001 ry=001. Rx=Dn(D0), Ry=An(A1).
        var (cpu, _) = Build((0x1000, 0xC1), (0x1001, 0x89));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0x11111111);
        cpu.SetRegister("A1", 0x22222222);
        cpu.Step();
        Assert.Equal(0x22222222u, (uint)cpu.GetRegister("D0"));
        Assert.Equal(0x11111111u, (uint)cpu.GetRegister("A1"));
    }

    // ── Task 18: LEA + PEA ──────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Lea_loads_effective_address_into_An()
    {
        // LEA (d16,A0),A1 = 0x43E8, disp 0x0010. 0100 an=001 111 eaMode=101 eaReg=000.
        var (cpu, _) = Build((0x1000, 0x43), (0x1001, 0xE8), (0x1002, 0x00), (0x1003, 0x10));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.Step();
        Assert.Equal(0x2010u, (uint)cpu.GetRegister("A1"));   // A0 + 0x10, no fetch
    }

    [Fact]
    public void Pea_pushes_effective_address()
    {
        // PEA (d16,A0) = 0x4868, disp 0x0010. 0100 1000 01 eaMode=101 eaReg=000.
        var (cpu, bus) = Build((0x1000, 0x48), (0x1001, 0x68), (0x1002, 0x00), (0x1003, 0x10));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.SetRegister("USP", 0x8000);
        cpu.Step();
        Assert.Equal(0x7FFCu, (uint)cpu.GetRegister("USP"));   // -(A7) by 4
        Assert.Equal(0x2010u, bus.Read32(0x7FFC));            // long pushed BE
    }

    // ── Task 19: TAS ────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Tas_sets_NZ_then_sets_bit7()
    {
        // TAS (A0) = 0x4AD0. 0100 1010 11 eaMode=010 eaReg=000.
        var (cpu, bus) = Build((0x1000, 0x4A), (0x1001, 0xD0), (0x2000, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.Step();
        Assert.Equal((byte)0x80, bus.Read8(0x2000));          // bit 7 set
        Assert.Equal(0x04u, (uint)cpu.GetRegister("SR") & 0x04);   // Z (original was 0)
    }

    // ── Task 20: MOVEM ──────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Movem_l_predecrement_store_reversed_mask()
    {
        // MOVEM.l D0/D1,-(A7) = 0x48E0, mask. dr=0(regs->mem) sz=1(.l) eaMode=100 eaReg=111(A7).
        // -(An) mask is REVERSED: bit0=A7..bit15=D0. To store D0+D1 the mask bits are: D0=bit15, D1=bit14.
        // mask = 0xC000.
        var (cpu, bus) = Build((0x1000, 0x48), (0x1001, 0xE7), (0x1002, 0xC0), (0x1003, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("USP", 0x8000);
        cpu.SetRegister("D0", 0x11111111);
        cpu.SetRegister("D1", 0x22222222);
        cpu.Step();
        Assert.Equal(0x7FF8u, (uint)cpu.GetRegister("USP"));   // 2 longs pushed = -8
        // Predecrement order: first stored (bit0 side) = D1 at the higher addr, then D0 below it.
        Assert.Equal(0x22222222u, bus.Read32(0x7FFC));        // D1 (stored first, higher)
        Assert.Equal(0x11111111u, bus.Read32(0x7FF8));        // D0 (stored last, lower)
    }

    [Fact]
    public void Movem_w_postincrement_load_sign_extends()
    {
        // MOVEM.w (A0)+,D0 = 0x4C98, mask 0x0001 (D0=bit0 forward). dr=1(mem->regs) sz=0(.w) eaMode=011 eaReg=000.
        var (cpu, bus) = Build((0x1000, 0x4C), (0x1001, 0x98), (0x1002, 0x00), (0x1003, 0x01));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        bus.Write16(0x2000, 0x8001);   // .w 0x8001 sign-extends to 0xFFFF8001
        cpu.Step();
        Assert.Equal(0xFFFF8001u, (uint)cpu.GetRegister("D0"));   // sign-extended
        Assert.Equal(0x2002u, (uint)cpu.GetRegister("A0"));        // (A0)+ advanced
    }

    // ── Task 20 Step 2b: MOVEP ──────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Movep_decode_captures_displacement_word()
    {
        // MOVEP.w d16(A0),D0 = 0x0108, disp 0x0000. 0000 ddd=000 1 dr=0(mem->reg) sz=0(.w) 001 aaa=000.
        var buf = new byte[] { 0x01, 0x08, 0x00, 0x00, 0, 0 };
        var stream = new BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        DecodeResult r = M68000Cpu.Decode(stream);
        Assert.NotEqual(0xFFFFFFFFu, r.OperationKey);
        Assert.True(r.ExtensionWords.Count >= 1, "the MOVEP displacement word must be captured");
        Assert.Equal(4, r.Length);    // operword + disp word
    }

    [Fact]
    public void Movep_w_reg_to_mem_byte_lanes()
    {
        // MOVEP.w D0,d16(A0) = 0x0188, disp 0x0000. dr=1(reg->mem) sz=0(.w). bytes land on every other address.
        var (cpu, bus) = Build((0x1000, 0x01), (0x1001, 0x88), (0x1002, 0x00), (0x1003, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.SetRegister("D0", 0x0000ABCD);   // .w lanes: AB at base, CD at base+2
        cpu.Step();
        Assert.Equal((byte)0xAB, bus.Read8(0x2000));
        Assert.Equal((byte)0xCD, bus.Read8(0x2002));
    }

    [Fact]
    public void Movep_w_mem_to_reg_byte_lanes()
    {
        // MOVEP.w d16(A0),D0 = 0x0108, disp 0x0000. dr=0(mem->reg) sz=0(.w).
        var (cpu, _) = Build((0x1000, 0x01), (0x1001, 0x08), (0x1002, 0x00), (0x1003, 0x00),
                             (0x2000, 0xAB), (0x2002, 0xCD));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("A0", 0x2000);
        cpu.SetRegister("D0", 0x12340000);
        cpu.Step();
        Assert.Equal(0x1234ABCDu, (uint)cpu.GetRegister("D0"));   // .w writes the low word
    }
}
