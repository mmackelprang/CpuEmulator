using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Machines;

/// <summary>Builds a runnable ZX Spectrum <see cref="Machine"/> and the ULA wired to its program space.
/// The ULA must read the BUILT machine's RAM, so we build the machine first with a ULA over a deferred
/// space, then re-point: in practice the Machine's program space is the same object the ULA reads, so
/// we construct the ULA over a freshly-created program AddressSpace that the BoardSpec then adopts.
/// Because BoardMachineFactory creates its OWN program AddressSpace, the supported pattern is: build the
/// machine, then construct the ULA over machine.Space(Program), then the surface uses that ULA for
/// display/keyboard/audio AND the board's Io slot. To keep the ULA the SAME instance the Io slot maps,
/// we build in one shot here by creating the ULA over a placeholder and swapping its RAM handle.</summary>
public static class SpectrumMachine
{
    public static Machine Build(byte[] rom, out SpectrumUla ula, ExecutionTier tier = ExecutionTier.Interpreter)
    {
        // The ULA needs the machine's program space. BoardMachineFactory builds that space internally and
        // realizes the ULA with the IMachineContext — so the ULA reads RAM via an IAddressSpace it is GIVEN
        // at Realize time, not at construction. Refactor: the ULA captures the program space in Realize.
        var pendingUla = new SpectrumUla(); // parameterless: RAM bound in Realize
        BoardSpec spec = SpectrumBoard.Spec(rom, pendingUla);
        Machine machine = BoardMachineFactory.Build(spec, tier);
        ula = pendingUla;
        return machine;
    }
}
