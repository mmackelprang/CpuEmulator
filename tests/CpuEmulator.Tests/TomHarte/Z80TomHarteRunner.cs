using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;
using CpuEmulator.Tests.Mos6502;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Executes a Z80 SingleStepTests case against a fresh <see cref="Z80Cpu"/> with full-64KiB program
/// RAM + a 16-bit I/O space, both tracing. Sets the full Z80 state (incl. F's X/Y bits and the packed
/// alternate pairs via the pair-view SetRegister), Steps once, and diffs registers + RAM + the
/// separate ports array + the per-T-state bus trace (handling null-bus internal T-states).
///
/// Staged gate (Ground truth G): <paramref name="registersOnly"/> asserts registers + RAM + ports +
/// cycle COUNT first (flag/register correctness), deferring the per-T-state bus-trace order; the full
/// gate (registersOnly == false) also diffs the trace.
/// </summary>
internal static class Z80TomHarteRunner
{
    // Per-worker-thread reusable 64 KiB program + 64 KiB I/O buses (lever 2 — the 68000 _ramArena pattern, ported).
    // RunCase/RunCaseThroughJit are synchronous (no await), so [ThreadStatic] is reentrancy-safe.
    [ThreadStatic] private static AddressSpace? _progTls;
    [ThreadStatic] private static byte[]? _progRamTls;
    [ThreadStatic] private static AddressSpace? _ioTls;
    [ThreadStatic] private static byte[]? _ioRamTls;

    private static (AddressSpace prog, byte[] progRam, AddressSpace io, byte[] ioRam) RentBuses()
    {
        if (_progTls is null)
        {
            _progRamTls = new byte[0x10000];
            _progTls = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
            _progTls.MapMemory(0x0000, _progRamTls, writable: true);
            _ioRamTls = new byte[0x10000];
            _ioTls = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
            _ioTls.MapMemory(0x0000, _ioRamTls, writable: true);
        }
        _progTls.ClearMappedBacking(_progRamTls!);
        _ioTls!.ClearMappedBacking(_ioRamTls!);
        return (_progTls, _progRamTls!, _ioTls, _ioRamTls!);
    }

    // Per-worker reused JIT (lever 4) for the all-fallback Z80 JIT path. SUBTLETY: in 5-3a every Z80 op falls back
    // to inner.Step, which writes ports through the INNER Z80's OWN io reference (set at construction) — NOT the
    // JIT's _ioBus (inert for the all-fallback path). So the reused inner Z80 must be bound ONCE to a PERSISTENT
    // TracingAddressSpace (_ioTraceTls) wrapping the persistent ioInner (_ioTls); DiffPorts reads that same trace.
    // RentBuses re-zeroes the program + ioInner backing in place (mapping persists) so Fastmem's snapshot stays
    // valid; ResetForReuse() flushes the block cache; and we clear _ioTraceTls's trace per case so the recorded
    // port trace is identical to a fresh-per-case TracingAddressSpace's. Z80Cpu.Reset() does NOT zero _cycles, so
    // DiffFinalState asserts the per-case cycle DELTA (captured after the reset) — byte-identical to the fresh
    // path's absolute assert (fresh _cycles starts at 0).
    [ThreadStatic] private static TracingAddressSpace? _ioTraceTls;
    [ThreadStatic] private static JittedCpu<Z80Cpu>? _jitTls;
    [ThreadStatic] private static Z80Cpu? _jitInnerTls;

    /// <summary>Rent this worker thread's reused Z80 JIT (lever 4), bound ONCE to the persistent program bus +
    /// the persistent Io <see cref="TracingAddressSpace"/> wrapping <paramref name="ioInner"/>. Built on first
    /// call; thereafter flushed via <c>ResetForReuse</c> and its Io trace cleared, so each case starts byte-clean.
    /// Returns the reused inner Z80 (state owner), the JIT (driver), and the persistent Io trace (DiffPorts reads).</summary>
    private static (Z80Cpu Inner, JittedCpu<Z80Cpu> Jit, TracingAddressSpace IoTrace) RentJit(AddressSpace program, AddressSpace ioInner)
    {
        if (_jitTls is null)
        {
            _ioTraceTls = new TracingAddressSpace(ioInner);   // persistent Io trace; bound ONCE into the inner Z80
            (_jitTls, _jitInnerTls) = Z80JittedCpuFactory.Create(program, _ioTraceTls);
        }
        else
        {
            _jitTls.ResetForReuse();   // flush cache + clear chains + reset inner — bound to the SAME pooled buses
            _ioTraceTls!.ResetTrace(); // clear the accumulated port trace so this case starts byte-clean
        }
        return (_jitInnerTls!, _jitTls, _ioTraceTls!);
    }

