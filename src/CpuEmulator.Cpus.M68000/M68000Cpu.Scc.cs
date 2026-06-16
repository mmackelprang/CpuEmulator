namespace CpuEmulator.Cpus.M68000;

/// <summary>
/// M4.5c Scc + the SHARED condition evaluator (reused by M4.5d's Bcc/DBcc). Scc writes a byte EA = 0xFF if the
/// condition (operword bits 11-8) is true else 0x00; NO CCR change; the 68000 reads the EA before writing (the
/// dummy read, like CLR) so the RMW is address-once via ResolveEaDest. CMPM (the M4.5b carried-forward fix,
/// Task 14) also lives here as a bespoke ALU-ish compare. Reuses the merged layer; seam untouched.
/// </summary>
public sealed partial class M68000Cpu
{
    /// <summary>The 16 M68000 condition codes (operword bits 11-8) evaluated against a CCR byte.
    /// X=0x10 N=0x08 Z=0x04 V=0x02 C=0x01.</summary>
    private static bool EvaluateCondition(uint cc, byte ccr)
    {
        bool n = (ccr & 0x08) != 0, z = (ccr & 0x04) != 0, v = (ccr & 0x02) != 0, c = (ccr & 0x01) != 0;
        return cc switch
        {
            0x0u => true,                  // T
            0x1u => false,                 // F
            0x2u => !c && !z,              // HI
            0x3u => c || z,                // LS
            0x4u => !c,                    // CC (HS)
            0x5u => c,                     // CS (LO)
            0x6u => !z,                    // NE
            0x7u => z,                     // EQ
            0x8u => !v,                    // VC
            0x9u => v,                     // VS
            0xAu => !n,                    // PL
            0xBu => n,                     // MI
            0xCu => n == v,                // GE
            0xDu => n != v,                // LT
            0xEu => !z && (n == v),        // GT
            _    => z || (n != v),         // LE (0xF)
        };
    }
    public static bool EvaluateConditionProbe(uint cc, byte ccr) => EvaluateCondition(cc, ccr);

    partial void SccExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint cc = (operword >> 8) & 0xFu;
        byte val = (byte)(EvaluateCondition(cc, (byte)(SR & 0xFF)) ? 0xFF : 0x00);
        AluDest dest = ResolveEaDest(srcMode, srcReg, 0u, r.ExtensionWords, out _);   // .b dummy read (address-once)
        WriteResolvedDest(dest, 0u, val);                                             // NO CCR change
    }

    // CMPM (Ay)+,(Ax)+ : compare two postincrement-memory operands; NO write; CMP CCR (X untouched).
    // Ay = bits 11-9 (operand A); Ax = bits 2-0 (operand B). Both (An)+; size = bits 7-6 (.b/.w/.l).
    partial void CmpMExecute(uint operword, CpuEmulator.Core.Jit.DecodeResult r, uint size, uint srcMode, uint srcReg)
    {
        uint ay = (operword >> 9) & 7u;   // (Ay)+ operand A
        uint ax = operword & 7u;          // (Ax)+ operand B
        byte oldCcr = (byte)(SR & 0xFF);
        // Postincrement BOTH (Ax first as the source, then Ay — confirm the order against the bundled CMP vectors).
        uint axAddr = ComputeEa(3u, ax, size, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false);  // (Ax)+
        uint b = ReadSized(axAddr, size) & SizeMask(size);
        uint ayAddr = ComputeEa(3u, ay, size, CpuEmulator.Core.Jit.ExtensionWords.None, pureEa: false);  // (Ay)+
        uint a = ReadSized(ayAddr, size) & SizeMask(size);
        uint result = (a - b) & SizeMask(size);
        SR = (ushort)((SR & 0xFF00) | AluCcr.Cmp(a, b, result, size, false, oldCcr));   // CMP CCR (X kept)
    }
}
