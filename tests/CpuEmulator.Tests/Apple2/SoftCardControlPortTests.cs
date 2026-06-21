using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardControlPortTests
{
    // A minimal ICoprocessorControl spy: records the last SetCoprocessorActive value + the call count.
    private sealed class ControlSpy : IMachineContext, ICoprocessorControl
    {
        public bool? LastActive { get; private set; }
        public int Calls { get; private set; }
        public void SetCoprocessorActive(bool active) { LastActive = active; Calls++; }

        // IMachineContext members are unused by the control port's Realize (it only needs the cast).
        public IScheduler Scheduler => throw new NotSupportedException();
        public IAddressSpace Space(AddressSpaceKind kind) => throw new NotSupportedException();
        public IInterruptLine IrqLine => throw new NotSupportedException();
        public IInterruptLine NmiLine => throw new NotSupportedException();
    }

    [Fact]
    public void A_write_flips_the_active_cpu_via_the_coprocessor_control()
    {
        var spy = new ControlSpy();
        var port = new SoftCardControlPort();
        port.Realize(spy);

        port.Write(0x00, AccessWidth.Byte, 0x00);   // first $CN00 write: hand off to the coprocessor
        Assert.Equal(true, spy.LastActive);
        Assert.Equal(1, spy.Calls);

        port.Write(0x00, AccessWidth.Byte, 0x00);   // the matching write: hand back to the primary
        Assert.Equal(false, spy.LastActive);
        Assert.Equal(2, spy.Calls);
    }

    [Fact]
    public void TryPeek_is_side_effect_free_and_does_not_switch_cpus()
    {
        var spy = new ControlSpy();
        var port = new SoftCardControlPort();
        port.Realize(spy);

        bool ok = port.TryPeek(0x00, out byte v);
        Assert.True(ok);
        Assert.Equal(0x00, v);            // open-bus, side-effect-free
        Assert.Equal(0, spy.Calls);       // a debugger peek did NOT toggle the active CPU
    }

    [Fact]
    public void On_a_non_coprocessor_context_the_port_is_inert()
    {
        var port = new SoftCardControlPort();
        // Realize with a context that is NOT an ICoprocessorControl: the cast fails, _ctl stays null.
        port.Realize(new PlainContext());
        port.Write(0x00, AccessWidth.Byte, 0x00);   // must not throw (degrades gracefully)
    }

    private sealed class PlainContext : IMachineContext
    {
        public IScheduler Scheduler => throw new NotSupportedException();
        public IAddressSpace Space(AddressSpaceKind kind) => throw new NotSupportedException();
        public IInterruptLine IrqLine => throw new NotSupportedException();
        public IInterruptLine NmiLine => throw new NotSupportedException();
    }

    [Fact]
    public void Real_Z80_runs_translated_against_shared_6502_RAM_after_the_control_port_handoff()
    {
        // --- 6502 system ROM at $D000-$FFFF: write the control port at $C200 (hand off to the Z80), spin.
        var rom = new byte[0x3000];
        // $D000: 8D 00 C2   STA $C200   (write the SoftCard control port -> hand off to the Z80)
        // $D003: 4C 03 D0   JMP $D003   (spin; the 6502 is now DMA-suspended)
        rom[0x0000] = 0x8D; rom[0x0001] = 0x00; rom[0x0002] = 0xC2;
        rom[0x0003] = 0x4C; rom[0x0004] = 0x03; rom[0x0005] = 0xD0;
        rom[0x2FFC] = 0x00; rom[0x2FFD] = 0xD0;   // reset -> $D000

        var translation = new SoftCardTranslation();
        var control = new SoftCardControlPort();
        var spec = new BoardSpec("apple2-softcard-test", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(0x0000, 0xC000, RegionKind.Ram),                 // $0000-$BFFF shared RAM
                new MemoryRegion(0xC000, 0x0200, RegionKind.Mmio),                // $C000-$C1FF I/O band
                new MemoryRegion(0xC200, 0x0100, RegionKind.Mmio),               // $C200 control-port page
                new MemoryRegion(0xC300, 0x0D00, RegionKind.Mmio),               // rest of the I/O band
                new MemoryRegion(0xD000, 0x3000, RegionKind.Rom, rom),            // $D000-$FFFF ROM
            ],
            Peripherals: [ new PeripheralSlot("softcard", control, 0xC200, 0x0100) ],
            Irq: IrqWiring.None,
            Reset: ResetConfig.None,
            Coprocessor: new CoprocessorSpec(
                CpuKind.Z80, translation, "softcard", ClockRatioToPrimary: 2.0));

        var machine = BoardMachineFactory.Build(spec);   // interpreter tier (coprocessor always interpreter)

        // Pre-load the Z80 routine into shared RAM at PHYSICAL $1000 (= Z80 logical $0000, branch 1).
        var bus = machine.Space(AddressSpaceKind.Program);
        byte[] z80Routine = [0x3E, 0x42, 0x32, 0x00, 0xF0, 0x18, 0xFE]; // LD A,$42; LD ($F000),A; JR -2
        for (uint i = 0; i < z80Routine.Length; i++)
            bus.Write8(0x1000 + i, z80Routine[i]);

        machine.Reset();
        Assert.False(machine.CoprocessorActive);          // the 6502 starts active

        // 1) Run the 6502 until it writes $C200 and hands off.
        machine.Run(100);
        Assert.True(machine.CoprocessorActive);           // the $CnXX write flipped the active CPU
        long six502Cycles = machine.Cpu.CycleCount;

        // 2) The Z80 resets to PC=0; run it. It fetches from physical $1000 (Z80 $0000), runs the routine,
        //    and writes $42 to Z80 $F000 -> physical $0000.
        machine.Coprocessor!.Reset();                      // Z80 reset: PC=0
        machine.Run(200);

        // The Z80 ran THROUGH the translation against the SHARED RAM: the 6502 reads physical $0000.
        Assert.Equal(0x42, bus.Read8(0x0000));
        // The suspended 6502 did NOT advance while the Z80 was the bus master.
        Assert.Equal(six502Cycles, machine.Cpu.CycleCount);
    }
}
