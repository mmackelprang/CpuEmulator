using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>
/// The SP0 demo computer, expressed as a declarative <see cref="BoardSpec"/> (mirroring
/// <see cref="Breadboard6502Board"/>): a 6502 with RAM low, a memory-mapped framebuffer + keyboard
/// + disk, and the demo ROM high. This replaces the SP0 design's hand-wired "DemoMachine" — the
/// board model (shipped 2026-06-19) is now the one way to compose a machine. The web surface
/// (CpuEmulator.Surface.Web) drives the built Machine through a MachineHost; the monitor host
/// (CpuEmulator.Host) could boot the very same spec — two surfaces, one machine.
/// <para>Map: RAM $0000-$7FFF; framebuffer VRAM $8000-$BFFF (16 KiB window); MMIO $C000-$DFFF
/// holding the keyboard ($D000) + disk ($D100) slots; ROM $E000-$FFFF. Only the keyboard is
/// IRQ-wired (the disk is polled; the framebuffer's FrameReady is a host event, not a guest IRQ).</para>
/// </summary>
public static class DemoBoard
{
    public const uint RamBase = 0x0000;
    public const uint RamLength = 0x8000;        // $0000-$7FFF (32 KiB)
    public const uint FramebufferBase = 0x8000;  // VRAM window
    public const uint FramebufferLength = 0x4000; // $8000-$BFFF (16 KiB reachable VRAM)
    public const uint MmioBase = 0xC000;         // $C000-$DFFF device block
    public const uint MmioLength = 0x2000;
    public const uint KeyboardBase = 0xD000;
    public const uint DiskBase = 0xD100;
    public const uint RomBase = 0xE000;
    public const uint RomLength = 0x2000;        // $E000-$FFFF (8 KiB)

    /// <summary>Build the demo board-spec over a ROM image and the three device instances (so the
    /// caller — the surface or a test — keeps handles to RenderInto / PostKey / the disk).</summary>
    public static BoardSpec Spec(byte[] rom, DemoFramebuffer framebuffer, DemoKeyboard keyboard, DemoDisk disk) =>
        new("demo", CpuKind.Mos6502, AddressBits: 16,
            Memory:
            [
                new MemoryRegion(RamBase, RamLength, RegionKind.Ram),
                new MemoryRegion(FramebufferBase, FramebufferLength, RegionKind.Mmio), // VRAM slot hole
                new MemoryRegion(MmioBase, MmioLength, RegionKind.Mmio),               // device slots hole
                new MemoryRegion(RomBase, RomLength, RegionKind.Rom, rom),
            ],
            Peripherals:
            [
                new PeripheralSlot("framebuffer", framebuffer, FramebufferBase, FramebufferLength),
                new PeripheralSlot("keyboard", keyboard, KeyboardBase, 0x0100),
                new PeripheralSlot("disk", disk, DiskBase, 0x0100),
            ],
            Irq: new IrqWiring(
            [
                new PeripheralIrq("keyboard", CpuInterrupt.Irq),
            ]),
            Reset: ResetConfig.None); // the demo ROM image carries its own $FFFC reset vector.
}
