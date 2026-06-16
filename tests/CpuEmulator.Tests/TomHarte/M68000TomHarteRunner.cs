using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Tests.Mos6502;   // TracingAddressSpace + BusAccess

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// The 680x0 TomHarte runner (M4.5a). Sets the full initial state, seeds the operword + extension words from
/// the prefetch queue into the bus (the 680x0 vectors place the operword in <c>initial.prefetch[0]</c>, NOT in
/// RAM at PC — confirmed across the whole v1 dataset), Steps once, and diffs the result.
///
/// <para><b>The corrected M4.5a gate is split along the axis ADR 0004 §3 already drew:</b></para>
/// <list type="bullet">
/// <item><b>DATA axis (asserted by M4.5a, ALWAYS):</b> the final D0–D7, A0–A6, USP, SSP, SR, and RAM — the
/// pure MOVE execution RESULT, independent of fetch timing. A mismatch here is a real MOVE bug.</item>
/// <item><b>TIMING axis (deferred to M4.5d):</b> final.pc, final.prefetch, the per-transaction bus trace, and
/// the cycle count (length). These are the prefetch-queue's observable timing state — the prefetch-queue
/// mechanism + cycle-accurate sequencing are M4.5d per ADR 0004 §3, so M4.5a carries them with TODO(M4.5d)
/// rather than asserting them. (The plan's original gate over-specified the timing axis as an M4.5a
/// precondition; the gate was corrected — see the plan's gate section.)</item>
/// </list>
/// <para>The two USP families (MOVEfromUSP/MOVEtoUSP) happen to ALSO satisfy the full timing axis under the
/// mechanical prefetch model (single transaction, no idle cycles), so the sweep asserts the fuller gate for
/// them as bonus evidence (<c>timingAxis: true</c>). Returns null on pass, a formatted report on failure.</para>
/// </summary>
internal static class M68000TomHarteRunner
{
    /// <summary>True when the case's real 68000 took an EXCEPTION (an address error on a misaligned wide EA
    /// access, a privilege violation, etc.) instead of completing the MOVE. The exception machinery — the
    /// stack-frame push + the vector fetch + the handler jump — is M4.5d per ADR 0004 §3 (the same axis as the
    /// prefetch/timing deferral), NOT a MOVE semantics concern. Detected by the un-fakeable signal: a pair of
    /// vector-table reads (at 4·v and 4·v+2, v an exception vector) whose composed value IS the case's final
    /// PC — i.e. the 68000 fetched a handler from the vector table and jumped to it. This does NOT match a
    /// legitimate MOVE that merely writes the stack via -(A7)/(A7) (those keep being asserted on the data axis),
    /// because such a MOVE does not fetch a handler from the vector table into PC. M4.5a executes only the
    /// non-exception MOVE cases; the exception cases are wired in M4.5d.
    /// <para>INVARIANT (review finding): this relies on TomHarte 680x0 v1 never placing a MOVE's operand reads
    /// at an aligned vector-table pair (4·v, 4·v+2 with v &lt; 0x100) whose composed value equals final.pc — a
    /// coincidence that would misclassify a normal MOVE as an exception case. Empirically confirmed across all
    /// 10 in-scope files (57,447 non-exception cases executed green, zero false-positive exclusions). When the
    /// M4.5d exception machinery lands and the trap is modeled, this heuristic is retired (the runner reproduces
    /// the exception sequence directly instead of detecting+deferring it).</para></summary>
    public static bool IsExceptionCase(M68000TomHarteCase c)
    {
        var reads = new List<(uint Addr, uint Val)>();
        foreach (var t in c.Transactions)
            if (t is { IsIdle: false, IsRead: true })
                reads.Add((t.Address, t.Value));
        for (int k = 0; k + 1 < reads.Count; k++)
        {
            var (a0, v0) = reads[k];
            var (a1, v1) = reads[k + 1];
            if (a1 == a0 + 2 && a0 < 0x400u && (a0 % 4u) == 0u)
            {
                uint handler = (v0 << 16) | v1;
                if (handler == c.Final.Pc) return true;
            }
        }
        return false;
    }

    /// <summary>Run one case. <paramref name="timingAxis"/> additionally asserts final.pc + the per-transaction
    /// bus trace + the cycle count (the M4.5d timing axis) — used only for the USP families that satisfy it
    /// under the mechanical prefetch model. Default false = the M4.5a data axis (regs + SR + RAM) only.
    /// Returns the <see cref="DeferredException"/> sentinel for an exception case (M4.5d), so the sweep counts
    /// it as deferred rather than executed-green.</summary>
    public const string DeferredException = "DEFERRED(M4.5d): exception case (address-error/privilege vector)";

