using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Wraps a fresh interpreter <see cref="Mos6502Cpu"/> + its concrete <see cref="AddressSpace"/>
/// in a Tier-1 <see cref="JittedCpu"/> for the TomHarte runner (Task 7). The factory hands back
/// both the wrapper (the <see cref="ICpuCore"/> the runner drives) and the inner interpreter
/// (the state owner the differ reads). The JIT TomHarte path drives <see cref="JittedCpu.Run"/>
/// rather than <see cref="JittedCpu.Step"/> (Step delegates to the interpreter), so block
/// compilation + execution actually occurs — see <see cref="TomHarteRunner.RunCaseThroughJit"/>.
/// </summary>
internal static class JittedCpuFactory
{
    /// <summary>Wrap an interpreter over <paramref name="space"/> in a default-options JIT
    /// (fastmem on — state + cycle parity, not bus-trace; Ground truth E).</summary>
    public static (JittedCpu Jit, Mos6502Cpu Inner) Create(AddressSpace space)
    {
        var inner = new Mos6502Cpu(space);
        return (new JittedCpu(inner, space), inner);
    }
}
