using CpuEmulator.Core;

namespace CpuEmulator.Cpus.M68000;

/// <summary>The MINIMAL hand-written half of the 68000 (M4.1) — the bus wiring, the A7/USP/SSP banking,
/// the SR/CCR accessors, and the policy hooks the generated partial requires. This is the STATE
/// FOUNDATION: it makes the generated register file compile and proves the register model synthetically
/// (32-bit round-trip, A7 banking by the SR S-bit, the SR/CCR split). It is NOT an interpreter: there is
/// NO decode, NO EA, NO op body, NO wide bus, NO prefetch queue, NO exception/vector machinery — those are
/// M4.2–M4.5. The instruction table is empty, so M4.1 never calls Step. The interrupt hooks are inert
/// (the IPL-level model is M4.5d).</summary>
public sealed partial class M68000Cpu
{
    // The single program/data bus (von Neumann; the 68000 has no separate I/O space — IO is memory-mapped).
    // M4.1 wires Read8/Write8 (the byte path); the wide big-endian Read16/Read32 are M4.2.
    private readonly IAddressSpace _bus;

    /// <summary>The supervisor-stack-bit mask in the 16-bit SR (bit 13). The S-bit selects which physical
    /// stack A7 aliases (USP when clear, SSP when set). Pinned here so the banking logic does not depend on
    /// the FlagLayout's declared bit (the layout names S=13; this constant must match it — guarded by the
    /// SupervisorMode_reflects_the_SR_S_bit test).</summary>
    private const ushort SrSupervisorBit = 1 << 13;

    public M68000Cpu(IAddressSpace bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <summary>True when the SR supervisor (S) bit is set. Selects the SSP bank for A7; the eventual
    /// exception machinery (M4.5d) toggles it on entry/RTE.</summary>
    public bool SupervisorMode => (SR & SrSupervisorBit) != 0;

    /// <summary>M4.5d-2a (ADR 0008 §5): the prefetch-queue END STATE after the last Step — the two words the
    /// corpus asserts as <c>final.prefetch</c>. The queue (<c>_fetchQueue</c>) is the generated FieldGrammar
    /// Step's CPU-owned stream (seeded from the formal PC, advanced+refilled by the decode walk, reseeded on a
    /// control transfer). Returns (0, 0) before the first Step (the queue is lazily created). The trailing
    /// formal PC is the live PC register (== <c>final.pc</c> for a non-deferred case).</summary>
    public (ushort Word0, ushort Word1) FinalPrefetch =>
        _fetchQueue?.FinalPrefetch ?? ((ushort)0, (ushort)0);

    /// <summary>Set/clear the SR supervisor (S) bit. A test/host convenience for M4.1 (the real toggle is
    /// the exception/RTE sequence in M4.5d). Keeps the banking tests independent of SR-bit-layout knowledge.</summary>
    public void SetSupervisorMode(bool supervisor) =>
        SR = (ushort)(supervisor ? (SR | SrSupervisorBit) : (SR & ~SrSupervisorBit));

    /// <summary>The Condition Code Register — the low byte of the 16-bit SR (X N Z V C). The 68000's
    /// user-visible flags; the system byte (interrupt mask, S, T) is the SR high byte.</summary>
    public byte Ccr
    {
        get => (byte)(SR & 0xFF);
        set => SR = (ushort)((SR & 0xFF00) | value);
    }

    /// <summary>A7 — the stack pointer, BANKED into USP/SSP by the SR S-bit (ADR 0003 Decision 1). NOT a
    /// spec register (Decision D2): the TomHarte schema names usp/ssp, never a7, so introspection exposes
    /// USP/SSP by name and A7 is this C# convenience view (the same altitude as the Z80 pair-views, but
    /// mode-selected rather than high/low-split, so hand-written rather than generated). The implicit
    /// stack ops of exceptions/BSR/JSR/RTS (M4.5) reference A7; privileged MOVE USP reaches the other bank.</summary>
    public uint A7
    {
        get => SupervisorMode ? SSP : USP;
        set { if (SupervisorMode) SSP = value; else USP = value; }
    }

    /// <summary>Reset — M4.1 stub (the real reset reads the initial SSP + PC from the vector table at
    /// addresses 0/4 via the wide bus; that is M4.5). Sets nothing else (the harness sets registers
    /// explicitly in the M4.5 TomHarte runner).</summary>
    public void Reset() { }

    // ── The policy hooks the generated partial requires (M4.5d-1, DD5: the thin IPL-level model) ──────────
    // The 68000's real interrupt input is the 3-bit IPL line (0-7), set via SetInterruptLevel. The generic
    // SetIrqLine/SetNmiLine shims map onto it so a generic caller still works: SetIrqLine(true) asserts a
    // generic level-7 (NMI-equivalent); SetNmiLine likewise. No GENERATED caller asserts these in the test
    // path (the synthetic IPL tests drive SetInterruptLevel directly) — they exist to satisfy the partial.
    public void SetIrqLine(bool asserted) => _iplLevel = asserted ? 7 : 0;
    public void SetNmiLine(bool asserted) { if (asserted) _iplLevel = 7; }   // level-7 is the non-maskable input

    /// <summary>The pending interrupt priority level (0-7); 7 is non-maskable. M4.5d-1 (DD5): the thin
    /// synthetic-tested IPL model; the acknowledge-cycle accuracy + the device-supplied vector are M4.5d-2.</summary>
    private int _iplLevel;

    /// <summary>Set the IPL input (0-7). The 68000 services the interrupt at the next Step when the level
    /// exceeds the SR interrupt mask (or is level 7).</summary>
    public void SetInterruptLevel(int level) => _iplLevel = level & 7;

    /// <summary>The SR interrupt mask (bits 10-8).</summary>
    private uint SrInterruptMask => (uint)((SR >> 8) & 7u);

    /// <summary>Program/data-bus byte read; charges one cycle (the cycle invariant). The wide big-endian
    /// Read16/Read32 the 68000 truly needs are M4.2 (this byte path keeps the generated Step compiling).</summary>
    private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
    private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }

