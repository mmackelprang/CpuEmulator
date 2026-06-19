using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>Wraps a fresh interpreter M8086Cpu + its 20-bit little-endian program AddressSpace in a Tier-1
/// JittedCpu&lt;M8086Cpu&gt; for the 8086 JIT tier-parity sweep (the M68000JittedCpuFactory analog, M5.6).
/// In M5.6 every 8086 op fell back to inner.Step (the empty-Ops NeedsFallback descriptors), so the JIT result
/// WAS the interpreter result. As of M6 PR-B/PR-C the MOV + ALU families EMIT real IL through the JIT; the
/// remaining families still fall back. This factory itself is UNCHANGED across that transition — it just builds a
/// plain JittedCpu&lt;M8086Cpu&gt;, which now emits the implemented families and falls back for the rest (the gate
/// + emit arms live in the generated descriptors / BlockCompiler, not here). A green sweep proves the GENERIC
/// COMPILER runs the 8086 faithfully. The 8086 handles IN/OUT in the interpreter body (open-bus), not a Port
/// callout, so no io bus is passed (the JittedCpu ctor's `ioBus ?? bus` default applies).</summary>
internal static class M8086JittedCpuFactory
{
    public static (JittedCpu<M8086Cpu> Jit, M8086Cpu Inner) Create(AddressSpace program)
    {
        var inner = new M8086Cpu(program);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, program);
        return (jit, inner);
    }
}
