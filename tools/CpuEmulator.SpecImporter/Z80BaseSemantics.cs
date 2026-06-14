namespace CpuEmulator.SpecImporter;

/// <summary>
/// Computes the micro-op text for a Z80 BASE-PLANE opcode ALGORITHMICALLY from the opcode byte
/// (M3.4a). The Z80 base plane is the classic regular octal encoding (the opcode bits select the
/// dst/src register, the ALU op, the pair, the condition code), so the per-opcode semantics are a
/// pure function of the opcode byte — far more faithful (and maintainable) than 248 hand-written
/// per-opcode JSON entries.
///
/// RECORDED DEVIATION from the plan: the plan envisioned a per-MNEMONIC semantics map. But the same
/// mnemonic (LD/ADD/INC/DEC) maps to many distinct ops depending on the register/pair the opcode
/// bits select, which a per-mnemonic map cannot express. The dataset carries no operand field, so
/// the operands are derived here from the opcode's bit fields. The per-mnemonic z80-semantics.json
/// map remains the AUTHORING surface for documentation + the ALU flag-family choice; this decoder is
/// the operand resolver the regular encoding makes exact. Returns null for an opcode this base-plane
/// decoder does not own (rotate-accumulator 0x07/0x0F/0x17/0x1F → M3.4b; the prefixed planes).
/// </summary>
public static class Z80BaseSemantics
{
    // The 8-register source/target table indexed by the opcode's 3-bit register field
    // (B C D E H L (HL) A). Index 6 is (HL) — a RegisterIndirect form, handled separately.
    private static readonly string[] Reg8 = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];

    // The pair table for the 16-bit register field bits 5-4 (rp): BC DE HL SP.
    private static readonly string[] RpSp = ["BC", "DE", "HL", "SP"];
    // The pair table for PUSH/POP (rp2): BC DE HL AF.
    private static readonly string[] Rp2 = ["BC", "DE", "HL", "AF"];

    // The 8 condition codes (cc) for JP cc/CALL cc/RET cc, indexed by bits 5-3:
    // NZ Z NC C PO PE P M  →  the Flag + sense pair the JumpIf/CallIf/RetCc factory carries.
    // NZ=Z false, Z=Z true, NC=C false, C=C true, PO=P false, PE=P true, P=S false, M=S true.
    private static readonly (string Flag, bool Sense)[] Cc =
    [
        ("Z", false), ("Z", true), ("C", false), ("C", true),
        ("P", false), ("P", true), ("S", false), ("S", true),
    ];

    /// <summary>Returns the ops-text (e.g. "[Transfer(\"C\",\"B\")]") for a base-plane Z80 opcode,
    /// or null if this decoder does not own it (the caller then emits a TODO / defers).</summary>
    public static string? OpsFor(int opcode, string mnemonic, string mode)
    {
        int x = (opcode >> 6) & 0x03;   // bits 7-6
        int y = (opcode >> 3) & 0x07;   // bits 5-3
        int z = opcode & 0x07;          // bits 2-0
        int p = (opcode >> 4) & 0x03;   // bits 5-4 (pair selector)
        bool q = (opcode & 0x08) != 0;  // bit 3

        switch (mnemonic)
        {
            case "NOP":  return "[]";
            case "HALT": return "[Halt()]";
            case "DAA":  return "[Daa()]";
            case "CPL":  return "[Cpl()]";
            case "SCF":  return "[Scf()]";
            case "CCF":  return "[Ccf()]";
            case "DI":   return "[Di()]";
            case "EI":   return "[Ei()]";
            case "EXX":  return "[Exx()]";

            case "EX":
                // 0x08 EX AF,AF' ; 0xEB EX DE,HL ; 0xE3 EX (SP),HL
                return opcode switch
                {
                    0x08 => "[ExAfAf()]",
                    0xEB => "[ExDeHl()]",
                    0xE3 => "[ExSpHl()]",
                    _ => null,
                };

            case "LD": return LdOps(opcode, mode, x, y, z, p, q);

            case "INC":
                if (mode == "RegisterIndirect") return "[IncMem8()]";          // INC (HL)
                if (x == 0 && z == 3) return $"[Inc16(\"{RpSp[p]}\")]";        // INC rr
                return $"[IncReg(\"{Reg8[y]}\")]";                             // INC r
            case "DEC":
                if (mode == "RegisterIndirect") return "[DecMem8()]";          // DEC (HL)
                if (x == 0 && z == 3) return $"[Dec16(\"{RpSp[p]}\")]";        // DEC rr
                return $"[DecReg(\"{Reg8[y]}\")]";                             // DEC r

            case "ADD":
                // 0x09/19/29/39 ADD HL,rr (16-bit) ; 0x80-0x87 + 0xC6 ADD A,s (8-bit).
                if (x == 0 && z == 1) return $"[Add16(\"HL\",\"{RpSp[p]}\")]"; // ADD HL,rr
                return "[Add8()]";
            case "ADC": return "[Adc8()]";
            case "SUB": return "[Sub8()]";
            case "SBC": return "[Sbc8()]";
            case "AND": return "[And8()]";
            case "OR":  return "[Or8()]";
            case "XOR": return "[Xor8()]";
            case "CP":  return "[Cp8()]";

            case "PUSH": return $"[Push16(\"{Rp2[p]}\")]";
            case "POP":  return $"[Pop16(\"{Rp2[p]}\")]";

            case "JP":
                if (mode == "RegisterIndirect") return "[JumpIndirect()]";     // JP (HL)
                if (z == 3) return "[JumpAbs()]";                              // 0xC3 JP nn
                return $"[JumpIf(Flag.{Cc[y].Flag}, {Low(Cc[y].Sense)})]";     // JP cc,nn
            case "JR":
                if (opcode == 0x18) return "[RelJump()]";                      // JR d
                // JR cc,d uses the 4-condition subset (NZ/Z/NC/C) — y-2 indexes Cc[0..3].
                return $"[RelJumpIf(Flag.{Cc[y - 4].Flag}, {Low(Cc[y - 4].Sense)})]";
            case "DJNZ": return "[Djnz(\"B\")]";
            case "CALL":
                if (opcode == 0xCD) return "[CallAbs()]";                      // CALL nn
                return $"[CallIf(Flag.{Cc[y].Flag}, {Low(Cc[y].Sense)})]";     // CALL cc,nn
            case "RET":
                if (opcode == 0xC9) return "[Ret()]";                          // RET
                return $"[RetCc(Flag.{Cc[y].Flag}, {Low(Cc[y].Sense)})]";      // RET cc
            case "RST": return "[Rst()]";

            case "IN":  return "[PortIn(\"A\")]";                              // IN A,(n)
            case "OUT": return "[PortOut(\"A\")]";                             // OUT (n),A

            // M3.4b: the four base-plane rotate-accumulators (share the rotate math, preserve S/Z/P-V).
            case "RLCA": return "[Rlca()]";
            case "RRCA": return "[Rrca()]";
            case "RLA":  return "[Rla()]";
            case "RRA":  return "[Rra()]";

            default: return null;
        }
    }

    private static string LdOps(int opcode, string mode, int x, int y, int z, int p, bool q)
    {
        // 16-bit immediate: LD rr,nn (ImmediateExtended, x==0 z==1 !q).
        if (mode == "ImmediateExtended") return $"[Load16(\"{RpSp[p]}\")]";

        // (nn) word/byte (ExtendedAddress).
        if (mode == "ExtendedAddress")
            return opcode switch
            {
                0x22 => "[Store16(\"HL\")]",     // LD (nn),HL
                0x2A => "[LoadMem16(\"HL\")]",   // LD HL,(nn)
                0x32 => "[Store(\"A\")]",        // LD (nn),A
                0x3A => "[Load(\"A\")]",         // LD A,(nn)
                _ => "[]",
            };

        // 8-bit immediate: LD r,n (Immediate). x==0, z==6; r = y. (HL) form is LD (HL),n.
        if (mode == "Immediate")
            return y == 6 ? "[StoreImm8()]" : $"[Load(\"{Reg8[y]}\")]";

        // Register-indirect 8-bit: (HL)/(BC)/(DE).
        if (mode == "RegisterIndirect")
            return opcode switch
            {
                0x02 => "[Store(\"A\")]",   // LD (BC),A
                0x12 => "[Store(\"A\")]",   // LD (DE),A
                0x0A => "[Load(\"A\")]",    // LD A,(BC)
                0x1A => "[Load(\"A\")]",    // LD A,(DE)
                // LD r,(HL): x==1, z==6 → Load(r=y). LD (HL),r: x==1, y==6 → Store(r=z).
                _ when y == 6 => $"[Store(\"{Reg8[z]}\")]",
                _ => $"[Load(\"{Reg8[y]}\")]",
            };

        // Register-to-register (Register): LD r,r' (x==1). dst=y, src=z. LD SP,HL (0xF9).
        if (mode == "Register")
        {
            if (opcode == 0xF9) return "[Transfer(\"HL\",\"SP\")]";  // LD SP,HL
            return $"[Transfer(\"{Reg8[z]}\",\"{Reg8[y]}\")]";       // LD dst,src  (Transfer(src,dst))
        }

        return "[]";
    }

    private static string Low(bool b) => b ? "true" : "false";
}