    /// <summary>M4.5d-1 (DD4): the address-error subset (vector 3). When <c>assertExceptions</c> un-defers the
    /// exception cases, the group-0 large-frame WORD contents are timing-coupled (ADR 0008 §2.1) — M4.5d-1
    /// asserts trap-taken (mode + handler PC) but the pushed frame words may not match on the data axis. Task 13
    /// Step 0 resolves this empirically; if the address-error frame words prove timing-sensitive, this predicate
    /// keeps vector-3 deferred while vector-4/5/6/7/8 assert. The vector-3 table entry is the read pair at
    /// (0xC, 0xE) whose composed value is final.pc.</summary>
    public static bool IsAddressErrorCase(M68000TomHarteCase c)
    {
        var reads = new List<(uint Addr, uint Val)>();
        foreach (var t in c.Transactions)
            if (t is { IsIdle: false, IsRead: true })
                reads.Add((t.Address, t.Value));
        for (int k = 0; k + 1 < reads.Count; k++)
        {
            var (a0, v0) = reads[k];
            var (a1, _) = reads[k + 1];
            if (a0 == 4u * Vector3 && a1 == a0 + 2)
            {
                _ = v0;
                return true;
            }
        }
        return false;
    }
    private const uint Vector3 = 3;   // address error (group 0 — the large frame)

    /// <summary>M4.5d-2a (ADR 0008 §5, plan T0): the PC/prefetch assertion mode — diffs the prefetch-queue
    /// END STATE (<c>final.pc</c> + both <c>final.prefetch</c> words) WITHOUT the full per-transaction trace /
    /// cycle-count diff (the 2a ceiling; cycle-exactness is 2b). Asserted on top of the data axis. Default-off
    /// (DD5) so the M4.5a-c data sweeps + the M4.5d-1 data-axis gate stay byte-identical; the 2a sweep
    /// (M68000TimingAxisTomHarteTests) passes <c>pcPrefetchAxis: true, assertExceptions: true</c>.</summary>
    public static string? RunCase(M68000TomHarteCase c, bool timingAxis = false, bool assertExceptions = false,
                                  bool pcPrefetchAxis = false)
    {
        // M4.5d-1 (ADR 0008 §3.4, sign-off D): the default-off assertExceptions preserves M4.5a-c byte-for-byte.
        // With the default, IsExceptionCase still short-circuits to DeferredException, so the M4.5a-c sweeps (which
        // never pass the flag) stay byte-identical. The M4.5d-1 exception sweep (Task 14) passes true, which lets
        // those cases RUN and be diffed on the data axis (the modeled RaiseException produces the same vector
        // fetch + frame the case expects). The flag only STRENGTHENS the gate; it never weakens the default.
        if (!assertExceptions && IsExceptionCase(c)) return DeferredException;
        // (DD4) The address-error (vector 3) large-frame WORD contents are timing-coupled (ADR 0008 §2.1). Task 13
        // Step 0 found the pushed group-0 frame words do NOT match on the data axis (they encode in-progress
        // bus-cycle state), so vector-3 stays DEFERRED even under assertExceptions — the small-frame exceptions
        // (vector 4/5/6/7/8) assert; the address-error frame-word precision is M4.5d-2 (assert trap-taken only).
        if (assertExceptions && IsAddressErrorCase(c)) return DeferredException;

        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 24,
            endianness: Endianness.BigEndian);
        inner.MapMemory(0x000000, new byte[0x1000000], writable: true);
        foreach (var e in c.Initial.Ram) inner.Write8(e.Address & inner.AddressMask, e.Value);

        // Seed the prefetch queue into the bus so the live fetch sees the operword (D-C corrected: the operword
        // is in initial.prefetch[0], NEVER in RAM at PC). prefetch[0] -> bus[pc], prefetch[1] -> bus[pc+2]; any
        // further extension words are already present in initial.ram at pc+4+. This makes the data-axis result
        // exact; the prefetch-queue REFILL (and its trace/cycle cost) is the M4.5d timing axis.
        uint pc = c.Initial.Pc;
        ushort[] pf = c.Initial.Prefetch;
        if (pf.Length > 0) inner.Write16((pc + 0u) & inner.AddressMask, pf[0]);
        if (pf.Length > 1) inner.Write16((pc + 2u) & inner.AddressMask, pf[1]);

        var bus = new TracingAddressSpace(inner);

        var cpu = new M68000Cpu(bus);
        var s = c.Initial;
        for (int i = 0; i < 8; i++) cpu.SetRegister($"D{i}", s.D[i]);
        for (int i = 0; i < 7; i++) cpu.SetRegister($"A{i}", s.A[i]);
        cpu.SetRegister("USP", s.Usp);
        cpu.SetRegister("SSP", s.Ssp);
        cpu.SetRegister("PC", s.Pc);
        cpu.SetRegister("SR", s.Sr);

