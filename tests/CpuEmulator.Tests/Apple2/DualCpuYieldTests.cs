using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Apple2;

public class DualCpuYieldTests
{
    // Build a minimal dual-CPU machine: 6502 primary + Z80 coprocessor over a SoftCardTranslation, with a
    // SoftCardControlPort at $C200. Mirrors SoftCardControlPortTests' synthetic board.
    private static (Machine machine, IAddressSpace bus) BuildDualCpu(byte[] rom)
    {
        var translation = new SoftCardTranslation();
        var control = new SoftCardControlPort();
        var spec = new BoardSpec("apple2-softcard-yield-test", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xC000, RegionKind.Ram),
                new MemoryRegion(0xC000, 0x0200, RegionKind.Mmio),
                new MemoryRegion(0xC200, 0x0100, RegionKind.Mmio),
                new MemoryRegion(0xC300, 0x0D00, RegionKind.Mmio),
                new MemoryRegion(0xD000, 0x3000, RegionKind.Rom, rom),
            ],
            Peripherals: [ new PeripheralSlot("softcard", control, 0xC200, 0x0100) ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            Coprocessor: new CoprocessorSpec(
                CpuKind.Z80, translation, "softcard", ClockRatioToPrimary: 2.0));

        var machine = BoardMachineFactory.Build(spec);
        return (machine, machine.Space(AddressSpaceKind.Program));
    }

    [Fact]
    public void The_active_core_yields_at_the_control_port_write_not_after_the_slice_budget()
    {
        // 6502 ROM at $D000:
        //   $D000: 8D 00 C2   STA $C200   ; write the control port -> hand off to the Z80 (yield HERE)
        //   $D003: A9 5A      LDA #$5A
        //   $D005: 8D 00 02   STA $0200   ; sentinel: must NOT execute until control returns to the 6502
        //   $D008: 4C 08 D0   JMP $D008   ; spin
        var rom = new byte[0x3000];
        rom[0x0000] = 0x8D; rom[0x0001] = 0x00; rom[0x0002] = 0xC2;
        rom[0x0003] = 0xA9; rom[0x0004] = 0x5A;
        rom[0x0005] = 0x8D; rom[0x0006] = 0x00; rom[0x0007] = 0x02;
        rom[0x0008] = 0x4C; rom[0x0009] = 0x08; rom[0x000A] = 0xD0;
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000

        var (machine, bus) = BuildDualCpu(rom);

        // Pre-load a Z80 routine at PHYSICAL $1000 (= Z80 logical $0000, branch 1) that writes a DIFFERENT
        // sentinel to Z80 $F000 (= physical $0000) and spins.
        //   3E 99  LD A,$99 ; 32 00 F0  LD ($F000),A ; 18 FE  JR -2
        byte[] z80 = [0x3E, 0x99, 0x32, 0x00, 0xF0, 0x18, 0xFE];
        for (uint i = 0; i < z80.Length; i++) bus.Write8(0x1000 + i, z80[i]);

        machine.Reset();
        Assert.False(machine.CoprocessorActive);

        // Run a SMALL budget -- enough for the 6502 to reach + execute the STA $C200, but the per-instruction
        // yield must stop the 6502 at that write (so the $5A sentinel at $0200 stays UNWRITTEN this slice).
        machine.Run(20);

        Assert.True(machine.CoprocessorActive,
            "the $C200 write must have flipped the active CPU to the Z80");
        Assert.NotEqual(0x5A, bus.Read8(0x0200));
        // ^ With the OLD whole-slice Run, the 6502 ran past its toggle and wrote $5A before yielding.
        //   With the per-instruction yield, the $C200 write is the LAST 6502 instruction of the slice.

        // Now let the Z80 run: it writes $99 to physical $0000. The 6502 is DMA-suspended (no $5A yet).
        machine.Coprocessor!.Reset();   // Z80 PC=0
        machine.Run(50);
        Assert.Equal(0x99, bus.Read8(0x0000));
        Assert.NotEqual(0x5A, bus.Read8(0x0200));   // the suspended 6502 still has not written its sentinel
    }
}