    // ── Wide big-endian bus access (M4.2 surface; M4.5a wires it into the MOVE bodies). Each charges the
    //    bus-access cycles. The 16-bit bus decomposes a .l into two .w transactions: ReadLongBus is two
    //    Read16 calls (high word first) — which the tracing bus records as two .w transactions. The cycle
    //    counts here are the BUS portion; the op body adds the instruction's internal cycles so CycleCount
    //    ends == the case's length (Σ transaction cycles — validated by the TomHarte gate). ────────────────
    private const int WordAccessCycles = 4;   // a word bus cycle is 4 clocks on the 68000 (S0-S7)

    /// <summary>M4.5d-2b (ADR 0008 §3): charge <paramref name="n"/> IDLE clocks — the corpus <c>["n", N]</c>
    /// slots (internal/dead bus cycles where the 68000 does no bus access). Idle cycles advance CycleCount ONLY;
    /// they produce NO bus access, so they emit NO trace entry (the runner's DiffBusTrace filters idle out of the
    /// EXPECTED list, so the emit side must simply not touch the bus). This is the FieldGrammar-path idle
    /// primitive — the analogue of the generated <c>IdleCycle()</c> the HaltOp/6502 path uses, but parameterized
    /// for the multi-clock 68000 idle runs and not tied to the halted latch. The generated Step flushes the
    /// per-instruction idle accumulator through it (the prefetch refills charge themselves via <see cref="Refill"/>,
    /// the operand accesses via the WordBus helpers).</summary>
    private void IdleCycles(int n) => _cycles += n;

    /// <summary>M4.5d-2b: the per-instruction IDLE-cycle accumulator the op bodies declare and the generated Step
    /// flushes (via <see cref="IdleCycles"/>) after the body runs. An op body adds the internal/dead cycles its
    /// instruction class spends with the bus idle (the corpus <c>["n", N]</c> slots) with <c>Idle(N)</c>; the
    /// generated Step resets it to 0 at the start of each instruction and charges the total at the end. The
    /// per-class idle reconciliation (e.g. the predecrement/index internal cycle, MULU/DIVU's long idle run) is
    /// T6.</summary>
    private int _pendingIdle;

    /// <summary>M4.5d-2b: an op body declares its internal/idle (<c>["n", N]</c>) clocks here. Accumulated into
    /// <see cref="_pendingIdle"/> and flushed via <see cref="IdleCycles"/> after the body (the runner's
    /// DiffBusTrace filters idle out of the expected trace, so idle adds cycles but no bus access).</summary>
    private void Idle(int n) => _pendingIdle += n;

    /// <summary>M4.5d-2b (the DEFERRED-REFILL seam, ADR 0008 §8.1): issue ONE deferred prefetch refill at THIS
    /// point in the op body — pop the oldest pending refill's frontier address (recorded by the decode walk's
    /// NextUnit) and do the TRACED 4-clock word bus read there. This places the refill read in the per-transaction
    /// trace BETWEEN the surrounding operand accesses (the interleaved shapes — e.g. CLR.w (An) = read, Refill,
    /// write). A no-op when the backlog is empty (an over-issuing body is harmless). The queue word is already
    /// correct (NextUnit set it via the untraced peek); this read exists for the TRACE + the cycle charge, so the
    /// returned word is discarded.</summary>
    private void Refill()
    {
        if (_fetchQueue is not null && _fetchQueue.TryPopRefill(out uint addr))
        {
            _cycles += WordAccessCycles;
            _ = _bus.Read16(addr);   // TRACED refill read (the queue value was already set via the untraced peek)
        }
    }

