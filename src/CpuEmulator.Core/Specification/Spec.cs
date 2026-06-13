namespace CpuEmulator.Core.Specification;

/// <summary>DSL factories for spec tables. The source generator recognizes calls to these
/// BY NAME with literal/enum arguments only — no variables, no computed expressions
/// (CPUGEN004/CPUGEN006 otherwise). This is the constrained-DSL contract from the design
/// spec (§5): specs must be statically analyzable.
/// Register arguments are register-NAME string literals (e.g. <c>Load("A")</c>) validated by
/// the generator against the spec's Registers table (CPUGEN008) — there is no closed Reg enum.
/// Flag arguments remain Flag enum members (out of M3.1a's register-only scope).</summary>
public static class Spec
{
    // EXISTING (6502 — unchanged): single-byte opcode; key == opcode (KeyShape.OpcodeByte).
    public static InstructionDef Insn(byte opcode, string mnemonic, AddrMode mode, Op[] ops) =>
        new(opcode, mnemonic, mode, ops);

    // NEW (M3.1b, default-off): a PREFIXED row — key = (prefix, opcode). The generator packs
    // key = (prefix << 8) | opcode (KeyShape.PrefixedOpcode). The 6502 never uses this overload.
    public static InstructionDef Insn(byte prefix, byte opcode, string mnemonic, AddrMode mode, Op[] ops) =>
        new(opcode, mnemonic, mode, ops, Prefix: prefix, KeyShape: DecodeKeyShape.PrefixedOpcode);

    // NEW (M3.1b, default-off): an OPCODE-GROUP row — key = (opcode, sub-field of a non-first byte).
    // The generator packs key = (opcode << 3) | subfield (KeyShape.OpcodeGroup). Named arg `subfield`
    // distinguishes this from the prefixed overload above. The 6502 never uses this overload.
    public static InstructionDef Insn(byte opcode, int subfield, string mnemonic, AddrMode mode, Op[] ops) =>
        new(opcode, mnemonic, mode, ops, SubField: subfield, KeyShape: DecodeKeyShape.OpcodeGroup);

    public static Op Load(string target) => new LoadRegOp(target);
    public static Op Store(string source) => new StoreRegOp(source);
    public static Op Transfer(string source, string target) => new TransferOp(source, target);
    public static Op Increment(string target) => new IncrementOp(target);
    public static Op SetNZ(string source) => new SetNZOp(source);
    public static Op Jump() => new JumpOp();
    public static Op BranchIf(Flag flag, bool when) => new BranchIfOp(flag, when);

    // ── ALU class (Task 5) ───────────────────────────────────────────────────
    public static Op Adc() => new AdcOp();
    public static Op Sbc() => new SbcOp();
    public static Op And() => new AndOp();
    public static Op Ora() => new OraOp();
    public static Op Eor() => new EorOp();
    public static Op Compare(string source) => new CompareOp(source);
    public static Op Bit() => new BitOp();

    // ── RMW class (Task 6) ───────────────────────────────────────────────────
    public static Op ShiftLeft() => new ShiftLeftOp();
    public static Op ShiftRight() => new ShiftRightOp();
    public static Op RotateLeft() => new RotateLeftOp();
    public static Op RotateRight() => new RotateRightOp();
    public static Op IncrementMem() => new IncrementMemOp();
    public static Op DecrementMem() => new DecrementMemOp();
    public static Op Decrement(string target) => new DecrementOp(target);

    // ── Stack / flag / flow class (Task 7) ──────────────────────────────────
    public static Op Push(string source) => new PushOp(source);
    public static Op Pull(string target) => new PullOp(target);
    public static Op PushP() => new PushPOp();
    public static Op PullP() => new PullPOp();
    public static Op SetFlag(Flag flag, bool value) => new SetFlagOp(flag, value);
    public static Op Jsr() => new JsrOp();
    public static Op Rts() => new RtsOp();

    // ── BRK/RTI flow class (Task 8 / 3b-ii) ────────────────────────────────
    public static Op Brk() => new BrkOp();
    public static Op Rti() => new RtiOp();

    // ── I/O-port + halt class (M3.2 — additive). Register args are register-NAME string literals
    // (the J2 convention, validated against the spec's Registers table by CPUGEN008), NOT a Reg.
    public static Op PortIn(string target) => new PortInOp(target);   // Z80: IN A,(n) / IN r,(C)
    public static Op PortOut(string source) => new PortOutOp(source); // Z80: OUT (n),A / OUT (C),r
    public static Op Halt() => new HaltOp();                          // Z80 HALT / 68000 STOP
}
