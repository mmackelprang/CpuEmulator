using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

public class RemapInvalidationTests
{
    // A 16-bit 6502 bus: zero page RAM at $0000, a banked window at $D000, reset vector ROM at $FFFC/$FFFD.
    private static (JittedCpu<Mos6502Cpu> cpu, AddressSpace bus) BuildJit(byte[] bankAtD000)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, 16);
        bus.MapMemory(0x0000, new byte[0x0100], writable: true);   // zero page
        bus.MapMemory(0xD000, bankAtD000, writable: false);        // the banked code window (1 page)
        var vec = new byte[0x0100];                                // $FF00-$FFFF; reset vector at $FFFC/$FFFD
        vec[0xFC] = 0x00; vec[0xFD] = 0xD0;                        // RESET -> $D000
        bus.MapMemory(0xFF00, vec, writable: false);
        var inner = new Mos6502Cpu(bus);
        var cpu = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, bus);
        cpu.Reset();                                               // PC <- $D000
        return (cpu, bus);
    }

    // A one-page $D000 bank: "LDA #imm ; STA $0010 ; JMP self" so the value stored at $10 identifies
    // which bank ran. imm is the un-fakeable discriminator.
    private static byte[] BankStoring(byte imm)
    {
        var page = new byte[0x0100];
        page[0x00] = 0xA9; page[0x01] = imm;        // LDA #imm
        page[0x02] = 0x85; page[0x03] = 0x10;       // STA $10
        page[0x04] = 0x4C; page[0x05] = 0x04; page[0x06] = 0xD0; // JMP $D004 (tight self-loop)
        return page;
    }

    [Fact]
    public void A_remapped_code_page_runs_the_new_bank_after_remap()
    {
        var (cpu, bus) = BuildJit(BankStoring(0xAA));

        long budget = 50;
        cpu.Run(ref budget);                         // compiles + runs the $D000 block -> $10 = 0xAA
        Assert.Equal(0xAA, bus.Read8(0x0010));

        // Bank in a DIFFERENT $D000 page (different immediate). Remap fires OnRemap -> fastmem
        // re-class + the old $D000 block is evicted.
        bus.Remap(0xD000, BankStoring(0xBB), writable: false);
        cpu.SetRegister("PC", 0xD000);               // re-enter the (now remapped) window
        budget = 50;
        cpu.Run(ref budget);                         // MUST recompile from the new bank -> $10 = 0xBB
        Assert.Equal(0xBB, bus.Read8(0x0010));
    }

    [Fact]
    public void Remap_to_a_writable_ram_bank_is_seen_by_the_fast_path()
    {
        // Bank $D000 in, write a value via the bus, remap to a second array, and confirm a fast-path
        // read through the JIT sees the SECOND array's bytes (the fastmem re-class worked).
        var first = BankStoring(0x11);
        var (cpu, bus) = BuildJit(first);
        long budget = 50; cpu.Run(ref budget);
        Assert.Equal(0x11, bus.Read8(0x0010));

        var second = BankStoring(0x22);
        bus.Remap(0xD000, second, writable: false);
        cpu.SetRegister("PC", 0xD000);
        budget = 50; cpu.Run(ref budget);
        Assert.Equal(0x22, bus.Read8(0x0010));       // the new array's immediate ran
    }
}