    /// <summary>M4.5d-2b: flush the LEADING refills — every pending refill EXCEPT the last — as traced reads
    /// BEFORE the operand access. On the 68000 the EXTENSION-word prefetch refills happen during decode and
    /// LEAD the operand access in the trace (the <c>F…F</c> prefix of e.g. CLR.b d16(An) = <c>F O F O</c>, abs.L
    /// = <c>F F O F O</c>); only the LAST refill (the operword-frontier "overlap" prefetch) defers into the
    /// operand sequence via <see cref="Refill"/>. A single-word instruction (1 pending refill) leads NOTHING — its
    /// one refill is the deferred overlap. The op body calls this before the operand read, then <see cref="Refill"/>
    /// at the overlap point.</summary>
    private void LeadRefills()
    {
        while (_fetchQueue is not null && _fetchQueue.PendingRefills > 1
               && _fetchQueue.TryPopRefill(out uint addr))
        {
            _cycles += WordAccessCycles;
            _ = _bus.Read16(addr);
        }
    }

    /// <summary>M4.5d-2b: flush ALL still-pending deferred refills as traced reads (in decode order). The
    /// generated Step calls this after the body so any refill the body did not place explicitly trails the
    /// operand accesses (the common <c>...F</c> / refills-lead tail). A refills-lead class needs NO explicit
    /// <see cref="Refill"/> calls — the whole backlog flushes here in order.</summary>
    private void FlushRefills()
    {
        while (_fetchQueue is not null && _fetchQueue.TryPopRefill(out uint addr))
        {
            _cycles += WordAccessCycles;
            _ = _bus.Read16(addr);
        }
    }

    /// <summary>M4.5d-2b: set by a control-transfer body that emitted its OWN target-prefetch reads (the two
    /// queue-end words) at the points the corpus records them — e.g. JSR/BSR interleave those reads with the
    /// return-PC push. The generated Step then recomputes the queue end state UNTRACED (ReseedPeek) instead of
    /// re-reading (and re-tracing) the target words. Cleared by the Step after each use. The simple transfers
    /// (Bcc-taken/JMP/DBcc-taken/RTS/RTR/RTE) leave this false and let the Step's traced Reseed emit the two
    /// target reads.</summary>
    private bool _reseededInBody;

    /// <summary>M4.5d-2b: a control-transfer body calls this to emit the TWO target-prefetch reads (the queue
    /// end-state words at <paramref name="target"/> and <paramref name="target"/>+2) as TRACED 4-clock word bus
    /// cycles at THIS point — used when the reads must interleave with other body accesses (JSR/BSR) rather than
    /// trail. Sets <see cref="_reseededInBody"/> so the Step recomputes the end state untraced. Returns the two
    /// words so the body can use them if needed (normally discarded — the prefetch values are queue state).</summary>
    private (ushort, ushort) PrefetchTarget(uint target)
    {
        ushort w0 = ReadWordBus(target);
        ushort w1 = ReadWordBus(unchecked(target + 2u));
        _reseededInBody = true;
        return (w0, w1);
    }

    /// <summary>M4.5d-2b: the internal "address calculation" idle cycles a memory EA mode costs on the 68000,
    /// charged into <see cref="_pendingIdle"/> by the data-op read/write helpers BEFORE the operand access. The
    /// 68000 spends extra internal clocks computing the predecrement / indexed addresses:
    /// <list type="bullet">
    ///   <item><c>-(An)</c> (mode 4): 2 clocks (the predecrement);</item>
    ///   <item><c>(d8,An,Xn)</c> (mode 6) / <c>(d8,PC,Xn)</c> (mode 7 reg 3): 2 clocks (the index add).</item>
    /// </list>
    /// Every other mode (Dn/An/(An)/(An)+/d16/abs/#imm) costs 0 internal clocks for the data ops (the
    /// reconciled corpus shapes — e.g. CLR.b -(An) = idle2 + read + refill + write = 14). LEA/PEA/JMP/JSR's
    /// address-only EA timing is bespoke (charged in their own bodies), so this is the DATA-op rule only.</summary>
    private void EaCalcIdle(uint mode, uint reg)
    {
        if (mode == 4u || mode == 6u || (mode == 7u && reg == 3u))
            Idle(2);
    }

