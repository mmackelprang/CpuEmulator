using CpuEmulator.Core;
using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;   // BlockCompiler<>, JittedCpu<>, Fastmem, JitOptions
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M5.6 genericity pins: the generated per-CPU <see cref="IJitTarget"/> seam resolves the 8086's
/// CPU-typed handles by name — including the new hand-written <c>AdvanceCycles</c> charge seam (GAP 1) — and
/// the generic <c>BlockCompiler&lt;M8086Cpu&gt;</c> discovers every 8086 block as a SINGLE fallback op (every
/// <c>JitDescriptorsByKey</c> entry is <c>NeedsFallback</c>/<c>EndsBlock</c> with empty <c>Ops[]</c>), builds
/// the register map without throwing (even over the composed AX/BX/CX/DX pair-view PROPERTIES, which the map
/// SKIPS — see fact 3), and a one-instruction <c>JittedCpu&lt;M8086Cpu&gt;.Run</c> produces the interpreter's
/// exact state (the GAP-3 ushort-key single-block invariant — IP is already a ushort, so the cache key is
/// exact). The all-fallback model is what makes the M5.6 tier-parity gate byte-identical Tier-0-vs-Tier-1 with
/// ZERO JIT-assembly change.</summary>
public class M8086JitGenericityTests
{
    [Fact]
    public void M8086_JitTarget_resolves_all_handles_including_AdvanceCycles()
    {
        IJitTarget t = M8086Cpu.JitTarget;
        Assert.Equal(typeof(M8086Cpu), t.CpuType);
        Assert.Equal("FLAGS", t.StatusField.Name);        // 8086 status = FLAGS
        Assert.Equal("IP", t.ProgramCounterField.Name);
        Assert.NotNull(t.StepMethod);
        Assert.NotNull(t.AdvanceCyclesMethod);            // GAP 1: must resolve, was null (→ NRE in BlockCompiler ctor)
        Assert.NotNull(t.CycleCountGetter);
        Assert.NotNull(t.InterruptPendingGetter);
    }

    [Fact]
    public void Generic_compiler_discovers_an_8086_block_as_a_single_fallback()
    {
        // 20-bit little-endian program space (the 8086/8088 default — NO BigEndian); map the whole 1 MB.
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        bus.Write8(0x1000, 0x90);   // NOP — a single-byte plain opcode; any byte works (the table is all-fallback)
        var cpu = new M8086Cpu(bus);
        var opts = new JitOptions();
        var compiler = new BlockCompiler<M8086Cpu>(cpu, M8086Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);

        var run = compiler.Discover(0x1000);
        Assert.Single(run);                       // every 8086 block is ONE op (NeedsFallback ends the block)
        Assert.True(run[0].D.NeedsFallback);      // ... that op falls back to the interpreter
        Assert.True(run[0].D.EndsBlock);
    }

    [Fact]
    public void Register_map_builds_against_all_8086_register_names()
    {
        // The map must NOT throw building the register-name→FieldInfo map. UNLIKE the 68000 (all field-backed),
        // the 8086's AX/BX/CX/DX are composed pair-view PROPERTIES (get/set over AH/AL etc.) — NOT fields — so
        // GetField returns null for them and the BlockCompiler ctor SKIPS them (it builds the map with
        // `if (CpuType.GetField(name) is { } f)`, exactly as it does for the Z80's AF/BC/DE/HL pair-views). No
        // emitted op references a pair in all-fallback M5, so a field-less pair-view is fine. The load-bearing
        // claim is that the map BUILDS; the per-name resolution accepts a FIELD (AH/AL/.../SP/IP/FLAGS) OR a
        // PROPERTY (AX/BX/CX/DX) — whichever the generator emitted (a pair-view is a property, a leaf is a field).
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        var cpu = new M8086Cpu(bus);
        var opts = new JitOptions();
        _ = new BlockCompiler<M8086Cpu>(cpu, M8086Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);  // no throw
        foreach (var name in M8086Cpu.JitTarget.RegisterNames)
            Assert.True(typeof(M8086Cpu).GetField(name) is not null || typeof(M8086Cpu).GetProperty(name) is not null,
                $"register '{name}' has neither a field nor a property");
    }

    [Fact]
    public void JittedCpu_of_8086_runs_a_NOP_via_fallback_identically_to_the_interpreter()
    {
        // GAP-3 guard: one instruction through JittedCpu<M8086Cpu>.Run is byte-identical to a single Step. The
        // (ushort)IP cache key is exact (IP is already a ushort — no 24-bit-PC truncation like the 68000), and
        // the fallback inner.Step does the real (CS<<4)+IP segmented fetch.
        static (ushort ip, long cyc) RunOne(bool throughJit)
        {
            var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
            bus.MapMemory(0, new byte[0x100000], writable: true);
            // CS=0, IP=0x1000 ⇒ physical (CS<<4)+IP = 0x1000. Write a NOP there (and the next byte, for safety).
            bus.Write8(0x1000, 0x90);
            bus.Write8(0x1001, 0x90);
            var inner = new M8086Cpu(bus);
            inner.SetRegister("CS", 0);
            inner.SetRegister("IP", 0x1000);
            inner.SetRegister("FLAGS", 0x0002);   // a benign live FLAGS (reserved bit 1 set); NOP changes only IP/cycles
            if (throughJit)
            {
                var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, bus);
                // A budget of 1 cycle runs EXACTLY ONE block iteration: `while (budget > 0)` passes once (1 > 0),
                // the single fallback op charges the NOP's full cycle cost (driving budget <= 0 — every 8086
                // instruction charges >= 1 cycle via the ReadBus/fetch loop), and the loop exits — one
                // instruction, mirroring the interpreter's single Step(). A larger budget would let Run loop and
                // execute several NOPs (the JIT dispatcher is a budget-driven loop, not a one-shot), correct JIT
                // behavior but not a single-instruction parity comparison.
                long budget = 1;
                jit.Run(ref budget);
            }
            else inner.Step();
            return ((ushort)inner.GetRegister("IP"), inner.CycleCount);
        }
        var (jip, jcyc) = RunOne(throughJit: true);
        var (iip, icyc) = RunOne(throughJit: false);
        Assert.Equal(iip, jip);     // the fallback advanced IP; the ushort cache key never aliased
        Assert.Equal(icyc, jcyc);   // the fallback charged the same cycles (CycleCount delta)
    }
}
