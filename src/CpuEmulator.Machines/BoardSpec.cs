using CpuEmulator.Core;

namespace CpuEmulator.Machines;

/// <summary>A declarative description of an emulated computer: which CPU, the address width, the
/// memory map (RAM/ROM/MMIO), the peripheral slots, the IRQ wiring, and the reset inputs. Data, not
/// code (mirroring the CPU-spec philosophy): BoardSpecValidator checks it; BoardMachineFactory
/// instantiates it into the existing runnable Machine. AddressBits is the CPU's bus width (16 for
/// the 6502/Z80; 8-24 per AddressSpace). Endianness is the program-bus byte order — little-endian by
/// default (the 6502/Z80/8086 convention); a big-endian CPU (the 68000) declares BigEndian so its wide
/// bus reads — the reset vectors and the fetched instruction stream — are byte-ordered correctly.
/// IoAddressBits, when &gt; 0, declares a separate I/O PORT space of that width (the Z80 IN/OUT range
/// = 16) into which IoMmio regions + Io peripheral slots are mapped; 0 (the default) means the board
/// has no I/O space (every pre-Spectrum board). NominalClockHz, when set, is the board's documented guest
/// clock in Hz (e.g. ~1,020,500 for the Apple ][+, 3,500,000 for the ZX Spectrum); it is surfaced read-only
/// as Machine.NominalClockHz for the perf-overlay HUD's real-time ratio (null = the HUD omits the ratio).</summary>
public sealed record BoardSpec(
    string Name,
    CpuKind Cpu,
    int AddressBits,
    IReadOnlyList<MemoryRegion> Memory,
    IReadOnlyList<PeripheralSlot> Peripherals,
    IrqWiring Irq,
    ResetConfig Reset,
    Endianness Endianness = Endianness.LittleEndian,
    int IoAddressBits = 0,
    CoprocessorSpec? Coprocessor = null,
    double? NominalClockHz = null);