    private ushort ReadWordBus(uint address)
    {
        _cycles += WordAccessCycles;
        return _bus.Read16(address);
    }

    private void WriteWordBus(uint address, ushort value)
    {
        _cycles += WordAccessCycles;
        _bus.Write16(address, value);
    }

    // A long access is TWO word transactions (high word first) — charge + access each separately so the
    // tracing bus records two .w transactions (the 16-bit-bus decomposition the vectors assert).
    private uint ReadLongBus(uint address)
    {
        ushort hi = ReadWordBus(address);
        ushort lo = ReadWordBus(address + 2);
        return ((uint)hi << 16) | lo;
    }

    private void WriteLongBus(uint address, uint value)
    {
        WriteWordBus(address, (ushort)(value >> 16));
        WriteWordBus(address + 2, (ushort)value);
    }

    /// <summary>M4.5d-2b: write a long as two .w transactions LOW WORD FIRST (the high word at address+2 written
    /// first, then the low word at address). This is the 68000 read-modify-write (NEG/NOT/CLR/single-EA ALU)
    /// write-back order — vector-confirmed: a .l RMW traces W(addr+2) then W(addr) (e.g. ADD.l Dn,(An) =
    /// R R F W(addr+2) W(addr)), the reverse of the data-fetch / MOVE.l store order. The cycle cost is identical
    /// (two word writes); only the trace ORDER differs, so the RMW path uses this and the MOVE store keeps the
    /// high-word-first <see cref="WriteLongBus"/>.</summary>
    private void WriteLongBusRmw(uint address, uint value)
    {
        WriteWordBus(address + 2, (ushort)value);            // low word first (at the higher address)
        WriteWordBus(address, (ushort)(value >> 16));        // then the high word
    }

    // Test seams (mirror the generated ComputeEaProbe) — drive the wide path from synthetic unit tests.
    public ushort ReadWordBusProbe(uint a) => ReadWordBus(a);
    public uint ReadLongBusProbe(uint a) => ReadLongBus(a);
    public void WriteWordBusProbe(uint a, ushort v) => WriteWordBus(a, v);
    public void WriteLongBusProbe(uint a, uint v) => WriteLongBus(a, v);

    /// <summary>Undefined-opcode hook — M4.1 stub (the 68000's illegal-instruction exception is M4.5d). The
    /// instruction table is empty in M4.1, so any Step would route here; M4.1 never calls Step.</summary>
    private void HandleUndefinedOpcode(byte opcode) { _cycles++; }

    /// <summary>The interrupt acknowledge (M4.5d-1, DD5): reuse RaiseException (the interrupt is "an exception
    /// sourced by the IPL line"). Enter supervisor, push the (PC, SR) frame, set the mask to the serviced level,
    /// vector through the autovector (24 + level). The generated Step calls this FIRST, so the acknowledge fires
    /// before the fetch (the ADR 0004 §2 Decision 3 seam). DD5: autovector default; the device-supplied vector
    /// + the acknowledge-cycle accuracy are M4.5d-2. NO TomHarte vector exercises this — synthetic-tested only.</summary>
    private partial bool TryServiceInterrupt()
    {
        if (!InterruptPending) return false;
        int level = _iplLevel;
        ushort srAtFault = (ushort)(SR & 0xFFFF);
        RaiseException(Vector.AutovectorBase + (uint)level, FrameKind.Small, srAtFault, PC);
        // RaiseException entered supervisor + cleared trace; now set the SR interrupt mask to the serviced level
        // (so a same-or-lower interrupt does not re-fire).
        SR = (ushort)(((uint)SR & 0xF8FFu) | ((uint)level << 8));
        _iplLevel = 0;   // the device de-asserts on acknowledge (the synthetic model).
        // M4.5d-2a (review Finding 2): the acknowledge set PC to the handler (a non-sequential transfer), but
        // Step returns here WITHOUT seeding the queue. Reseed it from the handler PC so FinalPrefetch reflects
        // the handler's prefetch, not the previous instruction's stale queue. (Synthetic-only in 2a — no vector
        // exercises an async interrupt; the acknowledge-cycle trace is 2b. Null-safe: the queue is lazily created
        // by the generated Step, so a pre-first-Step interrupt has nothing to reseed.)
        _fetchQueue?.Reseed(PC);
        return true;
    }

    /// <summary>True when the IPL exceeds the SR interrupt mask (or is level 7, non-maskable). M4.5d-1 (DD5).</summary>
    public partial bool InterruptPending => _iplLevel == 7 || (uint)_iplLevel > SrInterruptMask;
}
