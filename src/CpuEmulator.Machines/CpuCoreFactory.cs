using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;

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
        CpuKind kind, AddressSpaceKind programSpace, ExecutionTier tier) => tier switch
    {
        ExecutionTier.Interpreter => ctx => BuildInterpreter(kind, ctx, programSpace),
        _ => throw new MachineConfigurationException(
            $"Execution tier {tier} is not supported yet for {kind}."),
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
}
