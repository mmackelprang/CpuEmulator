namespace CpuEmulator.Cpus.M8086;

/// <summary>
/// M5.5d — the 8086 STRING op bodies (hand-written): MOVS/CMPS/SCAS/LODS/STOS (byte + word) and the
/// REP/REPE/REPNE prefix (the CX-counted, DF-directed repeat). Dispatched by the generated <c>ExecuteX86</c> to
/// <see cref="StringExecute"/>. Reuses the M5.5a EA byte/word bus helpers + the M5.5b flag core (CMPS/SCAS set
/// flags exactly like CMP/SUB).
///
/// <para><b>The string mechanics (reconciled byte-exact against the A4-AF corpus).</b> The SI/DI index registers
/// step by ±1 (byte ops) or ±2 (word ops) per iteration, DIRECTED by DF: DF=0 ⇒ increment, DF=1 ⇒ decrement.
/// The SOURCE operand is <c>DS:SI</c> (DS overridable by a segment-override prefix); the DESTINATION is
/// <c>ES:DI</c> (ES is NON-overridable — a prefix does NOT redirect the string destination). MOVS copies
/// DS:SI→ES:DI; CMPS compares DS:SI − ES:DI (flags, no write); SCAS compares AL/AX − ES:DI; LODS loads AL/AX
/// ← DS:SI; STOS stores AL/AX → ES:DI.</para>
///
/// <para><b>REP (the one-instruction CX loop — the Z80 LDIR/CPIR precedent).</b> A repeat prefix (F3 REP/REPE,
/// F2 REPNE) makes the whole string op a SINGLE instruction that loops CX times WITHIN one Step (the runner
/// Steps once per case). For MOVS/STOS/LODS the count is unconditional: repeat while CX ≠ 0, decrementing CX
/// each iteration. For the COMPARE ops (CMPS/SCAS) the prefix is REPE (F3 ⇒ repeat while ZF=1) or REPNE (F2 ⇒
/// repeat while ZF=0): each iteration decrements CX, does the compare (which sets ZF), then stops when CX hits 0
/// OR the ZF condition fails. With CX=0 going in, a REP op does ZERO iterations (no register/memory change). The
/// no-wait-state 8088 set does not interrupt a REP mid-loop, so the full loop runs in one Step.</para>
/// </summary>
public sealed partial class M8086Cpu
{
    /// <summary>Step SI and/or DI for one string iteration: ±1 for a byte op, ±2 for a word op, the SIGN chosen
    /// by DF (0 ⇒ +, 1 ⇒ -). <paramref name="stepSi"/>/<paramref name="stepDi"/> select which index advances
    /// (LODS steps only SI; STOS/SCAS only DI; MOVS/CMPS both).</summary>
    private void StringStep(bool word, bool stepSi, bool stepDi)
    {
        int delta = (FLAGS & FlagDF) != 0 ? (word ? -2 : -1) : (word ? 2 : 1);
        if (stepSi) SI = (ushort)(SI + delta);
        if (stepDi) DI = (ushort)(DI + delta);
    }

