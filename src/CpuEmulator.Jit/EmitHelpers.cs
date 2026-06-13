using System.Reflection.Emit;

namespace CpuEmulator.Jit;

/// <summary>Per-block emit state: the ILGenerator plus the reusable scratch locals the emit
/// arms share. Locals are typed to make the IL match the interpreter's C# (which works in
/// <c>uint</c> for addresses and <c>byte</c>/<c>int</c> for data). The arg indices for the
/// BlockDelegate signature are fixed:
///   0 = cpu (Mos6502Cpu), 1 = bus (AddressSpace), 2 = fastmem (byte[]?[]),
///   3 = dirty (DirtyMap), 4 = ref long budget, 5 = out BlockExit exit.</summary>
internal sealed class EmitContext
{
    public ILGenerator Il { get; }

    /// <summary>uint — a resolved operand byte / zero-page address / pointer.</summary>
    public LocalBuilder AddrLocal { get; }

    /// <summary>int — a data byte read from memory (held as int so byte ops compose).</summary>
    public LocalBuilder DataLocal { get; }

    /// <summary>uint — the effective address an access targets (the page-class branch keys on it).
    /// INVARIANT: both LoadByteFromBus and EmitStoreByte CLOBBER this local (they stash the access
    /// address here). An arm that needs an address to survive across a bus access must hold it in
    /// AddrLocal and re-store ea before each access (the RMW memory arms do exactly this).</summary>
    public LocalBuilder EaLocal { get; }

    /// <summary>uint — a low byte during multi-byte address resolution.</summary>
    public LocalBuilder LoLocal { get; }

    /// <summary>uint — a high byte during multi-byte address resolution.</summary>
    public LocalBuilder HiLocal { get; }

    /// <summary>long — scratch for the fallback's cycle-delta math.</summary>
    public LocalBuilder TmpLong { get; }

    public EmitContext(ILGenerator il)
    {
        Il = il;
        AddrLocal = il.DeclareLocal(typeof(uint));
        DataLocal = il.DeclareLocal(typeof(int));
        EaLocal = il.DeclareLocal(typeof(uint));
        LoLocal = il.DeclareLocal(typeof(uint));
        HiLocal = il.DeclareLocal(typeof(uint));
        TmpLong = il.DeclareLocal(typeof(long));
    }
}