    /// <summary>Run one case. The WZ/MEMPTR model is COMPLETE (M3.4c, Piece A): every Z80 op models its
    /// WZ writes and maintains Q (the shared 6502-class ops set Q=0; the flag-writing ops set Q=F), and
    /// the IM ops set the interrupt mode. So the final Q AND WZ AND IM are checked on EVERY case (the
    /// M3.4b <c>checkInternal</c> scoping is retired). Iff1/Iff2 were already checked universally.</summary>
    public static string? RunCase(Z80TomHarteCase c, bool registersOnly = false)
    {
        var (inner, _, ioInner, _) = RentBuses();
        foreach (var e in c.Initial.Ram) inner.Write8(e.Address, e.Value);
        var bus = new TracingAddressSpace(inner);

        // Pre-load the IN ports' return values so a read returns the vector's expected byte.
        foreach (var port in c.Ports)
            if (port.IsRead) ioInner.Write8(port.Address, port.Value);
        var io = new TracingAddressSpace(ioInner);

        var cpu = new Z80Cpu(bus, io);
        var s = c.Initial;
        cpu.SetRegister("PC", s.Pc); cpu.SetRegister("SP", s.Sp);
        cpu.SetRegister("A", s.A);   cpu.SetRegister("F", s.F);   // F carries the X(3)/Y(5) bits
        cpu.SetRegister("B", s.B);   cpu.SetRegister("C", s.C);
        cpu.SetRegister("D", s.D);   cpu.SetRegister("E", s.E);
        cpu.SetRegister("H", s.H);   cpu.SetRegister("L", s.L);
        cpu.SetRegister("I", s.I);   cpu.SetRegister("R", s.R);
        cpu.SetRegister("IX", s.Ix); cpu.SetRegister("IY", s.Iy);
        cpu.SetRegister("WZ", s.Wz);   // M3.4b: WZ/MEMPTR — BIT y,(HL) reads its high byte for X/Y
        cpu.SetRegister("AF_", s.Af_); cpu.SetRegister("BC_", s.Bc_);  // pair-view set of the alt set
        cpu.SetRegister("DE_", s.De_); cpu.SetRegister("HL_", s.Hl_);
        cpu.Iff1 = s.Iff1; cpu.Iff2 = s.Iff2;
        cpu.Im = s.Im;   // M3.4c: the interrupt mode (set by the ED IM ops)
        cpu.Q = (byte)s.Q;   // the q-pseudo-register drives the SCF/CCF X/Y quirk

        cpu.Step();

        var problems = new List<string>();
        var f = c.Final;
        Check(problems, cpu, "PC", f.Pc, 4); Check(problems, cpu, "SP", f.Sp, 4);
        Check(problems, cpu, "A", f.A, 2);   Check(problems, cpu, "F", f.F, 2);   // F incl. X/Y
        Check(problems, cpu, "B", f.B, 2);   Check(problems, cpu, "C", f.C, 2);
        Check(problems, cpu, "D", f.D, 2);   Check(problems, cpu, "E", f.E, 2);
        Check(problems, cpu, "H", f.H, 2);   Check(problems, cpu, "L", f.L, 2);
        Check(problems, cpu, "I", f.I, 2);   Check(problems, cpu, "R", f.R, 2);
        Check(problems, cpu, "IX", f.Ix, 4); Check(problems, cpu, "IY", f.Iy, 4);
        Check(problems, cpu, "AF_", f.Af_, 4); Check(problems, cpu, "BC_", f.Bc_, 4);
        Check(problems, cpu, "DE_", f.De_, 4); Check(problems, cpu, "HL_", f.Hl_, 4);
        // M3.4c (Piece A): the WZ/MEMPTR model is complete and every Z80 op maintains Q, so the final
        // Q AND WZ AND IM are checked on EVERY case (the M3.4b checkInternal scoping is retired).
        Check(problems, cpu, "WZ", f.Wz, 4);
        if (cpu.Q != f.Q) problems.Add($"Q: expected {f.Q:X2}, got {cpu.Q:X2}");
        if (cpu.Im != f.Im) problems.Add($"IM: expected {f.Im}, got {cpu.Im}");
        if (cpu.Iff1 != f.Iff1) problems.Add($"IFF1: expected {f.Iff1}, got {cpu.Iff1}");
        if (cpu.Iff2 != f.Iff2) problems.Add($"IFF2: expected {f.Iff2}, got {cpu.Iff2}");

        foreach (var e in f.Ram)
            if (inner.Read8(e.Address) != e.Value)
                problems.Add($"RAM[{e.Address:X4}]: expected {e.Value:X2}, got {inner.Read8(e.Address):X2}");

        DiffPorts(problems, io, c.Ports);

        // Cycle COUNT = the T-state total (the cycles array length).
        if (cpu.CycleCount != c.Cycles.Length)
            problems.Add($"cycle count: expected {c.Cycles.Length}, got {cpu.CycleCount}");

        if (!registersOnly)
            DiffBusTrace(problems, bus.Trace, c.Cycles);

        return problems.Count == 0 ? null : Format(c, bus, problems);
    }

