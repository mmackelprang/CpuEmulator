using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Tests.Mos6502;   // TracingAddressSpace + BusAccess

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// The 680x0 TomHarte runner SCAFFOLD. In M4.4b it builds a fresh <see cref="M68000Cpu"/> over a tracing
/// wide big-endian bus and sets the FULL initial state (32-bit D/A, usp/ssp, sr, pc, ram) — then returns
/// the <see cref="NotYetExecuted"/> sentinel WITHOUT Stepping (the op bodies are M4.5). M4.5 replaces the
/// sentinel body with: set the initial prefetch into the CPU's 2-word prefetch queue, Step once, then diff
/// registers + ram + the per-transaction bus trace + the FINAL prefetch queue (the new dimension). The
/// state-set half built here is the M4.5-ready scaffold.
/// </summary>
internal static class M68000TomHarteRunner
{
    public const string NotYetExecuted = "M4.4b scaffold: state set, not executed (op bodies are M4.5)";

    public static string RunCase(M68000TomHarteCase c)
    {
        // The wide big-endian program/data bus (M4.2, ADR 0003 Decision 2). The 68000 address space is
        // 24-bit; map the whole range writable so any case's ram + prefetch addresses resolve. (M4.5's
        // sweep will prefer page-windowing this 16 MiB allocation; for the scaffold's cases it is fine.)
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        inner.MapMemory(0x000000, new byte[0x1000000], writable: true);
        foreach (var e in c.Initial.Ram) inner.Write8(e.Address & inner.AddressMask, e.Value);
        var bus = new TracingAddressSpace(inner);

        var cpu = new M68000Cpu(bus);
        var s = c.Initial;
        for (int i = 0; i < 8; i++) cpu.SetRegister($"D{i}", s.D[i]);
        for (int i = 0; i < 7; i++) cpu.SetRegister($"A{i}", s.A[i]);
        // USP/SSP are first-class spec registers (M68000Spec) — settable by name. A7 banks onto whichever
        // the SR S-bit selects; M4.1 exposes USP/SSP directly, so set both explicitly.
        cpu.SetRegister("USP", s.Usp);
        cpu.SetRegister("SSP", s.Ssp);
        cpu.SetRegister("PC", s.Pc);
        cpu.SetRegister("SR", s.Sr);
        // NOTE: the 2-word prefetch queue (s.Prefetch) is parsed + carried (c.Initial/Final.Prefetch) but
        // NOT wired into the CPU here — the M68000Cpu prefetch-queue mechanism is M4.5. M4.5 will: load the
        // initial prefetch, Step, and assert the final prefetch (c.Final.Prefetch).

        // M4.4b: do NOT Step (no op bodies) and do NOT diff. Return the sentinel.
        // TODO(M4.5): replace with — load s.Prefetch, cpu.Step(), then diff D/A/usp/ssp/sr/pc + ram +
        //             bus.Trace (against c.Transactions) + the final prefetch queue (against c.Final).
        _ = bus;   // the tracing bus is wired so M4.5's per-transaction diff has the trace ready.
        return NotYetExecuted;
    }
}
