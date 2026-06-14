namespace CpuEmulator.SpecImporter;

/// <summary>
/// Computes the micro-op text for a Z80 ED-PLANE CORE opcode (0x40–0x7F) ALGORITHMICALLY from the
/// second byte (M3.4c), the ED analogue of <see cref="Z80CbSemantics"/>. The ED core is the octal
/// encoding x=1 (y = bits 5-3, z = bits 2-0, p = y&gt;&gt;1, q = y&amp;1): z selects the family
/// (0 IN r,(C); 1 OUT (C),r; 2 ADC/SBC HL,rp; 3 LD (nn),rp/rp,(nn); 4 NEG; 5 RETN/RETI; 6 IM;
/// 7 misc LD I/R/A + RRD/RLD + NOP). Returns the ops-text for an ED-core opcode, or null for an ED
/// opcode OUTSIDE the 0x40–0x7F core (the block ops 0xA0–0xBB are a later PR; the caller defers them).
/// </summary>
public static class Z80EdSemantics
{
    private static readonly string[] Reg8 = ["B", "C", "D", "E", "H", "L", "(HL)", "A"];
    private static readonly string[] RpSp = ["BC", "DE", "HL", "SP"];
    // im[y] — the interrupt mode each IM opcode sets (the undoc 0/1 forms map to 0). Confirmed against
    // the vectors (RECON FINDING F7): 0x46->0 0x4E->0 0x56->1 0x5E->2 0x66->0 0x6E->0 0x76->1 0x7E->2.
    private static readonly int[] ImMode = [0, 0, 1, 2, 0, 0, 1, 2];

    /// <summary>Ops-text for an ED-core opcode (0x40–0x7F), or null if outside the core.</summary>
    public static string? OpsFor(int opcode)
    {
        if (opcode is < 0x40 or > 0x7F) return null;   // block ops + low ED are out of scope here
        int y = (opcode >> 3) & 0x07;
        int z = opcode & 0x07;
        int p = y >> 1;
        bool q = (y & 1) != 0;
        return z switch
        {
            0 => $"[EdIn(\"{(y == 6 ? "none" : Reg8[y])}\")]",     // IN r,(C) ; y=6 IN (C) (discard)
            1 => $"[EdOut(\"{(y == 6 ? "zero" : Reg8[y])}\")]",    // OUT (C),r ; y=6 OUT (C),0
            2 => $"[EdAdcSbc16(\"{(q ? "ADC" : "SBC")}\",\"{RpSp[p]}\")]",
            3 => $"[EdLdNnRp(\"{(q ? "LOAD" : "STORE")}\",\"{RpSp[p]}\")]",
            4 => "[EdNeg()]",                                       // NEG (canonical + undoc dups)
            5 => opcode == 0x4D ? "[EdRetn(true)]" : "[EdRetn(false)]",  // 0x4D RETI ; else RETN
            6 => $"[EdIm({ImMode[y]})]",
            _ => MiscZ7(y),                                         // z=7
        };
    }

    private static string MiscZ7(int y) => y switch
    {
        0 => "[EdLdIaRa(\"I_A\")]",   // 0x47 LD I,A
        1 => "[EdLdIaRa(\"R_A\")]",   // 0x4F LD R,A
        2 => "[EdLdIaRa(\"A_I\")]",   // 0x57 LD A,I
        3 => "[EdLdIaRa(\"A_R\")]",   // 0x5F LD A,R
        4 => "[EdRrdRld(false)]",     // 0x67 RRD
        5 => "[EdRrdRld(true)]",      // 0x6F RLD
        _ => "[EdNop()]",             // 0x77 / 0x7F undoc NOP
    };
}
