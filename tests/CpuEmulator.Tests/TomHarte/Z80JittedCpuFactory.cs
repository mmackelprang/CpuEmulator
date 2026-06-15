using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>Wraps a fresh interpreter Z80Cpu + its program AddressSpace + Io space in a Tier-1
/// JittedCpu&lt;Z80Cpu&gt; for the Z80 JIT tier-parity sweep (the M2 6502 JittedCpuFactory analog). In
/// 5-3a every Z80 op falls back to inner.Step, so the JIT result IS the interpreter result — a green sweep
/// proves the GENERIC COMPILER runs the Z80 faithfully (the J1/J2/J3 deliverable).</summary>
internal static class Z80JittedCpuFactory
{
    public static (JittedCpu<Z80Cpu> Jit, Z80Cpu Inner) Create(AddressSpace program, IAddressSpace io)
    {
        var inner = new Z80Cpu(program, io);
        var jit = new JittedCpu<Z80Cpu>(inner, Z80Cpu.JitTarget, program, io);
        return (jit, inner);
    }
}
