using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 3: the construction-seam gate (RuntimeFeature.IsDynamicCodeSupported) + the
/// inner-CPU binding contract. On the JIT-capable test host the gate's positive throw cannot
/// be exercised (IsDynamicCodeSupported is true) — recorded honestly: we pin the message
/// constant + the negative branch here; the positive throw is covered by the Task 8 AOT
/// reference-graph check + a manual PublishAot smoke.</summary>
public class JittedCpuGateTests
{
    private static AddressSpace NewSpace()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    [Fact]
    public void Construction_succeeds_on_a_dynamic_code_host()
    {
        var space = NewSpace();
        var inner = new Mos6502Cpu(space);
        var ex = Record.Exception(() => new JittedCpu(inner, space));
        Assert.Null(ex);   // the gate's negative branch: dynamic code IS supported here
    }

    [Fact]
    public void Construction_binds_the_inner_interpreter()
    {
        var space = NewSpace();
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x1234;
        var jit = new JittedCpu(inner, space);
        Assert.Equal(0x1234ul, jit.GetRegister("PC"));   // the wrapper shares the inner's state
        Assert.Equal(inner.Architecture, jit.Architecture);
        Assert.Equal(inner.CycleCount, jit.CycleCount);
    }

    [Fact]
    public void Gate_message_names_the_interpreter_fallback_and_the_doc()
    {
        // Reference the message constant directly (do not re-type it) and pin its guidance shape.
        Assert.Contains("interpreter", JittedCpu.DynamicCodeRequiredMessage);
        Assert.Contains("jit.md", JittedCpu.DynamicCodeRequiredMessage);
        Assert.Contains("Mos6502Cpu", JittedCpu.DynamicCodeRequiredMessage);
    }

    [Fact]
    public void Null_inner_or_bus_is_a_construction_error()
    {
        var space = NewSpace();
        Assert.Throws<ArgumentNullException>(() => new JittedCpu(null!, space));
        Assert.Throws<ArgumentNullException>(() => new JittedCpu(new Mos6502Cpu(space), null!));
    }

    [Fact]
    public void Construction_over_a_shared_AddressSpace_wires_fastmem_to_the_inner_CPUs_bus()
    {
        // The JIT binds to a concrete AddressSpace for fastmem (recorded deviation). The documented
        // construction contract is that the inner CPU and the JIT are wired from the SAME
        // AddressSpace instance — a helper that wires correctly proves the contract: a fastmem
        // RAM round-trip through the JIT is visible on the same bus the inner CPU reads.
        var space = NewSpace();
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        var jit = new JittedCpu(inner, space);

        // LDA #$5A / STA $40 / JMP $0204 — fastmem store, then read back through the shared bus.
        space.Write8(0x0200, 0xA9); space.Write8(0x0201, 0x5A);
        space.Write8(0x0202, 0x85); space.Write8(0x0203, 0x40);
        space.Write8(0x0204, 0x4C); space.Write8(0x0205, 0x04); space.Write8(0x0206, 0x02);
        long budget = 5;
        jit.Run(ref budget);

        Assert.Equal(0x5A, space.Read8(0x40));   // the JIT's fastmem store landed on the inner CPU's bus
    }
}
