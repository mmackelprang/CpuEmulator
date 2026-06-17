using CpuEmulator.Core;

namespace CpuEmulator.Cpus.M8086;

/// <summary>The MINIMAL hand-written half of the 8086 (M5.1) — the bus field and the inert policy hooks
/// the generated partial requires. This is the STATE FOUNDATION: it makes the generated register file
/// compile and proves the register model synthetically (the AX/AH/AL partial-write hazard via the
/// generated pair-views, and the 20-bit little-endian bus round-trip up to 0xFFFFF).
///
/// It is NOT an interpreter. There is NO decode, NO effective-address calculation, NO segmentation
/// resolution (the seg&lt;&lt;4 + offset → 20-bit physical address), NO op body, and NO interrupt
/// machinery — those are M5.2–M5.6. The instruction table is empty (mirroring the 68000's M4.1
/// foundation), so M5.1 never meaningfully calls Step: any byte would route to
/// <see cref="HandleUndefinedOpcode"/>. The interrupt hooks below are inert — the real interrupt seam
/// (the maskable IRQ line gated by IF, the NMI vector, and the INT/INTO/divide vectors) is M5.5d.</summary>
public sealed partial class M8086Cpu
{
    /// <summary>The single program/data bus (the 8086 is von Neumann — code and data share the address
    /// space; the separate I/O port space is a later concern). The host/runner constructs it as
    /// <c>new AddressSpace(AddressSpaceKind.Program, addressBits: 20)</c> — the little-endian default
    /// (no BigEndian, no alignment enforcement), per ADR 0005 Decision 2. M5.1 wires only the byte path
    /// (Read8/Write8 via <see cref="ReadBus"/>/<see cref="WriteBus"/>); the wide LE Read16/Write16 the
    /// segment layer needs are M5.3.</summary>
    private readonly IAddressSpace _bus;

    public M8086Cpu(IAddressSpace bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <summary>Reset — M5.1 stub. The real 8086 reset jams CS:IP to F000:FFF0 (and clears the rest of
    /// the state) per ADR 0005 Decision 4; that is M5.5d. The M5.1 state tests set registers explicitly,
    /// so this does nothing yet.</summary>
    public void Reset() { }

    // ── The inert policy hooks the generated partial requires. No GENERATED caller asserts these in the
    //    M5.1 path (the table is empty); they exist to satisfy the partial's contract. The real interrupt
    //    model — the maskable IRQ line gated by the IF flag, and the non-maskable NMI (vector 2) — is M5.5d.

    /// <summary>Assert/de-assert the maskable IRQ line — M5.1 stub (the IF-gated INTR servicing is M5.5d).</summary>
    public void SetIrqLine(bool asserted) { }

    /// <summary>Assert/de-assert the non-maskable NMI line — M5.1 stub (NMI vector 2 is M5.5d).</summary>
    public void SetNmiLine(bool asserted) { }

    /// <summary>Program/data-bus byte read; charges one cycle (the cycle invariant). The wide
    /// little-endian Read16 the segment layer needs is M5.3 — this byte path keeps the generated Step
    /// compiling.</summary>
    private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }

    /// <summary>Program/data-bus byte write; charges one cycle. The wide LE Write16 is M5.3.</summary>
    private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }

    /// <summary>Undefined-opcode hook — M5.1 stub. The instruction table is empty, so any Step routes
    /// here; M5.1 never meaningfully calls Step. The 8086's real undefined-opcode behavior (it has no
    /// #UD trap; only the 80186+ added one) is settled with the decode/exec arms (M5.2+).</summary>
    private void HandleUndefinedOpcode(byte opcode) { _cycles++; }

    /// <summary>M5.1: no interrupt machinery — nothing is ever serviced. The real seam (the IF-gated
    /// INTR/NMI acknowledge that pushes FLAGS:CS:IP and vectors through the IVT) is M5.5d.</summary>
    private partial bool TryServiceInterrupt() => false;

    /// <summary>M5.1: nothing is ever pending. The real predicate (an asserted NMI, or an asserted INTR
    /// while IF is set) is M5.5d.</summary>
    public partial bool InterruptPending => false;
}
