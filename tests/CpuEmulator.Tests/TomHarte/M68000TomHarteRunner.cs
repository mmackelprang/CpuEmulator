using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Tests.Mos6502;   // TracingAddressSpace + BusAccess

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// The 680x0 TomHarte runner (M4.5a): set the full initial state, Step once, and diff registers + RAM + the
/// per-transaction word/long bus trace + the cycle count against the case's final/transactions. Returns null
/// on pass, a formatted report on failure (the Z80 runner shape). M4.5a asserts the MOVE family; final.prefetch
/// is parsed-but-not-asserted (the prefetch-queue refill is M4.5d — D-C (resolved)).
/// </summary>
internal static class M68000TomHarteRunner
{
    public static string? RunCase(M68000TomHarteCase c)
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 24,
            endianness: Endianness.BigEndian);
        inner.MapMemory(0x000000, new byte[0x1000000], writable: true);
        foreach (var e in c.Initial.Ram) inner.Write8(e.Address & inner.AddressMask, e.Value);
        var bus = new TracingAddressSpace(inner);

        var cpu = new M68000Cpu(bus);
        var s = c.Initial;
        for (int i = 0; i < 8; i++) cpu.SetRegister($"D{i}", s.D[i]);
        for (int i = 0; i < 7; i++) cpu.SetRegister($"A{i}", s.A[i]);
        cpu.SetRegister("USP", s.Usp);
        cpu.SetRegister("SSP", s.Ssp);
        cpu.SetRegister("PC", s.Pc);
        cpu.SetRegister("SR", s.Sr);
        // D-C (resolved): the operword is fetched from bus[pc] (the case's RAM carries it). The initial prefetch is
        // the already-prefetched operword; the live fetch re-reads from the bus. final.prefetch is NOT asserted
        // (the prefetch-queue refill mechanism is M4.5d).
        // TODO(M4.5d): assert c.Final.Prefetch once the prefetch-queue refill is modeled.

        cpu.Step();

        var problems = new List<string>();
        void Check(string name, uint expected)
        {
            uint got = (uint)cpu.GetRegister(name);
            if (got != expected) problems.Add($"{name}: expected {expected:X8}, got {got:X8}");
        }
        var f = c.Final;
        for (int i = 0; i < 8; i++) Check($"D{i}", f.D[i]);
        for (int i = 0; i < 7; i++) Check($"A{i}", f.A[i]);
        Check("USP", f.Usp);
        Check("SSP", f.Ssp);
        Check("PC", f.Pc);
        { uint gotSr = (uint)cpu.GetRegister("SR"); if (gotSr != f.Sr) problems.Add($"SR: expected {f.Sr:X4}, got {gotSr:X4}"); }

        // RAM diff via the INNER (non-tracing) space so the verification read is not itself traced.
        foreach (var e in f.Ram)
            if (inner.Read8(e.Address & inner.AddressMask) != e.Value)
                problems.Add($"RAM[{e.Address:X6}]: expected {e.Value:X2}, got {inner.Read8(e.Address & inner.AddressMask):X2}");

        // Cycle count = the case's length (Σ transaction cycles — CONFIRMED in M4.4b).
        if (cpu.CycleCount != c.Length)
            problems.Add($"cycle count: expected {c.Length}, got {cpu.CycleCount}");

        DiffBusTrace(problems, bus.Trace, c.Transactions);

        return problems.Count == 0 ? null : Format(c, problems);
    }

    /// <summary>Compare the recorded word/long BusAccess trace against the case's non-idle transactions, in
    /// order: address + direction + size + value (richer than the Z80's address-only diff — Recon §C). Idle
    /// ("n") transactions have no bus access, so they are filtered out of the expected list.</summary>
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