    /// <summary>M5.5d: execute one string instruction (optionally REP-prefixed). The repeat byte (F3/F2 or 0)
    /// rides <c>r.X86.RepPrefix</c> — F3 ⇒ REP/REPE, F2 ⇒ REPNE; 0 ⇒ a single, unconditional iteration.</summary>
    partial void StringExecute(uint key, CpuEmulator.Core.Jit.DecodeResult r)
    {
        byte rep = r.X86.RepPrefix;                                   // 0xF3 REP/REPE, 0xF2 REPNE, 0 none
        bool repeated = rep != 0;
        // The SOURCE segment (DS, overridable). The DESTINATION segment is always ES (non-overridable).
        X86SegmentOverride over = OverrideFromByte(r.X86.SegOverride);
        ushort srcSeg = ResolveSegment(DS, over);

        // Whether the op is a COMPARE (CMPS/SCAS) — its repeat is ZF-conditioned (REPE/REPNE); the others repeat
        // unconditionally until CX == 0.
        bool isCompare = key is 0xA6u or 0xA7u or 0xAEu or 0xAFu;

        // The single-iteration body. Returns ZF (only meaningful for the compare ops; ignored otherwise).
        bool DoOnce()
        {
            switch (key)
            {
                case 0xA4u:   // MOVSB: ES:DI <- DS:SI
                {
                    byte v = ReadEaByte(Physical(srcSeg, SI));
                    WriteEaByte(Physical(ES, DI), v);
                    StringStep(false, stepSi: true, stepDi: true);
                    return false;
                }
                case 0xA5u:   // MOVSW
                {
                    ushort v = ReadEaWordWrapped(srcSeg, SI);
                    WriteEaWordWrapped(ES, DI, v);
                    StringStep(true, stepSi: true, stepDi: true);
                    return false;
                }
                case 0xA6u:   // CMPSB: compare DS:SI - ES:DI (flags only)
                {
                    byte s = ReadEaByte(Physical(srcSeg, SI));
                    byte d = ReadEaByte(Physical(ES, DI));
                    SubFlags(s, d, 0, false);
                    StringStep(false, stepSi: true, stepDi: true);
                    return (FLAGS & FlagZF) != 0;
                }
                case 0xA7u:   // CMPSW
                {
                    ushort s = ReadEaWordWrapped(srcSeg, SI);
                    ushort d = ReadEaWordWrapped(ES, DI);
                    SubFlags(s, d, 0, true);
                    StringStep(true, stepSi: true, stepDi: true);
                    return (FLAGS & FlagZF) != 0;
                }
                case 0xACu:   // LODSB: AL <- DS:SI
                {
                    AL = ReadEaByte(Physical(srcSeg, SI));
                    StringStep(false, stepSi: true, stepDi: false);
                    return false;
                }
                case 0xADu:   // LODSW: AX <- DS:SI
                {
                    AX = ReadEaWordWrapped(srcSeg, SI);
                    StringStep(true, stepSi: true, stepDi: false);
                    return false;
                }
                case 0xAAu:   // STOSB: ES:DI <- AL
                {
                    WriteEaByte(Physical(ES, DI), AL);
                    StringStep(false, stepSi: false, stepDi: true);
                    return false;
                }
                case 0xABu:   // STOSW: ES:DI <- AX
                {
                    WriteEaWordWrapped(ES, DI, AX);
                    StringStep(true, stepSi: false, stepDi: true);
                    return false;
                }
                case 0xAEu:   // SCASB: compare AL - ES:DI (flags only)
                {
                    byte d = ReadEaByte(Physical(ES, DI));
                    SubFlags(AL, d, 0, false);
                    StringStep(false, stepSi: false, stepDi: true);
                    return (FLAGS & FlagZF) != 0;
                }
                case 0xAFu:   // SCASW
                {
                    ushort d = ReadEaWordWrapped(ES, DI);
                    SubFlags(AX, d, 0, true);
                    StringStep(true, stepSi: false, stepDi: true);
                    return (FLAGS & FlagZF) != 0;
                }
                default:
                    return false;
            }
        }

        if (!repeated)
        {
            DoOnce();   // a plain (non-REP) string op is exactly one iteration; CX untouched.
            return;
        }

        // REP: loop until CX == 0 (decrementing each iteration). For a compare op, the repeat is ZF-conditioned:
        // REPE (F3) stops as soon as ZF=0; REPNE (F2) stops as soon as ZF=1. With CX=0 going in, ZERO iterations.
        bool repWhileZfSet = rep == 0xF3u;   // F3 ⇒ REPE (repeat while ZF=1); F2 ⇒ REPNE (repeat while ZF=0)
        while (CX != 0)
        {
            CX = (ushort)(CX - 1);
            bool zf = DoOnce();
            if (isCompare && zf != repWhileZfSet)
                break;   // the ZF condition failed ⇒ stop the repeat (after this iteration's compare + step)
        }
    }
}
