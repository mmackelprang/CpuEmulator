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
/// <para><b>2a scope (PC/prefetch-exact, NOT cycle-exact).</b> This models the queue END STATE — the two
/// <see cref="FinalPrefetch"/> words + the trailing <see cref="FormalPc"/>. The exact per-transaction trace
/// interleaving and <c>CycleCount == length</c> are M4.5d-2b; 2a leaves the flat <c>UnitsConsumed * 4</c>
/// cycle charge in place and asserts queue state only.</para>
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
    /// (the Length the decode walk computes), leaving the queue words + frontier intact (the queue persists
    /// across Steps on the real CPU). The CPU calls this before each Decode.</summary>
    public void BeginInstruction() => _offset = 0;

    public uint NextUnit()
    {
        ushort word = _q0;                                  // execute-from the head
        _q0 = _q1;                                          // advance: q1 -> q0
        _q1 = _bus.Read16(_frontier);                       // refill: one fresh word at the frontier
        _frontier = unchecked(_frontier + 2u);
        _formalPc = unchecked(_formalPc + 2u);              // the formal PC follows the head
        _offset++;
        return word;
    }

    public uint PeekUnit() => _q0;                          // lookahead at the head, no advance/refill
}
