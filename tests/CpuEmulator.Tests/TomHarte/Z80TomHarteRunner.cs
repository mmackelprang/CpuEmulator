using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
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
    /// <summary>Run one case. <paramref name="checkInternal"/> additionally diffs the final Q and WZ —
    /// enforced for the M3.4b-implemented surface (the CB plane + the 4 rotate-accumulators), whose ops
    /// maintain Q and leave WZ invariant. The general base plane does NOT model WZ writes (CALL/JP/RET/…
    /// update WZ — M3.4c) nor maintain Q on the shared 6502-class ops (LD r,r' etc.), so it keeps the
    /// M3.4a posture (registers/RAM/ports/cycles/trace, no Q/WZ); enabling those checks for it would
    /// require out-of-scope base-plane WZ + shared-class-Q modeling.</summary>
    public static string? RunCase(Z80TomHarteCase c, bool registersOnly = false, bool checkInternal = false)
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        foreach (var e in c.Initial.Ram) inner.Write8(e.Address, e.Value);
        var bus = new TracingAddressSpace(inner);

        var ioInner = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
        ioInner.MapMemory(0x0000, new byte[0x10000], writable: true);
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
        if (checkInternal)
        {
            // M3.4b: the CB plane + rotate-accumulators maintain Q and leave WZ invariant.
            Check(problems, cpu, "WZ", f.Wz, 4);
            if (cpu.Q != f.Q) problems.Add($"Q: expected {f.Q:X2}, got {cpu.Q:X2}");
        }
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
