namespace CpuEmulator.Cpus.M8086;

/// <summary>
/// M5.5b — the 8086 BCD/ASCII decimal-adjust op bodies: DAA (0x27), DAS (0x2F), AAA (0x37), AAS (0x3F),
/// AAM (0xD4), AAD (0xD5). Dispatched by the generated <c>ExecuteX86</c> to <see cref="BcdExecute"/>. Reuses
/// the flag-computation primitives in M8086Cpu.Alu.cs (the same partial class): the FLAGS bit masks, SetFlag,
/// and SetSzp (SF/ZF/PF from the final AL).
///
/// <para>These follow the EXACT 8086 silicon algorithms — the TomHarte vectors are unforgiving about the
/// pre-/post-adjust ordering and the CF stickiness. DAA/DAS read the ORIGINAL AL + the ORIGINAL CF for the
/// high-nibble test (NOT the post-low-adjust AL), and the high adjust ORs into CF. AAA/AAS adjust AX and force
/// AL &amp; 0x0F. The undefined flags (OF for DAA/DAS; OF/SF/ZF/PF for AAA/AAS) are left as natural fallout — the
/// TomHarte flags-mask excludes them (DAA/DAS mask 0xF7FF, AAA/AAS mask 63291, AAM/AAD 63470).</para>
/// </summary>
public sealed partial class M8086Cpu
{
    /// <summary>M5.5b: execute one BCD/ASCII-adjust instruction.</summary>
    partial void BcdExecute(uint key, CpuEmulator.Core.Jit.DecodeResult r)
    {
        switch (key)
        {
            case 0x27: Daa(); break;
            case 0x2F: Das(); break;
            case 0x37: Aaa(); break;
            case 0x3F: Aas(); break;
            case 0xD4: Aam((byte)r.X86.Imm); break;
            case 0xD5: Aad((byte)r.X86.Imm); break;
        }
    }

    // The high-nibble adjust threshold is AF-DEPENDENT on the 8086 (derived against the full TomHarte corpus):
    // when AF was set going in, the high adjust fires at old_AL > 0x9F; when AF was clear, at old_AL > 0x99.
    // (The silicon's comparator absorbs the auxiliary state — the famous DAA/DAS subtlety the vectors pin.)
    private const byte DaaHighThresholdAfSet   = 0x9F;
    private const byte DaaHighThresholdAfClear = 0x99;

    /// <summary>DAA — decimal adjust AL after addition. Capture old AL + old CF + old AF, clear CF. Low adjust:
    /// if (AL&amp;0xF)&gt;9 OR AF, AL += 6, CF = old_CF OR (the add carried out of the byte), AF = 1; else AF = 0.
    /// High adjust: if old_AL &gt; (AF-dependent threshold) OR old_CF, AL += 0x60, CF = 1; else CF = 0. SF/ZF/PF
    /// from the final AL. (Verified AL/CF/AF byte-exact against all 10,000 DAA vectors.)</summary>
    private void Daa()
    {
        byte oldAl = AL;
        bool oldCf = (FLAGS & FlagCF) != 0;
        bool oldAf = (FLAGS & FlagAF) != 0;
        byte highThreshold = oldAf ? DaaHighThresholdAfSet : DaaHighThresholdAfClear;
        SetFlag(FlagCF, false);

        if ((AL & 0x0F) > 9 || oldAf)
        {
            int sum = AL + 6;
            AL = (byte)sum;
            SetFlag(FlagCF, oldCf || sum > 0xFF);   // low add CAN carry out of the byte → CF
            SetFlag(FlagAF, true);
        }
        else
        {
            SetFlag(FlagAF, false);
        }

        if (oldAl > highThreshold || oldCf)
        {
            AL = (byte)(AL + 0x60);
            SetFlag(FlagCF, true);
        }
        else
        {
            SetFlag(FlagCF, false);
        }

        SetSzp(AL, width16: false);
    }