        cpu.Step();

        var problems = new List<string>();
        void Check(string name, uint expected)
        {
            uint got = (uint)cpu.GetRegister(name);
            if (got != expected) problems.Add($"{name}: expected {expected:X8}, got {got:X8}");
        }
        var f = c.Final;

        // ── DATA axis (M4.5a — always asserted): the pure execution result. ──────────────────────────────────
        for (int i = 0; i < 8; i++) Check($"D{i}", f.D[i]);
        for (int i = 0; i < 7; i++) Check($"A{i}", f.A[i]);
        Check("USP", f.Usp);
        Check("SSP", f.Ssp);
        { uint gotSr = (uint)cpu.GetRegister("SR"); if (gotSr != f.Sr) problems.Add($"SR: expected {f.Sr:X4}, got {gotSr:X4}"); }

        // RAM diff via the INNER (non-tracing) space so the verification read is not itself traced. The MOVE
        // operand writes are the data axis; the prefetch-refill READS do not change RAM, so the inner-space RAM
        // diff is exact for the data axis even though M4.5a does not model the refill.
        foreach (var e in f.Ram)
            if (inner.Read8(e.Address & inner.AddressMask) != e.Value)
                problems.Add($"RAM[{e.Address:X6}]: expected {e.Value:X2}, got {inner.Read8(e.Address & inner.AddressMask):X2}");

        // ── PC/PREFETCH axis (M4.5d-2a — the queue END STATE: final.pc + both final.prefetch words). The 2a
        // ceiling: it asserts the prefetch-queue mechanism's observable state WITHOUT the per-transaction
        // trace / cycle-count diff (those are 2b — cycle-exactness). final.pc is the trailing formal PC (the
        // live PC register after Step); final.prefetch is the CPU-owned queue's end state.
        if (pcPrefetchAxis)
        {
            Check("PC", f.Pc);
            var (w0, w1) = cpu.FinalPrefetch;
            if (f.Prefetch.Length > 0 && w0 != f.Prefetch[0])
                problems.Add($"prefetch[0]: expected {f.Prefetch[0]:X4}, got {w0:X4}");
            if (f.Prefetch.Length > 1 && w1 != f.Prefetch[1])
                problems.Add($"prefetch[1]: expected {f.Prefetch[1]:X4}, got {w1:X4}");
        }

        // ── TIMING axis (M4.5d-2b — the FULL per-transaction bus trace + cycle count == length). 2a does NOT
        // turn this on (the flat *4 cycle charge stands; the refill-interleaved trace is 2b). It remains the
        // bonus gate for the USP families that satisfy the mechanical model.
        if (timingAxis)
        {
            Check("PC", f.Pc);
            if (cpu.CycleCount != c.Length)
                problems.Add($"cycle count: expected {c.Length}, got {cpu.CycleCount}");
            DiffBusTrace(problems, bus.Trace, c.Transactions);
        }

        return problems.Count == 0 ? null : Format(c, problems);
    }

    /// <summary>Compare the recorded word/long BusAccess trace against the case's non-idle transactions, in
    /// order: address + direction + size + value (richer than the Z80's address-only diff — Recon §C). Idle
    /// ("n") transactions have no bus access, so they are filtered out of the expected list. (TIMING axis —
    /// M4.5d; used by the USP sweep as bonus evidence.)</summary>
    private static void DiffBusTrace(List<string> problems, List<BusAccess> got, M68000Transaction[] expected)
    {
        var bus = expected.Where(t => !t.IsIdle).ToArray();
        int n = System.Math.Min(bus.Length, got.Count);
        for (int i = 0; i < n; i++)
        {
            var e = bus[i];
            var a = got[i];
            AccessWidth ew = e.SizeTag == ".b" ? AccessWidth.Byte
                           : e.SizeTag == ".w" ? AccessWidth.Word : AccessWidth.Long;
            if (a.Address != e.Address || a.IsRead != e.IsRead || a.Width != ew || a.Value != e.Value)
            {
                problems.Add($"bus trace diverges at access {i + 1}: expected " +
                    $"{(e.IsRead ? "R" : "W")}{e.SizeTag} {e.Address:X6}={e.Value:X} got " +
                    $"{(a.IsRead ? "R" : "W")} {a.Address:X6}={a.Value:X} (w {a.Width})");
                break;
            }
        }
        if (bus.Length != got.Count)
            problems.Add($"bus access count: expected {bus.Length}, got {got.Count}");
    }

    private static string Format(M68000TomHarteCase c, List<string> problems)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"FAIL: {c.Name}");
        foreach (var p in problems) sb.AppendLine($"  - {p}");
        return sb.ToString();
    }
}
