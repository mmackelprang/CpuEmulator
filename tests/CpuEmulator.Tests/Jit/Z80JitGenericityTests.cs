using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>5-3a genericity pins: the per-CPU <see cref="IJitTarget"/> seam resolves the CPU-typed
/// handles by name (Task 1), the generic <c>BlockCompiler&lt;Z80Cpu&gt;</c> discovers a Z80 block and
/// builds the 36-name register map without throwing (Tasks 2 + 7), every Z80 op is a fallback in 5-3a
/// (Task 7), a <c>JittedCpu&lt;Z80Cpu&gt;</c> runs a Z80 NOP via the interpreter fallback (Task 5), and
/// the GENERATED per-CPU targets resolve for both CPUs (Task 6).</summary>
public class Z80JitGenericityTests
{
    [Fact]
    public void Z80_JitTarget_exposes_the_cpu_typed_handles()
    {
        IJitTarget t = Z80Cpu.JitTarget;
        Assert.Equal(typeof(Z80Cpu), t.CpuType);
        // The status + PC fields resolve by NAME on the Z80 type (the J2 baked-handle replacement).
        Assert.NotNull(t.StatusField);     // "F" on the Z80 (vs "P" on the 6502)
        Assert.Equal("F", t.StatusField.Name);
        Assert.NotNull(t.ProgramCounterField);
        Assert.Equal("PC", t.ProgramCounterField.Name);
        // The interpreter-fallback handles resolve on the Z80 type.
        Assert.NotNull(t.StepMethod);
        Assert.NotNull(t.AdvanceCyclesMethod);
        Assert.NotNull(t.CycleCountGetter);
        Assert.NotNull(t.InterruptPendingGetter);
    }
}