    /// <summary>DAS — decimal adjust AL after subtraction (the borrow-direction analogue of DAA). The low adjust
    /// borrow does NOT set CF (only old_CF propagates through the low branch — distinct from DAA's low add,
    /// which CAN carry out); the high adjust SETS CF. Same AF-dependent high threshold. (Verified AL/CF/AF
    /// byte-exact against all 10,000 DAS vectors.)</summary>
    private void Das()
    {
        byte oldAl = AL;
        bool oldCf = (FLAGS & FlagCF) != 0;
        bool oldAf = (FLAGS & FlagAF) != 0;
        byte highThreshold = oldAf ? DaaHighThresholdAfSet : DaaHighThresholdAfClear;
        SetFlag(FlagCF, false);

        if ((AL & 0x0F) > 9 || oldAf)
        {
            AL = (byte)(AL - 6);
            SetFlag(FlagCF, oldCf);   // the low borrow is absorbed into AF; CF reflects only old_CF here
            SetFlag(FlagAF, true);
        }
        else
        {
            SetFlag(FlagAF, false);
        }

        if (oldAl > highThreshold || oldCf)
        {
            AL = (byte)(AL - 0x60);
            SetFlag(FlagCF, true);
        }
        // NOTE: DAS does NOT clear CF in the else branch (the high adjust only SETS it; the low branch already
        // resolved CF). This is the pinned 8086 behavior the vectors expect.

        SetSzp(AL, width16: false);
    }

    /// <summary>AAA — ASCII adjust AL after addition. If (AL&amp;0xF)&gt;9 OR AF: AL += 6 (masked to a byte),
    /// AH += 1 (INDEPENDENTLY — NOT a 16-bit AX+=0x106, which would double-propagate the AL+6 byte-carry into
    /// AH), AF = CF = 1; else AF = CF = 0. Then AL &amp;= 0x0F. (Verified AX/CF byte-exact against all 10,000
    /// AAA vectors.) OF/SF/ZF/PF are undefined → masked; set from AL for determinism.</summary>
    private void Aaa()
    {
        if ((AL & 0x0F) > 9 || (FLAGS & FlagAF) != 0)
        {
            AL = (byte)(AL + 6);
            AH = (byte)(AH + 1);
            SetFlag(FlagAF, true);
            SetFlag(FlagCF, true);
        }
        else
        {
            SetFlag(FlagAF, false);
            SetFlag(FlagCF, false);
        }
        AL = (byte)(AL & 0x0F);
        SetSzp(AL, width16: false);
    }

    /// <summary>AAS — ASCII adjust AL after subtraction. If (AL&amp;0xF)&gt;9 OR AF: AL -= 6 (masked), AH -= 1
    /// (INDEPENDENTLY), AF = CF = 1; else AF = CF = 0. Then AL &amp;= 0x0F. (Verified AX/CF byte-exact against
    /// all 10,000 AAS vectors.)</summary>
    private void Aas()
    {
        if ((AL & 0x0F) > 9 || (FLAGS & FlagAF) != 0)
        {
            AL = (byte)(AL - 6);
            AH = (byte)(AH - 1);
            SetFlag(FlagAF, true);
            SetFlag(FlagCF, true);
        }
        else
        {
            SetFlag(FlagAF, false);
            SetFlag(FlagCF, false);
        }
        AL = (byte)(AL & 0x0F);
        SetSzp(AL, width16: false);
    }

    /// <summary>AAM — ASCII adjust AX after multiply. base = the imm8 (normally 0x0A): AH = AL / base,
    /// AL = AL % base. SF/ZF/PF from the final AL. base == 0 ⇒ divide error (INT0) — HONEST DEFERRAL to M5.5d
    /// (route to HandleUndefinedOpcode, leave state unchanged) like DIV.</summary>
    private void Aam(byte baseByte)
    {
        if (baseByte == 0)
        {
            // M5.5b honest deferral: AAM base 0 → INT0 (divide-error vector). The interrupt push is M5.5d.
            HandleUndefinedOpcode(0xD4);
            return;
        }
        byte al = AL;
        AH = (byte)(al / baseByte);
        AL = (byte)(al % baseByte);
        SetSzp(AL, width16: false);
    }

    /// <summary>AAD — ASCII adjust AX before division. base = the imm8: AL = (AL + AH*base) &amp; 0xFF, AH = 0.
    /// SF/ZF/PF from the final AL.</summary>
    private void Aad(byte baseByte)
    {
        AL = (byte)((AL + AH * baseByte) & 0xFF);
        AH = 0;
        SetSzp(AL, width16: false);
    }
}
