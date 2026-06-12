namespace CpuEmulator.Core.Specification;

/// <summary>What a CPU does when it fetches an opcode its spec does not define (spec §7).
/// A user-callback variant is deferred until a consumer needs it.</summary>
public enum UndefinedOpcodePolicy
{
    /// <summary>Throw <see cref="UndefinedOpcodeException"/>. Default — loud, for development.</summary>
    Throw,

    /// <summary>Treat as a 2-cycle NOP (fetch + one internal cycle) so execution always progresses.</summary>
    Nop,
}