    /// <summary>The JIT tier-parity sibling of <see cref="RunCase"/> (M3.5-3a): builds a fresh inner
    /// Z80Cpu over the same program + Io spaces, sets the FULL Z80 state IDENTICALLY to RunCase, wraps it
    /// in <see cref="JittedCpu{Z80Cpu}"/>, drives <c>jit.Run</c> with a one-instruction budget, and diffs
    /// the SAME full state (PC/SP/A/F incl X/Y, B–L, the alt pairs, I/R, IX/IY, WZ, Q, IM, Iff1/Iff2) +
    /// RAM + ports + cycle COUNT off the inner Z80. Every Z80 op falls back to inner.Step in 5-3a, so the
    /// JIT result IS the interpreter result — a green sweep proves the GENERIC COMPILER runs the Z80
    /// faithfully (the J1/J2/J3 deliverable). Fastmem-on means RAM/ROM bypass the bus, so the per-T-state
    /// BUS TRACE is NOT asserted here (Ground truth E — the 6502 JIT sweep asserts state+RAM+cycles, not
    /// the trace); the port ops go through the inner Z80's Io bus on the fallback Step, so the Io trace is
    /// still exact.</summary>
    public static string? RunCaseThroughJit(Z80TomHarteCase c)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported)
            return null;   // JIT-only proof; treat as pass where dynamic code is disabled (AOT)

        var (program, _, ioInner, _) = RentBuses();
        foreach (var e in c.Initial.Ram) program.Write8(e.Address, e.Value);

        // Pre-load the IN ports' return values into the persistent ioInner (re-zeroed by RentBuses each case) so a
        // fallback IN reads the vector's expected byte. The recorded port trace lands on the persistent Io trace.
        foreach (var port in c.Ports)
            if (port.IsRead) ioInner.Write8(port.Address, port.Value);

        // Rent the reused inner Z80 + JIT (lever 4), bound ONCE to the persistent program bus + persistent Io
        // trace. RentJit flushes the block cache + resets the inner Z80 (ResetForReuse) and clears the Io trace so
        // this case starts byte-clean. The all-fallback Z80 writes ports through the inner Z80's Io reference (the
        // persistent Io trace), exactly as a fresh-per-case TracingAddressSpace would. Set ALL state (incl.
        // Iff1/Iff2/Q/Im which have no ICpuCore.SetRegister path) AFTER the reset.
        var (inner, jit, io) = RentJit(program, ioInner);
        ApplyInitialState(inner, c.Initial);

        // Cycle baseline AFTER the reset/reseed: the reused inner accumulates _cycles across cases (Z80 Reset does
        // not zero it), so DiffFinalState asserts the per-case DELTA. For a fresh inner this baseline is 0, so the
        // delta equals the absolute — byte-identical to the original fresh-per-case assertion.
        long cyclesBefore = inner.CycleCount;
        long budget = c.Cycles.Length;   // one instruction's worth of T-states
        jit.Run(ref budget);

        return DiffFinalState(c, inner, program, io, cyclesBefore);
    }

    /// <summary>Set the full Z80 initial state on <paramref name="cpu"/> — the SAME assignments RunCase
    /// makes inline, factored so RunCase and RunCaseThroughJit set state identically (DRY).</summary>
    private static void ApplyInitialState(Z80Cpu cpu, Z80State s)
    {
        cpu.SetRegister("PC", s.Pc); cpu.SetRegister("SP", s.Sp);
        cpu.SetRegister("A", s.A);   cpu.SetRegister("F", s.F);   // F carries the X(3)/Y(5) bits
        cpu.SetRegister("B", s.B);   cpu.SetRegister("C", s.C);
        cpu.SetRegister("D", s.D);   cpu.SetRegister("E", s.E);
        cpu.SetRegister("H", s.H);   cpu.SetRegister("L", s.L);
        cpu.SetRegister("I", s.I);   cpu.SetRegister("R", s.R);
        cpu.SetRegister("IX", s.Ix); cpu.SetRegister("IY", s.Iy);
        cpu.SetRegister("WZ", s.Wz);
        cpu.SetRegister("AF_", s.Af_); cpu.SetRegister("BC_", s.Bc_);
        cpu.SetRegister("DE_", s.De_); cpu.SetRegister("HL_", s.Hl_);
        cpu.Iff1 = s.Iff1; cpu.Iff2 = s.Iff2;
        cpu.Im = s.Im;
        cpu.Q = (byte)s.Q;
    }

    /// <summary>Diff the full final Z80 state + RAM + ports + cycle COUNT off <paramref name="cpu"/> — the
    /// SAME comparison RunCase makes inline (minus the per-T-state bus trace, which fastmem-on bypasses).
    /// Returns null on pass, else the formatted report.</summary>
    private static string? DiffFinalState(Z80TomHarteCase c, Z80Cpu cpu, AddressSpace program, TracingAddressSpace io,
        long cyclesBefore = 0)
    {
        var problems = new List<string>();
        var f = c.Final;
        Check(problems, cpu, "PC", f.Pc, 4); Check(problems, cpu, "SP", f.Sp, 4);
        Check(problems, cpu, "A", f.A, 2);   Check(problems, cpu, "F", f.F, 2);
        Check(problems, cpu, "B", f.B, 2);   Check(problems, cpu, "C", f.C, 2);
        Check(problems, cpu, "D", f.D, 2);   Check(problems, cpu, "E", f.E, 2);
        Check(problems, cpu, "H", f.H, 2);   Check(problems, cpu, "L", f.L, 2);
        Check(problems, cpu, "I", f.I, 2);   Check(problems, cpu, "R", f.R, 2);
        Check(problems, cpu, "IX", f.Ix, 4); Check(problems, cpu, "IY", f.Iy, 4);
        Check(problems, cpu, "AF_", f.Af_, 4); Check(problems, cpu, "BC_", f.Bc_, 4);
        Check(problems, cpu, "DE_", f.De_, 4); Check(problems, cpu, "HL_", f.Hl_, 4);
        Check(problems, cpu, "WZ", f.Wz, 4);
        if (cpu.Q != f.Q) problems.Add($"Q: expected {f.Q:X2}, got {cpu.Q:X2}");
        if (cpu.Im != f.Im) problems.Add($"IM: expected {f.Im}, got {cpu.Im}");
        if (cpu.Iff1 != f.Iff1) problems.Add($"IFF1: expected {f.Iff1}, got {cpu.Iff1}");
        if (cpu.Iff2 != f.Iff2) problems.Add($"IFF2: expected {f.Iff2}, got {cpu.Iff2}");

        foreach (var e in f.Ram)
            if (program.Read8(e.Address) != e.Value)
                problems.Add($"RAM[{e.Address:X4}]: expected {e.Value:X2}, got {program.Read8(e.Address):X2}");

        DiffPorts(problems, io, c.Ports);

        long cyclesCharged = cpu.CycleCount - cyclesBefore;
        if (cyclesCharged != c.Cycles.Length)
            problems.Add($"cycle count: expected {c.Cycles.Length}, got {cyclesCharged}");

        if (problems.Count == 0) return null;
        var sb = new StringBuilder();
        sb.AppendLine($"[JIT] case '{c.Name}'");
        foreach (string problem in problems) sb.AppendLine($"  {problem}");
        return sb.ToString();
    }

    private static void Check(List<string> problems, Z80Cpu cpu, string name, ulong expected, int hex)
    {
        ulong actual = cpu.GetRegister(name);
        if (actual != expected)
            problems.Add($"{name}: expected {expected.ToString("X" + hex)}, got {actual.ToString("X" + hex)}");
    }

    private static void DiffPorts(List<string> problems, TracingAddressSpace io, Z80Port[] expected)
    {
        // The vector's ports array lists the I/O transactions in order. The tracing Io space recorded
        // the CPU's actual port reads/writes; compare them as ordered (address, value, direction).
        var actual = io.Trace;
        if (actual.Count != expected.Length)
            problems.Add($"port count: expected {expected.Length}, got {actual.Count}");
        for (int i = 0; i < Math.Min(actual.Count, expected.Length); i++)
        {
            var a = actual[i]; var e = expected[i];
            if (a.Address != e.Address || a.Value != e.Value || a.IsRead != e.IsRead)
                problems.Add($"port[{i}]: expected {e}, got {(a.IsRead ? "IN" : "OUT")} {a.Address:X4}={a.Value:X2}");
        }
    }

    private static void DiffBusTrace(List<string> problems, IReadOnlyList<BusAccess> trace, Z80Cycle[] expected)
    {
        // The Z80 splits a memory access across T-states: the MREQ T-state ('*-m-') carries the address
        // with NULL data, and the data byte arrives on a following T-state (at the same address for a
        // read/write, or at the refresh address for the M1 opcode fetch). We extract one access per
        // MREQ T-state as (address, direction) and compare to the recorded bus trace IN ORDER. The
        // access VALUES are validated separately by the RAM diff (a read returns ram[addr]; a write
        // lands in ram), so matching (address, direction) order is the bus-trace fidelity that the
        // staged gate's second stage checks — the per-T-state ORDERING, not a redundant value re-check.
        var expectedAccesses = expected
            .Where(c => c.IsMemReq && (c.IsRead || c.IsWrite))
            .Select(c => (c.Address, IsRead: c.IsRead))
            .ToList();
        for (int i = 0; i < Math.Min(trace.Count, expectedAccesses.Count); i++)
        {
            var a = trace[i]; var e = expectedAccesses[i];
            if (a.Address != e.Address || a.IsRead != e.IsRead)
            {
                problems.Add($"bus trace diverges at access {i + 1}: expected {(e.IsRead ? "R" : "W")} {e.Address:X4}, got {(a.IsRead ? "R" : "W")} {a.Address:X4}");
                break;
            }
        }
        if (trace.Count != expectedAccesses.Count)
            problems.Add($"bus access count: expected {expectedAccesses.Count}, got {trace.Count}");
    }

    private static string Format(Z80TomHarteCase c, TracingAddressSpace bus, List<string> problems)
    {
        byte ByteAt(ushort address) => c.Initial.Ram.FirstOrDefault(r => r.Address == address)?.Value ?? 0;
        ushort pc = c.Initial.Pc;
        string disasm = Z80Cpu.Disassemble(ByteAt(pc), ByteAt((ushort)(pc + 1)), ByteAt((ushort)(pc + 2)));
        var sb = new StringBuilder();
        sb.AppendLine($"case '{c.Name}' — {disasm}");
        foreach (string problem in problems)
            sb.AppendLine($"  {problem}");
        return sb.ToString();
    }
}
