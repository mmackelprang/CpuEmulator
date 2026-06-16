namespace CpuEmulator.Core.Jit;

/// <summary>
/// The live 68000 instruction fetch stream — a STATEFUL 2-word prefetch queue the CPU owns across Steps
/// (M4.5d-2a, ADR 0008 §5). Word-granular (UnitBytes == 2), big-endian, over a uint-wide formal PC. The
/// generated word-granular Decode(IFetchStream) walk consumes the operword + extension words through it
/// unchanged (same IFetchStream contract — the change is statefulness + the refill, not the walk API).
///
/// <para><b>The queue model (reverse-engineered empirically against the 680x0/v1 corpus — ADR 0008 §1.2,
/// §8.1).</b> The real 68000 keeps two prefetched words: <c>q0</c> (the word being executed-from) and
/// <c>q1</c> (the next word). The FORMAL PC is the address of <c>q0</c>; the physical fetch FRONTIER (the
/// address of the next refill read) trails it by the 2-word queue depth, i.e. <c>frontier == formalPc +
/// 4</c>. Each <see cref="NextUnit"/> returns <c>q0</c>, shifts <c>q1 → q0</c>, refills <c>q1</c> with one
/// fresh word read from the bus at the frontier, and advances both the frontier and the formal PC by one
/// word. So after consuming N stream words sequentially the queue holds <c>[word@(pc+2N), word@(pc+2N+2)]</c>
/// and the formal PC is <c>pc + 2N</c> — exactly the corpus's <c>final.pc</c>/<c>final.prefetch</c> end
/// state for a non-branching instruction.</para>
///
/// <para><b>Branches/jumps/returns/exceptions</b> set the CPU's PC non-sequentially. The queue is then
/// RESEEDED from the new PC (<see cref="Reseed"/>) so the end state is <c>[word@newPc, word@(newPc+2)]</c> —
/// matching the corpus's taken-branch prefetch (two fresh reads from the target). The CPU applies the reseed
/// at the end of Step iff the live PC diverged from <see cref="FormalPc"/>.</para>
///
/// <para><b>Queue END STATE (PC/prefetch-exact).</b> This models the queue END STATE — the two
/// <see cref="FinalPrefetch"/> words + the trailing <see cref="FormalPc"/> — which the 2a sweep asserts.</para>
///
/// <para><b>2b cycle/trace machinery.</b> Each <see cref="NextUnit"/> refill is a REAL 4-clock word bus cycle
/// that the tracing bus records; <see cref="RefillCount"/> counts those per-instruction refills so the generated
/// Step can charge <c>4 * RefillCount</c> for the prefetch portion of the cycle total. The corpus-given seed
/// words are NOT re-fetched (they are already in the queue), so <see cref="SeedPeek"/> primes the queue via the
/// untraced inner-memory peek — keeping the seed reads out of the per-transaction trace. The remaining
/// idle/operand cycles to reach <c>length</c> are charged by the CPU's idle primitive + the op-body bus helpers
/// (the per-instruction-class reconciliation against <c>transactions</c> is staged in 2b, T6).</para>
///
/// <para>6502/Z80 are UNAFFECTED: this type is 68000-specific (used only by the FieldGrammar Step path);
/// their fetch streams + generated arms are untouched — verified by the byte-identity regression guard.</para>
/// </summary>
public sealed class M68000FetchStream : IFetchStream
{
    private readonly IAddressSpace _bus;
    private ushort _q0;            // the word currently executed-from (== word@FormalPc)
    private ushort _q1;            // the next prefetched word        (== word@FormalPc+2)
    private uint _frontier;        // the address of the next refill read (== FormalPc + 4)
    private uint _formalPc;        // the address q0 came from (the 68000 formal PC, trails the frontier by 4)
    private int _offset;           // units (words) consumed since the last Seed/Reseed — drives Length
    private int _refills;          // M4.5d-2b: refill READS issued since the last BeginInstruction (the bus
                                   // accesses NextUnit traced at the frontier) — drives the per-instruction
                                   // prefetch-refill cycle charge (each refill is a 4-clock word bus cycle).

    /// <summary>Construct over the bus. The queue is empty until <see cref="Seed"/> primes it from the
    /// initial PC + prefetch words (the CPU owns the lifetime — one stream per CPU, reused across Steps).</summary>
    public M68000FetchStream(IAddressSpace bus)
    {
        _bus = bus;
    }

    /// <summary>Back-compat single-instruction construction (the M4.5a-era stateless origin form, used by the
    /// decode-walk + smoke tests): prime the queue by reading the two words physically at <paramref name="origin"/>,
    /// so a fresh stream over a bus that already holds the operword + extension words behaves like the old
    /// Read16-walk for the Length/decode contract. The TomHarte runner uses <see cref="Seed"/> with the
    /// corpus prefetch words instead.</summary>
    public M68000FetchStream(IAddressSpace bus, uint origin) : this(bus)
    {
        Seed(origin, bus.Read16(origin), bus.Read16(unchecked(origin + 2u)));
    }

