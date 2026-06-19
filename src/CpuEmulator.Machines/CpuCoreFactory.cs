using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Jit;

namespace CpuEmulator.Machines;

/// <summary>The CpuKind -> ICpuCore factory (the resolved open question #1). Returns a
/// Func&lt;IMachineContext, ICpuCore&gt; suitable for MachineBuilder.WithCpu. This is the one place
/// allowed to name the concrete cores AND the JIT, so the layering rule (Core references nothing)
/// stays intact. Piece #1 boots the 6502 (real $FFFC reset) and the Z80 (real PC=0 reset); the
/// 68000/8086 have no-op Reset stubs and are deferred to piece #2 (a MachineConfigurationException
/// here makes that explicit rather than silently producing a board that cannot boot).</summary>
public static class CpuCoreFactory
{
    public static Func<IMachineContext, ICpuCore> ForKind(
        CpuKind kind, AddressSpaceKind programSpace, ExecutionTier tier) =>
        ctx => tier switch
        {
            ExecutionTier.Interpreter => BuildInterpreter(kind, ctx, programSpace),
            ExecutionTier.Jit => BuildJit(kind, ctx, programSpace),
            _ => throw new MachineConfigurationException(
                $"Execution tier {tier} is not supported."),
        };

    private static ICpuCore BuildInterpreter(CpuKind kind, IMachineContext ctx, AddressSpaceKind programSpace)
    {
        IAddressSpace bus = ctx.Space(programSpace);
        return kind switch
        {
            CpuKind.Mos6502 => new Mos6502Cpu(bus),
            CpuKind.Z80 => new Z80Cpu(bus),
            _ => throw new MachineConfigurationException(
                $"CpuKind {kind} cannot boot a board yet (no real reset). Deferred to piece #2."),
        };
    }

    private static ICpuCore BuildJit(CpuKind kind, IMachineContext ctx, AddressSpaceKind programSpace)
    {
        // The JIT binds fastmem to the CONCRETE AddressSpace (page table + backing arrays). The
        // Machine builds AddressSpace as the only IAddressSpace, so this cast always holds.
        var bus = (AddressSpace)ctx.Space(programSpace);
        return kind switch
        {
            CpuKind.Mos6502 => new JittedCpu<Mos6502Cpu>(new Mos6502Cpu(bus), Mos6502Cpu.JitTarget, bus),
            CpuKind.Z80 => BuildZ80Jit(bus),
            _ => throw new MachineConfigurationException(
                $"CpuKind {kind} cannot boot a board yet (no real reset). Deferred to piece #2."),
        };
    }

    private static ICpuCore BuildZ80Jit(AddressSpace bus)
    {
        var inner = new Z80Cpu(bus);
        // The Z80's JIT routes Port-op callouts to its own Io space (inner.IoBus). The board's
        // peripherals are memory-mapped (spec section 6), so the Io space stays empty here.
        return new JittedCpu<Z80Cpu>(inner, Z80Cpu.JitTarget, bus, inner.IoBus);
    }
}
