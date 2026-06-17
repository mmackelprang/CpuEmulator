using System.Reflection;
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M68000;
using CpuEmulator.Jit;   // BlockCompiler<>, JittedCpu<>, Fastmem, JitOptions
using CpuEmulator.Tests.TomHarte;   // M68000TomHarteTheory, the loader/runner/corpus (smoke fact)
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

    [Fact]
    public void Generic_compiler_discovers_a_68000_block_as_a_single_fallback()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24,
            endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
        bus.Write16(0x001000, 0x4E71);   // NOP (operword); any 68000 word — the table is empty so it falls back
        var cpu = new M68000Cpu(bus);
        var opts = new JitOptions();
        var compiler = new BlockCompiler<M68000Cpu>(cpu, M68000Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);

        var run = compiler.Discover(0x1000);
        Assert.Single(run);                       // every 68000 block is ONE op (Undefined ends the block)
        Assert.True(run[0].D.NeedsFallback);      // ... that op falls back to the interpreter
        Assert.True(run[0].D.EndsBlock);
    }

    [Fact]
    public void Register_map_builds_against_all_68000_register_names()
    {
        // The map must NOT throw on any of D0-D7/A0-A6/USP/SSP/PC/SR (all are field-backed on M68000Cpu —
        // the 68000 has no composed pair-view PROPERTIES like the Z80, so every name resolves).
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
        var cpu = new M68000Cpu(bus);
        var opts = new JitOptions();
        _ = new BlockCompiler<M68000Cpu>(cpu, M68000Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);  // no throw
        foreach (var name in M68000Cpu.JitTarget.RegisterNames)
            Assert.True(typeof(M68000Cpu).GetField(name) is not null, $"register '{name}' has no field");
    }

    [Fact]
    public void JittedCpu_of_68000_runs_a_NOP_via_fallback_identically_to_the_interpreter()
    {
        // GAP-3 guard: one instruction through JittedCpu<M68000Cpu>.Run is byte-identical to a single Step.
        static (uint pc, long cyc) RunOne(bool throughJit)
        {
            var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24, endianness: Endianness.BigEndian);
            bus.MapMemory(0x000000, new byte[0x1000000], writable: true);
            bus.Write16(0x001000, 0x4E71);     // NOP at PC; prefetch refill word at PC+2
            bus.Write16(0x001002, 0x4E71);
            var inner = new M68000Cpu(bus);
            inner.SetRegister("PC", 0x001000);
            inner.SetRegister("SR", 0x2700);   // supervisor, ints masked (a benign live SR)
            if (throughJit)
            {
                var jit = new JittedCpu<M68000Cpu>(inner, M68000Cpu.JitTarget, bus);
                // A budget of 1 cycle runs EXACTLY ONE block iteration: `while (budget > 0)` passes once
                // (1 > 0), the single fallback op charges the NOP's full cycle cost (driving budget < 0), and
                // the loop exits — one instruction, mirroring the interpreter's single Step(). A larger budget
                // would let Run loop and execute several NOPs (the JIT dispatcher is a budget-driven loop, not
                // a one-shot), which is correct JIT behavior but not a single-instruction parity comparison.
                long budget = 1;
                jit.Run(ref budget);
            }
            else inner.Step();
            return ((uint)inner.GetRegister("PC"), inner.CycleCount);
        }
        var (jpc, jcyc) = RunOne(throughJit: true);
        var (ipc, icyc) = RunOne(throughJit: false);
        Assert.Equal(ipc, jpc);     // the fallback set the real 24-bit PC; the ushort cache key never aliased
        Assert.Equal(icyc, jcyc);   // the fallback charged the same cycles (CycleCount delta)
    }

    [M68000TomHarteTheory]   // skips when the 680x0 vectors are absent (same attribute the data-axis sweeps use)
    [InlineData("NOP.json.gz")]
    public void One_family_file_is_tier_parity_green_through_the_JIT(string file)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = System.IO.Path.Combine(dir, file);
        Assert.True(System.IO.File.Exists(path), $"vector file missing: {path}");
        var cases = M68000TomHarteLoader.LoadFile(path);
        int executed = 0;
        var failures = new System.Collections.Generic.List<string>();
        foreach (var c in cases)
        {
            // Carry the interpreter sweeps' corpus-artifact exclusions forward (Refinement 3). NOP.json.gz has
            // neither artifact, so this is a no-op here — included for symmetry with the headline sweep.
            if (M68000DataAxisCorpus.IsExcludedCase(c)) continue;
            var rr = M68000TomHarteRunner.RunCaseThroughJit(c, assertExceptions: true);
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) continue;
            executed++;
            if (rr is not null) { failures.Add(rr); if (failures.Count >= 5) break; }
        }
        Assert.True(executed > 0, $"{file}: 0 executed cases");
        Assert.True(failures.Count == 0, $"{file}: {failures.Count} tier-parity failures:\n" + string.Join("\n", failures));
    }
}
