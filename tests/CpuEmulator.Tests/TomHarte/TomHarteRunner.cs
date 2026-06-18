using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using CpuEmulator.Tests.Mos6502;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Executes TomHarte SingleStepTests cases against a fresh CPU with a full 64 KiB RAM and
/// a tracing bus, then diffs the result against the expected final state and cycle log.
/// </summary>
internal static class TomHarteRunner
{
    // Per-worker-thread reusable 64 KiB program bus (lever 2 — the 68000 _ramArena pattern, ported). RunCase is
    // synchronous (no await), so [ThreadStatic] is reentrancy-safe: a worker thread never reenters RunCase.
    [ThreadStatic] private static AddressSpace? _busTls;
    [ThreadStatic] private static byte[]? _ramTls;

    private static (AddressSpace bus, byte[] ram) RentBus()
    {
        if (_busTls is null)
        {
            _ramTls = new byte[0x10000];
            _busTls = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
            _busTls.MapMemory(0x0000, _ramTls, writable: true);
        }
        _busTls.ClearMappedBacking(_ramTls!);   // re-zero; mapping persists → identical to a fresh new byte[0x10000]
        return (_busTls, _ramTls!);
    }

    // Per-worker reused JIT (lever 4). Built ONCE per worker thread bound to the pooled raw program bus (the JIT
    // path uses the RAW space, not the TracingAddressSpace); ResetForReuse() flushes the block cache between cases
    // so the SAME (ushort)PC recompiles from the new case's bytes (the isolation invariant). The inner Mos6502Cpu
    // is wrapped once; SetRegister re-seeds it per case. NOTE: Mos6502Cpu.Reset() does NOT zero the cycle counter
    // (it charges the 7-cycle reset sequence), so the reused inner's CycleCount accumulates across cases — the JIT
    // path therefore asserts the per-case cycle DELTA (CycleCount captured after the reset, before Run) rather than
    // the absolute, which is byte-identical to the fresh-per-case absolute assert (fresh CycleCount starts at 0).
    [ThreadStatic] private static JittedCpu<Mos6502Cpu>? _jitTls;
    [ThreadStatic] private static Mos6502Cpu? _jitInnerTls;

    private static (JittedCpu<Mos6502Cpu> Jit, Mos6502Cpu Inner) RentJit(AddressSpace space)
    {
        if (_jitTls is null)
            (_jitTls, _jitInnerTls) = JittedCpuFactory.Create(space);
        else
            _jitTls.ResetForReuse();   // flush cache + clear chains + reset inner — bound to the SAME pooled bus
        return (_jitTls, _jitInnerTls!);
    }

    /// <summary>
    /// Executes one vector case against a fresh CPU + full-64KiB RAM with a tracing bus.
    /// Returns null on pass, or a multi-line failure report with the disassembled instruction
    /// and a cycle-by-cycle expected/actual table.
    /// </summary>
    public static string? RunCase(TomHarteCase testCase)
    {
        var (inner, _) = RentBus();
        foreach (var entry in testCase.Initial.Ram)
            inner.Write8(entry.Address, entry.Value);

        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", testCase.Initial.Pc);
        cpu.SetRegister("S", testCase.Initial.S);
        cpu.SetRegister("A", testCase.Initial.A);
        cpu.SetRegister("X", testCase.Initial.X);
        cpu.SetRegister("Y", testCase.Initial.Y);
        cpu.SetRegister("P", testCase.Initial.P);

        cpu.Step();

        var problems = new List<string>();
        CheckRegister(problems, cpu, "PC", testCase.Final.Pc);
        CheckRegister(problems, cpu, "S",  testCase.Final.S);
        CheckRegister(problems, cpu, "A",  testCase.Final.A);
        CheckRegister(problems, cpu, "X",  testCase.Final.X);
        CheckRegister(problems, cpu, "Y",  testCase.Final.Y);
        CheckRegister(problems, cpu, "P",  testCase.Final.P);

        foreach (var entry in testCase.Final.Ram)
        {
            byte actual = inner.Read8(entry.Address);
            if (actual != entry.Value)
                problems.Add($"RAM[{entry.Address:X4}]: expected {entry.Value:X2}, got {actual:X2}");
        }

        if (cpu.CycleCount != testCase.Cycles.Length)
            problems.Add($"cycle count: expected {testCase.Cycles.Length}, got {cpu.CycleCount}");
        for (int i = 0; i < Math.Min(bus.Trace.Count, testCase.Cycles.Length); i++)
        {
            var expected = testCase.Cycles[i];
            var actual   = bus.Trace[i];
            if (actual.Address != expected.Address || actual.Value != expected.Value
                || actual.IsRead != expected.IsRead)
            {
                problems.Add($"bus trace diverges at cycle {i + 1} (table below)");
                break;
            }
        }

        return problems.Count == 0 ? null : Format(testCase, bus, problems);
    }

