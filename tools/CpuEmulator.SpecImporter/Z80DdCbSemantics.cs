namespace CpuEmulator.SpecImporter;

/// <summary>
/// Computes the micro-op text for a Z80 DDCB/FDCB COMPOUND opcode ALGORITHMICALLY from the FINAL opcode
/// byte (M3.4e-3), the compound analogue of <see cref="Z80CbSemantics"/>. A DD CB d op (FD identical with
/// IY) applies the classic CB octal operation to (IX+d)/(IY+d): x selects the family (0 rotate/shift via
/// rot[y], 1 BIT, 2 RES, 3 SET); for x=0 y selects rot[y]; for x!=0 y is the bit index; z selects the
/// undoc STORE-COPY register reg[z] (B C D E H L (HL) A) — z=6 (the (HL) slot) means NO copy, and BIT
/// (x=1) NEVER copies regardless of z. The store-copy writes the PLAIN register (NOT IXh/IXl). Returns
/// the ops-text for every final opcode 0x00..0xFF — all 256 are owned (the compound page is total; no
/// prefix-byte holes because the byte after the displacement is the operation, never a prefix).
/// The index register (IX vs IY) is NOT encoded here — the emit arm reads it from the compound key's p1.
/// </summary>
public static class Z80DdCbSemantics
{
    private static readonly string[] Reg8 = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
    // rot[y] for x=0 — index 6 is SLL (undocumented shift-left-inline).
    private static readonly string[] Rot = ["RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL"];

    public static string OpsFor(int finalOpcode)
    {
        int x = (finalOpcode >> 6) & 0x03;
        int y = (finalOpcode >> 3) & 0x07;
        int z = finalOpcode & 0x07;
        // The undoc store-copy register: reg[z] for z != 6, "-" (no copy) for z=6 (the (HL) slot).
        string copy = z == 6 ? "-" : Reg8[z];
        return x switch
        {
            0 => $"[DdCb(\"{Rot[y]}\",0,\"{copy}\")]",   // rotate/shift (IX+d) + the store-copy
            1 => $"[DdCb(\"BIT\",{y},\"-\")]",            // BIT y,(IX+d) — NEVER copies (z ignored)
            2 => $"[DdCb(\"RES\",{y},\"{copy}\")]",       // RES y,(IX+d) + the store-copy
            _ => $"[DdCb(\"SET\",{y},\"{copy}\")]",       // SET y,(IX+d) + the store-copy
        };
    }
}
