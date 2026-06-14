namespace CpuEmulator.SpecImporter;

/// <summary>
/// Computes the micro-op text for a Z80 DD/FD-PLANE CORE opcode ALGORITHMICALLY (M3.4e-2), the DD/FD
/// analogue of <see cref="Z80CbSemantics"/>/<see cref="Z80EdSemantics"/>. A DD (IX) / FD (IY) prefix
/// REINTERPRETS the following byte (the D3 rule, re-derived from the SingleStepTests vectors):
///   (1) an op that names (HL) as memory -> (IX+d)/(IY+d) (mode Indexed): LD/ALU/INC-DEC/LD-imm;
///   (2) an op naming H/L (and NO memory) -> re-read as IXh/IXl (the undoc half ops);
///   (3) an op naming the HL pair -> the HL slot becomes IX/IY (the 16-bit ops);
///   (4) otherwise the prefix is INERT -> the base op (PC+2, R+2).
/// Returns the ops-text for ANY DD/FD core opcode (it owns all 252), delegating (2)/(3)/(4) to
/// <see cref="Z80BaseSemantics"/> with the H/L -> IXh/IXl and HL-pair -> IX textual substitution
/// applied to the base op-text. The (IX+d) memory forms (1) are detected by opcode and emit the
/// plane-agnostic DdFd… indexed ops (the emit arm reads IX/IY from the OperationKey prefix).
///
/// NOTE on the undoc-half 8-bit ALU (DD 84 = ADD A,IXh etc.): the base ALU op-text (e.g. [Add8()])
/// carries NO source register name (the base emit arm resolves the source from the opcode's z-field),
/// so there is no "H"/"L" string to substitute here. Those forms therefore keep the base op-text
/// UNCHANGED; the emit arm's source resolver (CpuEmitter.SourceRegFromOpcode) is prefix-aware and
/// maps the H/L source slot to IXh/IXl/IYh/IYl for a DD/FD-prefixed ALU row (M3.4e-2 Task 5).
/// </summary>
public static class Z80DdFdSemantics
{
    // The 8-register table indexed by a 3-bit register field (B C D E H L (HL) A). Index 6 is (HL).
    private static readonly string[] Reg8 = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
    // The 8-bit ALU ops indexed by y (bits 5-3) for the x=2 block (ADD..CP A,s).
    private static readonly string[] Alu = ["ADD", "ADC", "SUB", "SBC", "AND", "XOR", "OR", "CP"];

    /// <summary>Ops-text for a DD/FD core opcode. <paramref name="mnemonic"/>/<paramref name="mode"/>
    /// are the dataset row's fields (the base decoder needs them to resolve operands); <paramref
    /// name="isIy"/> selects the IY plane (IY/IYh/IYl).</summary>
    public static string? OpsFor(int opcode, string mnemonic, string mode, bool isIy)
    {
        // The four prefix bytes are not core opcodes (a DD followed by CB/DD/ED/FD is the compound/chain
        // case — out of scope here; the dataset has no such core row).
        if (opcode is 0xCB or 0xDD or 0xED or 0xFD) return null;

        // (1) The Indexed (IX+d)/(IY+d) forms — keyed off the base opcode's (HL)-naming members.
        string? indexed = IndexedFor(opcode);
        if (indexed is not null) return indexed;

        // (2)/(3)/(4) — derive the BASE op-text, then substitute H/L -> IXh/IXl and the HL pair -> IX
        // for the half/16-bit ops. The base decoder is the source of truth for the inert + the regular
        // operand resolution; the substitution is a textual rewrite of the produced op-text.
        string? baseOps = Z80BaseSemantics.OpsFor(opcode, mnemonic, mode);
        if (baseOps is null) return null;
        return SubstituteHalfAndPair(baseOps, isIy);
    }

    // The (HL)-naming base opcodes that become (IX+d)/(IY+d). Re-derived from the base octal encoding:
    //   LD r,(HL): x=1,z=6 (0x46/4E/56/5E/66/6E/7E) ; LD (HL),r: x=1,y=6 (0x70-0x77 except 0x76 HALT)
    //   LD (HL),n: 0x36 ; ALU A,(HL): x=2,z=6 (0x86/8E/96/9E/A6/AE/B6/BE) ; INC/DEC (HL): 0x34/0x35.
    private static string? IndexedFor(int opcode)
    {
        // INC/DEC (HL) -> INC/DEC (IX+d)
        if (opcode == 0x34) return "[DdFdIncDecIndexed(false)]";
        if (opcode == 0x35) return "[DdFdIncDecIndexed(true)]";
        // LD (HL),n -> LD (IX+d),n  (4-byte: displacement THEN immediate; G5)
        if (opcode == 0x36) return "[DdFdStoreImmIndexed()]";
        // ALU A,(HL) -> ALU A,(IX+d)  (x=2, z=6)
        if ((opcode & 0xC7) == 0x86)
            return $"[DdFdAluIndexed(\"{Alu[(opcode >> 3) & 7]}\")]";
        // LD r,(HL) -> LD r,(IX+d)  (x=1, z=6, y != 6 because y=6 z=6 = 0x76 HALT)
        if ((opcode & 0xC7) == 0x46 && ((opcode >> 3) & 7) != 6)
            return $"[DdFdLdIndexed(\"LOAD\",\"{Reg8[(opcode >> 3) & 7]}\")]";
        // LD (HL),r -> LD (IX+d),r  (x=1, y=6, z != 6 because y=6 z=6 = 0x76 HALT)
        if ((opcode & 0xF8) == 0x70 && (opcode & 7) != 6)
            return $"[DdFdLdIndexed(\"STORE\",\"{Reg8[opcode & 7]}\")]";
        return null;
    }

    // (2)/(3) the textual substitution: in a base op-text, rewrite a STANDALONE "H"/"L" register name
    // to "IXh"/"IXl" (or IYh/IYl) and the HL pair to IX/IY. The base op-text uses quoted operand names
    // (e.g. Transfer("H","B"), IncReg("H"), Add16("HL","BC"), Load16("HL")). The (HL) indirect forms
    // are ALREADY handled by IndexedFor (returned above), so no "(HL)" string survives to here.
    // Quoted-string replacement is exact: "H" never matches inside "HL" (a different string literal),
    // and "HL" is replaced as its own token, so ordering is irrelevant.
    private static string SubstituteHalfAndPair(string baseOps, bool isIy)
    {
        string h = isIy ? "IYh" : "IXh";
        string l = isIy ? "IYl" : "IXl";
        string pair = isIy ? "IY" : "IX";
        return baseOps
            .Replace("\"H\"", $"\"{h}\"")
            .Replace("\"L\"", $"\"{l}\"")
            .Replace("\"HL\"", $"\"{pair}\"");
    }
}
