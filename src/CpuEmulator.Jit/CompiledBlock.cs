using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Jit;

/// <summary>How a compiled block returned control.</summary>
public enum BlockExit { Normal, Budget, Irq }

/// <summary>The emitted delegate shape. The DynamicMethod is created with this exact
/// signature; the dispatcher in JittedCpu.Run invokes it. 'cpu' is the wrapped interpreter
/// (emitted IL reads/writes its public fields via baked FieldInfo and calls its bus/Step on
/// the slow paths); 'bus' is the concrete AddressSpace (MMIO callouts go here); 'fastmem' is
/// the per-page classification (direct RAM/ROM access via PageBacking + PageOffset + the
/// writability needed for the store/dirty arm); 'dirty' carries the page-dirty marks for RAM
/// stores; 'budget' is decremented per instruction and drives the budget exit; 'exit' reports
/// why the block returned.
///
/// RECORDED DEVIATION from Ground truth D: the 'fastmem' parameter is the <see cref="Fastmem"/>
/// object, not a bare <c>byte[]?[]</c>. The emitted fastmem fast path needs the per-page
/// backing-array OFFSET (<c>backing[PageOffset[page] + (addr &amp; 0xFF)]</c> — boards map RAM/ROM
/// at non-zero starts, so the offset is generally non-zero) and per-page WRITABILITY (the
/// store/dirty arm), neither of which a bare backing array can carry. Passing the Fastmem object
/// (which exposes all three arrays) is the minimal faithful fix — directly analogous to the
/// plan's own choice to pass the <see cref="DirtyMap"/> class rather than a bare <c>bool[]</c>.</summary>
/// RECORDED DEVIATION (Task 6): the 'bus' parameter is typed <see cref="IAddressSpace"/>, not the
/// concrete <c>AddressSpace</c> Ground truth D names. The fastmem fast path never touches it (it
/// goes direct to the backing array); only the slow path (MMIO + every access under
/// <c>DisableFastmem</c>) calls <c>bus.Read8/Write8</c>, and both are <see cref="IAddressSpace"/>
/// members. Typing it as the interface lets the trace-equivalence mode route every callout through
/// a <c>TracingAddressSpace</c> (which implements <see cref="IAddressSpace"/> but does NOT derive
/// from <c>AddressSpace</c>) while fastmem classification still binds to the concrete
/// <c>AddressSpace</c> at construction. Production (fastmem-on) passes the concrete bus, which IS
/// an <see cref="IAddressSpace"/>.
public delegate void BlockDelegate(
    Mos6502Cpu cpu, IAddressSpace bus, Fastmem fastmem, DirtyMap dirty,
    ref long budget, out BlockExit exit);

/// <summary>A compiled block: the emitted delegate, the PC it is keyed on, and the set of
/// 256-byte pages its instruction bytes span (for dirty-page invalidation).</summary>
internal sealed class CompiledBlock(ushort entryPc, BlockDelegate del, IReadOnlyCollection<int> spannedPages)
{
    public ushort EntryPc { get; } = entryPc;
    public IReadOnlyCollection<int> SpannedPages { get; } = spannedPages;

    public void Run(
        Mos6502Cpu cpu, IAddressSpace bus, Fastmem fastmem, DirtyMap dirty,
        ref long budget, out BlockExit exit)
        => del(cpu, bus, fastmem, dirty, ref budget, out exit);
}
