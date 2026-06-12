namespace CpuEmulator.Core.Specification;

/// <summary>DSL factories for spec tables. The source generator recognizes calls to these
/// BY NAME with literal/enum arguments only — no variables, no computed expressions
/// (CPUGEN004/CPUGEN006 otherwise). This is the constrained-DSL contract from the design
/// spec (§5): specs must be statically analyzable.</summary>
public static class Spec
{
    public static InstructionDef Insn(byte opcode, string mnemonic, AddrMode mode, Op[] ops) =>
        new(opcode, mnemonic, mode, ops);

    public static Op Load(Reg target) => new LoadRegOp(target);
    public static Op Store(Reg source) => new StoreRegOp(source);
    public static Op Transfer(Reg source, Reg target) => new TransferOp(source, target);
    public static Op Increment(Reg target) => new IncrementOp(target);
    public static Op SetNZ(Reg source) => new SetNZOp(source);
    public static Op Jump() => new JumpOp();
    public static Op BranchIf(Flag flag, bool when) => new BranchIfOp(flag, when);
}
