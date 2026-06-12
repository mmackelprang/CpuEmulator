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

    // ── ALU class (Task 5) ───────────────────────────────────────────────────
    public static Op Adc() => new AdcOp();
    public static Op Sbc() => new SbcOp();
    public static Op And() => new AndOp();
    public static Op Ora() => new OraOp();
    public static Op Eor() => new EorOp();
    public static Op Compare(Reg source) => new CompareOp(source);
    public static Op Bit() => new BitOp();

    // ── RMW class (Task 6) ───────────────────────────────────────────────────
    public static Op ShiftLeft() => new ShiftLeftOp();
    public static Op ShiftRight() => new ShiftRightOp();
    public static Op RotateLeft() => new RotateLeftOp();
    public static Op RotateRight() => new RotateRightOp();
    public static Op IncrementMem() => new IncrementMemOp();
    public static Op DecrementMem() => new DecrementMemOp();
    public static Op Decrement(Reg target) => new DecrementOp(target);

    // ── Stack / flag / flow class (Task 7) ──────────────────────────────────
    public static Op Push(Reg source) => new PushOp(source);
    public static Op Pull(Reg target) => new PullOp(target);
    public static Op PushP() => new PushPOp();
    public static Op PullP() => new PullPOp();
    public static Op SetFlag(Flag flag, bool value) => new SetFlagOp(flag, value);
    public static Op Jsr() => new JsrOp();
    public static Op Rts() => new RtsOp();

    // ── BRK/RTI flow class (Task 8 / 3b-ii) ────────────────────────────────
    public static Op Brk() => new BrkOp();
    public static Op Rti() => new RtiOp();
}
