using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// The 8088 TomHarte DATA-axis runner — M5.4 SCAFFOLD. It proves the state-set wiring (bus + registers)
/// compiles and runs against the real CPU surface, but it does NOT yet Step: the 8086 instruction table is
/// empty until the M5.5 op-body milestones (M5.5a–d), so any Step would route to HandleUndefinedOpcode. The
/// runner therefore sets up the case's initial state and returns the <see cref="NotYetExecuted"/> sentinel.
///
/// <para>The correctness pieces this scaffold backs — the SPARSE-final merge (case
/// <see cref="M8088TomHarteCase.MergedFinalRegs"/>), the mask-aware flag compare
/// (<see cref="M8088Metadata.ApplyFlagsMask"/>), and the ram diff — are exercised by the loader tests DIRECTLY,
/// not by stepping the CPU. When the op bodies land (M5.5), this runner gains a Step + a regs/flags-mask/ram
/// diff against <see cref="M8088TomHarteCase.MergedFinalRegs"/>; the queue + cycles stay carried-not-asserted
/// until the timing axis (M5.5e).</para>
/// </summary>
internal static class M8088TomHarteRunner
{
    /// <summary>The scaffold sentinel: the data-axis diff cannot run until the M5.5 op bodies exist, so
    /// <see cref="RunCase"/> sets the state and returns this instead of asserting a Step result.</summary>
    public const string NotYetExecuted = "NOT-EXECUTED(M5.5): op bodies land in M5.5a-d";

    /// <summary>
    /// Build a fresh 20-bit little-endian bus, install <c>initial.ram</c>, construct an <see cref="M8086Cpu"/>,
    /// and set the full 14-register initial state via the generated <c>SetRegister</c>. Because there are NO op
    /// bodies yet (M5.5), this does NOT Step — it returns <see cref="NotYetExecuted"/> once the state is wired.
    /// The <c>queue</c> + <c>cycles</c> are IGNORED (the data axis does not need them; the timing axis is M5.5e).
    /// </summary>
    public static string RunCase(M8088TomHarteCase c)
    {
        // 20-bit physical address space, little-endian (the 8086/8088 default — NO BigEndian), per ADR 0005 D2.
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        uint mask = bus.AddressMask;
        foreach (var cell in c.Initial.Ram)
            bus.Write8(cell.Address & mask, cell.Value);

        var cpu = new M8086Cpu(bus);
        var r = c.Initial.Regs;
        cpu.SetRegister("AX", r.Ax);
        cpu.SetRegister("BX", r.Bx);
        cpu.SetRegister("CX", r.Cx);
        cpu.SetRegister("DX", r.Dx);
        cpu.SetRegister("CS", r.Cs);
        cpu.SetRegister("SS", r.Ss);
        cpu.SetRegister("DS", r.Ds);
        cpu.SetRegister("ES", r.Es);
        cpu.SetRegister("SP", r.Sp);
        cpu.SetRegister("BP", r.Bp);
        cpu.SetRegister("SI", r.Si);
        cpu.SetRegister("DI", r.Di);
        cpu.SetRegister("IP", r.Ip);
        cpu.SetRegister("FLAGS", r.Flags);

        // The queue + cycles are carried on the case but the data axis ignores them (timing axis = M5.5e).
        // No Step: the instruction table is empty until M5.5, so there is nothing to assert yet.
        return NotYetExecuted;
    }
}
