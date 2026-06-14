namespace CpuEmulator.SpecImporter;

/// <summary>
/// Computes the micro-op text for a Z80 CB-PLANE opcode ALGORITHMICALLY from the second byte (M3.4b),
/// the CB analogue of <see cref="Z80BaseSemantics"/>. The CB plane is the classic octal encoding
/// (x = bits 7-6, y = bits 5-3, z = bits 2-0): x selects the family (0 rotate/shift, 1 BIT, 2 RES,
/// 3 SET); for x=0, y selects rot[y]; for x≠0, y is the bit index; z selects the register operand
/// reg[z] (B C D E H L (HL) A). Returns the ops-text (e.g. "[CbRotate(\"RLC\",\"B\")]") for every
/// CB opcode 0x00..0xFF — all 256 are owned (the plane is total).
/// </summary>
public static class Z80CbSemantics
{
    private static readonly string[] Reg8 = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
    // rot[y] for x=0 — index 6 is SLL (undocumented shift-left-inline).
    private static readonly string[] Rot = ["RLC", "RRC", "RL", "RR", "SLA", "SRA", "SLL", "SRL"];

    public static string OpsFor(int opcode)
    {
        int x = (opcode >> 6) & 0x03;
        int y = (opcode >> 3) & 0x07;
        int z = opcode & 0x07;
        string target = Reg8[z];
        return x switch
        {
            0 => $"[CbRotate(\"{Rot[y]}\",\"{target}\")]",
            1 => $"[CbBit(\"BIT\",{y},\"{target}\")]",
            2 => $"[CbBit(\"RES\",{y},\"{target}\")]",
            _ => $"[CbBit(\"SET\",{y},\"{target}\")]",
        };
    }
}
