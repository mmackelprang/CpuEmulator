using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>Wraps a fresh interpreter M68000Cpu + its 24-bit BigEndian program AddressSpace in a Tier-1
/// JittedCpu&lt;M68000Cpu&gt; for the 68000 JIT tier-parity sweep (the Z80JittedCpuFactory analog, M4.6).
/// In M4 every 68000 op falls back to inner.Step, so the JIT result IS the interpreter result — a green
/// sweep proves the GENERIC COMPILER runs the 68000 faithfully (all-fallback, zero JIT-assembly change).
/// The 68000 has NO Io space, so no io bus is passed (the JittedCpu ctor's `ioBus ?? bus` default applies).</summary>
internal static class M68000JittedCpuFactory
{
    public static (JittedCpu<M68000Cpu> Jit, M68000Cpu Inner) Create(AddressSpace program)
    {
        var inner = new M68000Cpu(program);
        var jit = new JittedCpu<M68000Cpu>(inner, M68000Cpu.JitTarget, program);
        return (jit, inner);
    }
}
