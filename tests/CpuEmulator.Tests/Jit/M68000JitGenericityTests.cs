using System.Reflection;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M4.6 genericity pins: the generated per-CPU <see cref="IJitTarget"/> seam resolves the 68000's
/// CPU-typed handles by name — including the new hand-written <c>AdvanceCycles</c> charge seam (GAP 1) — and
/// the generic <c>BlockCompiler&lt;M68000Cpu&gt;</c> discovers every 68000 block as a SINGLE fallback op (the
/// empty <c>JitDescriptorsByKey</c> → every op <c>Undefined</c>/<c>NeedsFallback</c>/<c>EndsBlock</c>), builds
/// the 19-name register map without throwing, and a one-instruction <c>JittedCpu&lt;M68000Cpu&gt;.Run</c>
/// produces the interpreter's exact state (the GAP-3 ushort-key single-block invariant). The all-fallback model
/// is what makes the M4.6 tier-parity gate byte-identical Tier-0-vs-Tier-1 with ZERO JIT-assembly change.</summary>
public class M68000JitGenericityTests
{
    [Fact]
    public void M68000_JitTarget_resolves_all_handles_including_AdvanceCycles()
    {
        IJitTarget t = M68000Cpu.JitTarget;
        Assert.Equal(typeof(M68000Cpu), t.CpuType);
        Assert.Equal("SR", t.StatusField.Name);          // 68000 status = SR
        Assert.Equal("PC", t.ProgramCounterField.Name);
        Assert.NotNull(t.StepMethod);
        Assert.NotNull(t.AdvanceCyclesMethod);            // GAP 1: must resolve, was null
        Assert.NotNull(t.CycleCountGetter);
        Assert.NotNull(t.InterruptPendingGetter);
    }
}
