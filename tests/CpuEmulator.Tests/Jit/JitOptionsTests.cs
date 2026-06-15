using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using CpuEmulator.Tests.Mos6502;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 6: JitOptions — the DisableFastmem accuracy mode (Ground truth E) and the
/// test-overridable BlockLengthCap. DisableFastmem routes every access through the bus, restoring
/// per-cycle bus-trace equivalence with the interpreter (the trace spot tests' mode); fastmem on
/// (the default) bypasses the bus for RAM/ROM. SMC must keep working in trace mode (Ground truth
/// G, last row): writable-RAM stores still dirty-mark their page.</summary>
public class JitOptionsTests
{
    private static AddressSpace NewRamSpace()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    private static void Poke(AddressSpace space, ushort at, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            space.Write8((uint)(at + i), bytes[i]);
    }

    // ── DisableFastmem routes RAM reads through the bus (the trace bus sees them) ───────────────
    [Fact]
    public void DisableFastmem_routes_RAM_reads_through_the_bus()
    {
        // A JIT with DisableFastmem + a TracingAddressSpace trace bus: a RAM read appears in the
        // trace. (With fastmem on it would NOT — RAM reads go direct to the backing array.)
        var space = NewRamSpace();
        space.Write8(0x0010, 0x42);
        Poke(space, 0x0200, 0xA5, 0x10, 0x4C, 0x02, 0x02); // LDA $10 / JMP $0202
        var tracing = new TracingAddressSpace(space);
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, options: new JitOptions { DisableFastmem = true }, traceBus: tracing);

        long budget = 3;
        jit.Run(ref budget);

        // The RAM read of $0010 routed through the bus → it is in the trace.
        Assert.Contains(tracing.Trace, a => a.IsRead && a.Address == 0x0010 && a.Value == 0x42);
        Assert.Equal(0x42, inner.A);
    }

    // ── Default options keep fastmem on (RAM reads bypass the bus) ──────────────────────────────
    [Fact]
    public void Default_options_keep_fastmem_on()
    {
        var space = NewRamSpace();
        space.Write8(0x0010, 0x42);
        Poke(space, 0x0200, 0xA5, 0x10, 0x4C, 0x02, 0x02);
        var tracing = new TracingAddressSpace(space);
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        // Default options (fastmem on): the traceBus is ignored, RAM goes direct to the backing.
        var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, options: new JitOptions(), traceBus: tracing);

        long budget = 3;
        jit.Run(ref budget);

        Assert.DoesNotContain(tracing.Trace, a => a.Address == 0x0010); // RAM access NOT on the bus
        Assert.Equal(0x42, inner.A);                                    // state still correct
    }

    // ── DisableFastmem still invalidates SMC (Ground truth G last row) ──────────────────────────
    [Fact]
    public void DisableFastmem_still_invalidates_self_modifying_code()
    {
        // Even in trace mode (all accesses via the bus), a writable-RAM store must dirty-mark its
        // page so an intra-block opcode patch is caught — SMC parity must hold in BOTH modes. We
        // diff a DisableFastmem JIT against a fresh interpreter on the intra-block opcode-patch
        // program from InvalidationTests.
        static (Mos6502Cpu refCpu, Mos6502Cpu jitInner) Run()
        {
            var refSpace = NewRamSpace();
            refSpace.Write8(0x30, 0x01);
            Poke(refSpace, 0x0200, 0xA9, 0xC6, 0x8D, 0x07, 0x02, 0xA9, 0x05, 0xE6, 0x30, 0x85, 0x31, 0x4C, 0x0B, 0x02);
            var refCpu = new Mos6502Cpu(refSpace);
            refCpu.PC = 0x0200; refCpu.S = 0xFD; refCpu.P = 0x24;
            long rb = 200; refCpu.Run(ref rb);

            var jitSpace = NewRamSpace();
            jitSpace.Write8(0x30, 0x01);
            Poke(jitSpace, 0x0200, 0xA9, 0xC6, 0x8D, 0x07, 0x02, 0xA9, 0x05, 0xE6, 0x30, 0x85, 0x31, 0x4C, 0x0B, 0x02);
            var inner = new Mos6502Cpu(jitSpace);
            inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
            var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, jitSpace, options: new JitOptions { DisableFastmem = true });
            long jb = 200; jit.Run(ref jb);
            for (uint a = 0; a <= 0xFFFF; a++)
                Assert.Equal(refSpace.Read8(a), jitSpace.Read8(a));
            return (refCpu, inner);
        }

        var (r, j) = Run();
        Assert.Equal(r.A, j.A);
        Assert.Equal(r.PC, j.PC);
        Assert.Equal(r.CycleCount, j.CycleCount);
    }

    // ── BlockLengthCap is honored (a small cap splits a straight run into multiple blocks) ──────
    [Fact]
    public void BlockLengthCap_is_honored()
    {
        var space = NewRamSpace();
        var nops = new byte[5];
        Array.Fill(nops, (byte)0xEA);
        Poke(space, 0x0200, nops);
        var inner = new Mos6502Cpu(space);
        var opts = new JitOptions { BlockLengthCap = 4 };
        var compiler = new BlockCompiler<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, space, new Fastmem(space, opts), opts);

        var run = compiler.Discover(0x0200);
        Assert.Equal(4, run.Count); // capped at 4 instructions; the 5th NOP is a new block
    }

    // ── BlockLengthCap default is 64 ────────────────────────────────────────────────────────────
    [Fact]
    public void BlockLengthCap_default_is_64()
    {
        Assert.Equal(64, new JitOptions().BlockLengthCap);
    }

    // ── DisableFastmem defaults off ─────────────────────────────────────────────────────────────
    [Fact]
    public void DisableFastmem_defaults_off()
    {
        Assert.False(new JitOptions().DisableFastmem);
    }
}
