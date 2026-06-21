using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using Xunit;

namespace CpuEmulator.Tests.Spectrum;

/// <summary>Un-fakeable regression guard that the 50 Hz IM1 maskable interrupt is actually DELIVERED to
/// the guest. The 48K ROM's IM1 ISR increments the 24-bit frame counter FRAMES at $5C78 (3 bytes,
/// little-endian) once per frame. We boot the canonical ROM, read FRAMES, run ~50 more frames, and assert
/// FRAMES advanced by about that many. Before the /INT-hold fix the line was pulsed assert+release inside
/// one frame-tick callback so the level-sampled INT was never serviced → this delta would be exactly 0.
/// Skips-with-note when the canonical ROM is absent.</summary>
[Trait("Category", "UAT")]
public class SpectrumInterruptTests
{
    private const long CyclesPerFrame = SpectrumUla.TStatesPerFrame; // 69888
    private const long BootCycles = 7_000_000;                       // past the RAM test; ISR + main loop running
    private const uint FramesAddr = 0x5C78;                          // 24-bit FRAMES counter (3 bytes LE)

    [SpectrumRomTheory]   // skips-with-note when 48.rom (canonical) is absent
    [InlineData(ExecutionTier.Interpreter)]
    [InlineData(ExecutionTier.Jit)]
    public void Fifty_hertz_IM1_increments_FRAMES(ExecutionTier tier)
    {
        byte[] rom = SpectrumRom.Load(SpectrumRomVectors.TryGetRomPath());
        Machine machine = SpectrumMachine.Build(rom, out _, tier);
        machine.Reset();

        // Boot past the power-on RAM test; once the main loop + IM1 ISR are running, FRAMES increments.
        machine.Run(BootCycles);
        IAddressSpace ram = machine.Space(AddressSpaceKind.Program);
        uint framesBefore = ReadFrames(ram);

        // Run 50 more frames; the IM1 ISR should tick FRAMES once per frame.
        const int extraFrames = 50;
        machine.Run(CyclesPerFrame * extraFrames);
        uint framesAfter = ReadFrames(ram);

        // Directly proves the IM1 is being serviced every frame. A generous lower bound (>=40 of 50) stays
        // robust to a frame or two of slack while still being impossible to pass if the IRQ is not delivered
        // (the pre-fix delta was 0).
        uint delta = framesAfter - framesBefore;
        Assert.True(delta >= 40,
            $"[{tier}] expected FRAMES ($5C78) to advance ~{extraFrames} over {extraFrames} frames " +
            $"(50 Hz IM1 delivered); got delta={delta} (before={framesBefore}, after={framesAfter})");
    }

    private static uint ReadFrames(IAddressSpace ram) =>
        ram.Read8(FramesAddr)
        | (uint)(ram.Read8(FramesAddr + 1) << 8)
        | (uint)(ram.Read8(FramesAddr + 2) << 16);
}
