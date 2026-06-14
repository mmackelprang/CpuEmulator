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

    // NEW (M3.4e-1b, default-off): a COMPOUND-prefixed row (the Z80 DD CB d op / FD CB d op). Names
    // BOTH prefix bytes + the final opcode. The displacement sits between the prefix-pair and the
    // opcode (declared via the PrefixByte's DisplacementBeforeOpcode). The generator packs
    // key = (prefix1 << 16) | (prefix2 << 8) | finalOpcode (KeyShape.Compound). The 6502 never uses it.
    public static InstructionDef Insn(byte prefix1, byte prefix2, byte finalOpcode, string mnemonic, AddrMode mode, Op[] ops) =>
        new(finalOpcode, mnemonic, mode, ops, Prefix: prefix1, Prefix2: prefix2, KeyShape: DecodeKeyShape.Compound);

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

    // ── Composable flag micro-ops (M3.4a — general, 8086-reusable). Register-NAME string args. ──
    public static Op SetSZ(string source) => new SetSZOp(source);
    public static Op SetParity(string source) => new SetParityOp(source);
    public static Op SetXY(string source) => new SetXYOp(source);
    public static Op SetAddSub(bool subtract) => new SetAddSubOp(subtract);

    // ── M3.4a Z80 base-plane micro-ops (additive; the 6502 names none). ──
    // 8-bit flag-correct ALU (A-implicit; source resolved by mode).
    public static Op Add8() => new Add8Op();
    public static Op Adc8() => new Adc8Op();
    public static Op Sub8() => new Sub8Op();
    public static Op Sbc8() => new Sbc8Op();
    public static Op And8() => new And8Op();
    public static Op Or8() => new Or8Op();
    public static Op Xor8() => new Xor8Op();
    public static Op Cp8() => new Cp8Op();
    // 8-bit INC/DEC.
    public static Op IncReg(string target) => new IncRegOp(target);
    public static Op DecReg(string target) => new DecRegOp(target);
    public static Op IncMem8() => new IncMem8Op();
    public static Op DecMem8() => new DecMem8Op();
    // 16-bit ALU.
    public static Op Add16(string target, string source) => new Add16Op(target, source);
    public static Op Inc16(string target) => new Inc16Op(target);
    public static Op Dec16(string target) => new Dec16Op(target);
    // 16-bit LD.
    public static Op Load16(string target) => new Load16Op(target);
    public static Op Store16(string source) => new Store16Op(source);
    public static Op LoadMem16(string target) => new LoadMem16Op(target);
    public static Op StoreImm8() => new StoreImm8Op();
    // Pair stack.
    public static Op Push16(string pair) => new Push16Op(pair);
    public static Op Pop16(string pair) => new Pop16Op(pair);
    // Exchange.
    public static Op ExDeHl() => new ExDeHlOp();
    public static Op ExAfAf() => new ExAfAfOp();
    public static Op Exx() => new ExxOp();
    public static Op ExSpHl() => new ExSpHlOp();
    // Flow (conditional + relative).
    public static Op JumpIf(Flag cc, bool sense) => new JumpIfOp(cc, sense);
    public static Op CallIf(Flag cc, bool sense) => new CallIfOp(cc, sense);
    public static Op RetCc(Flag cc, bool sense) => new RetCcOp(cc, sense);
    public static Op RelJump() => new RelJumpOp();
    public static Op RelJumpIf(Flag cc, bool sense) => new RelJumpIfOp(cc, sense);
    public static Op Djnz(string counter) => new DjnzOp(counter);
    public static Op Rst() => new RstOp();
    public static Op JumpIndirect() => new JumpIndirectOp();
    public static Op JumpAbs() => new JumpAbsOp();
    public static Op CallAbs() => new CallAbsOp();
    public static Op Ret() => new RetOp();
    // Misc.
    public static Op Daa() => new DaaOp();
    public static Op Cpl() => new CplOp();
    public static Op Scf() => new ScfOp();
    public static Op Ccf() => new CcfOp();
    public static Op Di() => new DiOp();
    public static Op Ei() => new EiOp();

    // ── M3.4b CB plane + rotate-accumulators (additive) ──
    public static Op Rlca() => new RlcaOp();
    public static Op Rrca() => new RrcaOp();
    public static Op Rla() => new RlaOp();
    public static Op Rra() => new RraOp();
    public static Op CbRotate(string op, string target) => new CbRotateOp(op, target);
    public static Op CbBit(string op, int bit, string target) => new CbBitOp(op, bit, target);

    // ── M3.4c ED-core plane (additive) ──
    public static Op EdIn(string target) => new EdInOp(target);
    public static Op EdOut(string source) => new EdOutOp(source);
    public static Op EdAdcSbc16(string op, string pair) => new EdAdcSbc16Op(op, pair);
    public static Op EdLdNnRp(string op, string pair) => new EdLdNnRpOp(op, pair);
    public static Op EdNeg() => new EdNegOp();
    public static Op EdRetn(bool isReti) => new EdRetnOp(isReti);
    public static Op EdIm(int mode) => new EdImOp(mode);
    public static Op EdLdIaRa(string op) => new EdLdIaRaOp(op);
    public static Op EdRrdRld(bool isRld) => new EdRrdRldOp(isRld);
    public static Op EdNop() => new EdNopOp();

    // ── M3.4d ED block ops (additive) ──
    public static Op EdBlock(string mnemonic) => new EdBlockOp(mnemonic);

    // ── M3.4e-2 DD/FD indexed plane (additive) ──
    public static Op DdFdLdIndexed(string op, string reg) => new DdFdLdIndexedOp(op, reg);
    public static Op DdFdStoreImmIndexed() => new DdFdStoreImmIndexedOp();
    public static Op DdFdAluIndexed(string op) => new DdFdAluIndexedOp(op);
    public static Op DdFdIncDecIndexed(bool isDec) => new DdFdIncDecIndexedOp(isDec);
}
