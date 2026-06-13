using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Jit;

/// <summary>How a compiled block returned control.</summary>
public enum BlockExit
{
    /// <summary>Clean block end; cpu.PC holds the successor (static or dynamic).</summary>
    Normal,

    /// <summary>Budget &lt;= 0 at an instruction boundary; cpu.PC at the next instruction.</summary>
    Budget,

    /// <summary>(Reserved) interrupt sampled; the dispatcher services via inner.Step.</summary>
    Irq,

    /// <summary>NEW (M2-ii): the intra-block SMC guard tripped — the block self-modified one of its
    /// own pages; cpu.PC at the next instruction. The dispatcher MUST InvalidateIfDirty +
    /// re-decode before continuing. NEVER a chainable exit (Ground truth B).</summary>
    Recompile,
}

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
    ChainDispatch chain,         // 5th param (M2-ii): the chain-edge callback (stack-safe successor run)
    ref long budget, out BlockExit exit,
    IAddressSpace ioBus);        // 8th param (M3.2): the Io-bus IAddressSpace the Port emit arm calls
                                 // (Ground truth D — a SECOND, never-fastmem callout). APPENDED so no
                                 // existing arg index shifts; the 6502's emitted IL never references it
                                 // (no 6502 block contains a port op), so 6502 blocks are byte-identical.

/// <summary>The chain-edge callback the emitted block calls at a statically-known exit (M2-ii). Given
/// the (compile-time-constant) target PC, it arranges for the successor chain to run WITHOUT a
/// dispatcher round-trip, threading budget + exit. Implemented stack-safely as a LOOP in
/// <see cref="JittedCpu"/> (not emitted recursion — see the stack-safety note in
/// <c>BlockCompiler.EmitChainOrExit</c>), so a 96M-cycle Klaus chain does not blow the host stack.</summary>
public delegate void ChainDispatch(ushort targetPc, ref long budget, out BlockExit exit);

/// <summary>A compiled block: the emitted delegate, the PC it is keyed on, and the set of
/// 256-byte pages its instruction bytes span (for dirty-page invalidation).</summary>
internal sealed class CompiledBlock(ushort entryPc, BlockDelegate del, IReadOnlyCollection<int> spannedPages)
{
    public ushort EntryPc { get; } = entryPc;
    public IReadOnlyCollection<int> SpannedPages { get; } = spannedPages;

    /// <summary>Run the block. <paramref name="ioBus"/> (M3.2) is the Io-bus IAddressSpace the Port
    /// emit arm calls; it defaults to null because a CPU with no port op (the 6502) never references
    /// arg 7, so existing callers need not supply it (the byte-identical-6502 invariant: existing
    /// JIT tests are unchanged). A port-using CPU's dispatcher passes its real Io bus.</summary>
    public void Run(
        Mos6502Cpu cpu, IAddressSpace bus, Fastmem fastmem, DirtyMap dirty,
        ChainDispatch chain, ref long budget, out BlockExit exit, IAddressSpace? ioBus = null)
        => del(cpu, bus, fastmem, dirty, chain, ref budget, out exit, ioBus!);
}