    /// <summary>
    /// Executes one vector case through a Tier-1 <see cref="JittedCpu"/> wrapping a fresh
    /// interpreter + full-64KiB RAM, and diffs the result against the expected final state,
    /// RAM, and cycle count. **It does NOT diff the bus trace** — fastmem bypasses the bus for
    /// RAM/ROM (Ground truth E: JIT parity is state + cycle-count equivalence, not bus-trace
    /// equivalence while fastmem is on). Trace-equivalence is the DisableFastmem mode, pinned
    /// separately by JitTraceEquivalenceTests.
    ///
    /// Crucially, TomHarte exercises ONE instruction, and <see cref="JittedCpu.Step"/> delegates
    /// to the interpreter — so a naive Step run would just re-test the interpreter. This drives
    /// <see cref="JittedCpu.Run"/> with a budget equal to the case's cycle count (one
    /// instruction's worth), forcing block compilation + execution of the single instruction.
    /// ADC/SBC/BRK/RTI run through the JIT's interpreter-fallback path (still a valid parity
    /// check — the JIT must produce the interpreter's result whether by emit or by fallback).
    /// Returns null on pass, or a multi-line failure report.
    /// </summary>
    public static string? RunCaseThroughJit(TomHarteCase testCase)
    {
        var (space, _) = RentBus();
        foreach (var entry in testCase.Initial.Ram)
            space.Write8(entry.Address, entry.Value);

        // Rent this worker thread's reused Tier-1 JittedCpu<Mos6502Cpu> (lever 4) bound to the SAME pooled bus.
        // RentJit flushes the block cache + resets the inner CPU between cases (ResetForReuse) so the SAME (ushort)PC
        // recompiles from THIS case's bytes — re-seed the registers on the rent's inner below.
        var (jit, inner) = RentJit(space);
        inner.SetRegister("PC", testCase.Initial.Pc);
        inner.SetRegister("S", testCase.Initial.S);
        inner.SetRegister("A", testCase.Initial.A);
        inner.SetRegister("X", testCase.Initial.X);
        inner.SetRegister("Y", testCase.Initial.Y);
        inner.SetRegister("P", testCase.Initial.P);

        // Capture the cycle baseline AFTER the reset/reseed: the reused inner accumulates _cycles across cases (Reset
        // does not zero it), so we assert the per-case DELTA. For a fresh inner this baseline is 0, so the delta
        // equals the absolute — byte-identical to the original fresh-per-case assertion.
        long cyclesBefore = inner.CycleCount;
        long budget = testCase.Cycles.Length; // one instruction's worth of cycles
        jit.Run(ref budget);

        var problems = new List<string>();
        CheckRegister(problems, inner, "PC", testCase.Final.Pc);
        CheckRegister(problems, inner, "S",  testCase.Final.S);
        CheckRegister(problems, inner, "A",  testCase.Final.A);
        CheckRegister(problems, inner, "X",  testCase.Final.X);
        CheckRegister(problems, inner, "Y",  testCase.Final.Y);
        CheckRegister(problems, inner, "P",  testCase.Final.P);

        foreach (var entry in testCase.Final.Ram)
        {
            byte actual = space.Read8(entry.Address);
            if (actual != entry.Value)
                problems.Add($"RAM[{entry.Address:X4}]: expected {entry.Value:X2}, got {actual:X2}");
        }

        long cyclesCharged = inner.CycleCount - cyclesBefore;
        if (cyclesCharged != testCase.Cycles.Length)
            problems.Add($"cycle count: expected {testCase.Cycles.Length}, got {cyclesCharged}");

        if (problems.Count == 0)
            return null;

        ushort pc = testCase.Initial.Pc;
        byte ByteAt(ushort address) =>
            testCase.Initial.Ram.FirstOrDefault(r => r.Address == address)?.Value ?? 0;
        string disassembly = Mos6502Cpu.Disassemble(
            ByteAt(pc), ByteAt((ushort)(pc + 1)), ByteAt((ushort)(pc + 2)));
        var sb = new StringBuilder();
        sb.AppendLine($"case '{testCase.Name}' (JIT) — {disassembly}");
        foreach (string problem in problems)
            sb.AppendLine($"  {problem}");
        return sb.ToString();
    }

    private static void CheckRegister(
        List<string> problems, Mos6502Cpu cpu, string name, ulong expected)
    {
        ulong actual = cpu.GetRegister(name);
        if (actual != expected)
        {
            // 16-bit PC prints 4 hex digits; the 8-bit registers print 2.
            string fmt = name == "PC" ? "X4" : "X2";
            problems.Add($"{name}: expected {expected.ToString(fmt)}, got {actual.ToString(fmt)}");
        }
    }

    private static string Format(
        TomHarteCase testCase, TracingAddressSpace bus, List<string> problems)
    {
        byte ByteAt(ushort address) =>
            testCase.Initial.Ram.FirstOrDefault(r => r.Address == address)?.Value ?? 0;

        ushort pc = testCase.Initial.Pc;
        string disassembly = Mos6502Cpu.Disassemble(
            ByteAt(pc), ByteAt((ushort)(pc + 1)), ByteAt((ushort)(pc + 2)));

        var sb = new StringBuilder();
        sb.AppendLine($"case '{testCase.Name}' — {disassembly}");
        foreach (string problem in problems)
            sb.AppendLine($"  {problem}");
        sb.AppendLine($"  {"cycle",5}  {"expected",-14}  actual");
        for (int i = 0; i < Math.Max(testCase.Cycles.Length, bus.Trace.Count); i++)
        {
            string expected = i < testCase.Cycles.Length ? testCase.Cycles[i].ToString() : "—";
            string actual   = i < bus.Trace.Count        ? bus.Trace[i].ToString()        : "—";
            sb.AppendLine($"  {i + 1,5}  {expected,-14}  {actual}");
        }
        return sb.ToString();
    }
}