    public int UnitBytes => 2;
    public int UnitsConsumed => _offset;

    /// <summary>M4.5d-2b: the number of prefetch-REFILL reads issued since the last <see cref="BeginInstruction"/>
    /// — i.e. the bus accesses <see cref="NextUnit"/> performed at the frontier (each one a real 4-clock word
    /// bus cycle that appears in the per-transaction trace). For a purely sequential instruction this equals
    /// <see cref="UnitsConsumed"/> (one refill per consumed word); the generated Step charges
    /// <c>4 * RefillCount</c> for the prefetch portion of the instruction's cycle total (ADR 0008 §3, 2b).</summary>
    public int RefillCount => _refills;

    /// <summary>The 68000 formal PC: the address of the word at the head of the queue (<c>q0</c>). Trails the
    /// physical fetch frontier by the 2-word queue depth.</summary>
    public uint FormalPc => _formalPc;

    /// <summary>The queue END STATE — the two prefetch words the corpus's <c>final.prefetch</c> asserts.</summary>
    public (ushort Word0, ushort Word1) FinalPrefetch => (_q0, _q1);

    /// <summary>Prime the queue from the initial PC + the two corpus prefetch words. The formal PC is the
    /// operword's address; the frontier is two words ahead (the next refill comes from <c>pc + 4</c>).</summary>
    public void Seed(uint pc, ushort prefetch0, ushort prefetch1)
    {
        _q0 = prefetch0;
        _q1 = prefetch1;
        _formalPc = pc;
        _frontier = unchecked(pc + 4u);
        _offset = 0;
    }

    /// <summary>M4.5d-2b: seed the queue from the bus at <paramref name="pc"/> WITHOUT issuing traced bus reads.
    /// The seeded prefetch words are GIVEN by the corpus (<c>initial.prefetch</c>, written into the bus by the
    /// runner) — the real 68000 already holds them in its queue, so it does NOT re-fetch them; they must therefore
    /// NOT appear in the per-transaction bus trace. The old <see cref="Seed"/> call in the generated Step passed
    /// two <c>_bus.Read16</c> results, and over a tracing bus those two reads POLLUTED the trace with phantom
    /// fetches the corpus never records (ADR 0008 §8.1; runner doc). This reads the two words through
    /// <see cref="IAddressSpace.TryPeek8"/> — the untraced inner-memory peek every space forwards to its backing —
    /// so the seed is invisible to the trace. Big-endian: high byte first (the 68000 bus byte order). Falls back
    /// to 0 for an unmapped address (TryPeek8 == false), matching a fresh-RAM read.</summary>
    public void SeedPeek(uint pc)
    {
        _q0 = Peek16(pc);
        _q1 = Peek16(unchecked(pc + 2u));
        _formalPc = pc;
        _frontier = unchecked(pc + 4u);
        _offset = 0;
    }

    /// <summary>Read a big-endian word WITHOUT tracing it (via <see cref="IAddressSpace.TryPeek8"/>, the untraced
    /// inner-memory peek). Used by <see cref="SeedPeek"/> so the corpus-given prefetch words do not pollute the
    /// per-transaction bus trace. Unmapped bytes peek as 0 (a fresh-RAM read).</summary>
    private ushort Peek16(uint address)
    {
        byte hi = _bus.TryPeek8(address, out var h) ? h : (byte)0;
        byte lo = _bus.TryPeek8(unchecked(address + 1u), out var l) ? l : (byte)0;
        return (ushort)((hi << 8) | lo);
    }

    /// <summary>Reseed the queue from a new PC after a non-sequential control transfer (branch/jump/return/
    /// exception): read the two words physically at <paramref name="pc"/> so the end state is
    /// <c>[word@pc, word@(pc+2)]</c>. Does NOT reset <see cref="UnitsConsumed"/> — the instruction's Length is
    /// already fixed by the decode walk that ran before the transfer.</summary>
    public void Reseed(uint pc)
    {
        _formalPc = pc;
        _frontier = unchecked(pc + 4u);
        _q0 = _bus.Read16(pc);
        _q1 = _bus.Read16(unchecked(pc + 2u));
    }

    /// <summary>Begin a fresh instruction at the head of the queue: zero the per-instruction consumed count
    /// (the Length the decode walk computes) AND the per-instruction refill count, leaving the queue words +
    /// frontier intact (the queue persists across Steps on the real CPU). The CPU calls this before each
    /// Decode.</summary>
    public void BeginInstruction() { _offset = 0; _refills = 0; }

    public uint NextUnit()
    {
        ushort word = _q0;                                  // execute-from the head
        _q0 = _q1;                                          // advance: q1 -> q0
        _q1 = _bus.Read16(_frontier);                       // refill: one fresh word at the frontier (TRACED, 4c)
        _frontier = unchecked(_frontier + 2u);
        _formalPc = unchecked(_formalPc + 2u);              // the formal PC follows the head
        _offset++;
        _refills++;                                         // count the traced refill read (drives the 2b charge)
        return word;
    }

    public uint PeekUnit() => _q0;                          // lookahead at the head, no advance/refill
}
