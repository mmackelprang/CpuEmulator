using System.Reflection.Emit;

namespace CpuEmulator.Jit;

/// <summary>The emitted ADC/SBC arms — both the binary and the decimal (BCD) paths, behind an
/// emitted <c>if ((P &amp; 0x08) != 0)</c>. Each line of IL is a one-for-one translation of the
/// interpreter's exact NMOS algorithm (Ground truth E, lifted verbatim from CpuEmitter.EmitAluBody).
/// The interpreter is the oracle; the parity tests + the full TomHarte sweep + the differential
/// fuzzer diff against it, so any transcription error fails loudly (a wrong flag, a wrong result, or
/// an InvalidProgramException) rather than silently. NO cycles are charged here: decimal ADC/SBC are
/// the same cycle count as binary on the NMOS 6502, and the opcode-fetch + operand-resolution cycles
/// already ran (EmitChargeOneCycle up-front + EmitOperandRead). The D-branch is pure compute.</summary>
internal sealed partial class BlockCompiler
{
    /// <summary>Emit NMOS ADC (Ground truth E). 'data' is in ctx.DataLocal. Scratch:
    /// TmpInt='temp' (the binary sum, for the Z-from-binary quirk), NibLocal='before' (low nibble),
    /// SumLocal='sum' (the BCD sum, source of N/V before the +0x60 correction, C after).</summary>
    private void EmitAdc(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;

        // int temp = A + data + (P & 0x01);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, FA);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Add);
        EmitCarryIn(ctx);                                   // + (P & 0x01)
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, ctx.TmpInt);                 // temp

        Label binary = il.DefineLabel(), done = il.DefineLabel();
        // if ((P & 0x08) != 0) <decimal> else <binary>
        EmitDecimalFlagSet(ctx);
        il.Emit(OpCodes.Brfalse, binary);

        // ── decimal arm ──
        // int before = (A & 0x0F) + (data & 0x0F) + (P & 0x01);
        EmitMaskedA(ctx, 0x0F);
        EmitMaskedData(ctx, 0x0F); il.Emit(OpCodes.Add);
        EmitCarryIn(ctx); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, ctx.NibLocal);
        // if (before >= 0x0A) before = ((before + 0x06) & 0x0F) + 0x10;
        {
            Label noAdj = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, ctx.NibLocal);
            il.Emit(OpCodes.Ldc_I4, 0x0A);
            il.Emit(OpCodes.Blt, noAdj);
            il.Emit(OpCodes.Ldloc, ctx.NibLocal);
            il.Emit(OpCodes.Ldc_I4, 0x06); il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Ldc_I4, 0x0F); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I4, 0x10); il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, ctx.NibLocal);
            il.MarkLabel(noAdj);
        }
        // int sum = (A & 0xF0) + (data & 0xF0) + before;
        EmitMaskedA(ctx, 0xF0);
        EmitMaskedData(ctx, 0xF0); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, ctx.SumLocal);
        // P = (P & 0x3C) | (sum & 0x80) | (V) | (Z-from-binary);
        //   V = ((~(A ^ data) & (A ^ sum) & 0x80) != 0 ? 0x40 : 0x00)
        //   Z = ((temp & 0xFF) == 0 ? 0x02 : 0x00)
        EmitStoreP(ctx, () =>
        {
            EmitMaskedP(ctx, 0x3C);
            il.Emit(OpCodes.Ldloc, ctx.SumLocal); il.Emit(OpCodes.Ldc_I4, 0x80); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Or);
            EmitAdcOverflow(ctx, ctx.SumLocal);   // V from the pre-correction sum
            il.Emit(OpCodes.Or);
            EmitZeroFromTemp(ctx);                // Z from the BINARY sum (the quirk)
            il.Emit(OpCodes.Or);
        });
        // if (sum >= 0xA0) sum += 0x60;
        {
            Label noCorr = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, ctx.SumLocal);
            il.Emit(OpCodes.Ldc_I4, 0xA0);
            il.Emit(OpCodes.Blt, noCorr);
            il.Emit(OpCodes.Ldloc, ctx.SumLocal);
            il.Emit(OpCodes.Ldc_I4, 0x60); il.Emit(OpCodes.Add);
            il.Emit(OpCodes.Stloc, ctx.SumLocal);
            il.MarkLabel(noCorr);
        }
        // P = (P & 0xFE) | (sum >= 0x100 ? 0x01 : 0x00);  A = (byte)sum;
        EmitStoreP(ctx, () =>
        {
            EmitMaskedP(ctx, 0xFE);
            EmitGeFlag(ctx, ctx.SumLocal, 0x100, 0x01);   // C from the corrected sum
            il.Emit(OpCodes.Or);
        });
        EmitStoreAFromInt(ctx, ctx.SumLocal);
        il.Emit(OpCodes.Br, done);

        // ── binary arm ──
        // P = (P & 0xBE) | (temp > 0xFF ? 0x01 : 0) | V; A = (byte)temp; P = (P&0x7D)|Z|N
        il.MarkLabel(binary);
        EmitStoreP(ctx, () =>
        {
            EmitMaskedP(ctx, 0xBE);
            EmitGtFlag(ctx, ctx.TmpInt, 0xFF, 0x01);      // C from temp
            il.Emit(OpCodes.Or);
            EmitAdcOverflow(ctx, ctx.TmpInt);             // V from temp
            il.Emit(OpCodes.Or);
        });
        EmitStoreAFromInt(ctx, ctx.TmpInt);
        EmitSetNZFromA(ctx);
        il.MarkLabel(done);
    }

    /// <summary>Emit NMOS SBC (Ground truth E). 'data' is in ctx.DataLocal. temp = A + (data ^ 0xFF)
    /// + (P &amp; 1). The decimal arm corrects ONLY A; ALL flags (C/V/Z/N) come from the binary temp.</summary>
    private void EmitSbc(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;

        // int temp = A + (data ^ 0xFF) + (P & 0x01);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, FA);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Ldc_I4, 0xFF); il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.Add);
        EmitCarryIn(ctx); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, ctx.TmpInt);                 // temp

        Label binary = il.DefineLabel(), done = il.DefineLabel();
        EmitDecimalFlagSet(ctx);
        il.Emit(OpCodes.Brfalse, binary);

        // ── decimal arm ──
        // int before = (A & 0x0F) - (data & 0x0F) + (P & 0x01) - 1;
        EmitMaskedA(ctx, 0x0F);
        EmitMaskedData(ctx, 0x0F); il.Emit(OpCodes.Sub);
        EmitCarryIn(ctx); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Stloc, ctx.NibLocal);
        // if (before < 0) before = ((before - 0x06) & 0x0F) - 0x10;
        {
            Label noAdj = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, ctx.NibLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Bge, noAdj);
            il.Emit(OpCodes.Ldloc, ctx.NibLocal);
            il.Emit(OpCodes.Ldc_I4, 0x06); il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Ldc_I4, 0x0F); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Ldc_I4, 0x10); il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Stloc, ctx.NibLocal);
            il.MarkLabel(noAdj);
        }
        // int sum = (A & 0xF0) - (data & 0xF0) + before;
        EmitMaskedA(ctx, 0xF0);
        EmitMaskedData(ctx, 0xF0); il.Emit(OpCodes.Sub);
        il.Emit(OpCodes.Ldloc, ctx.NibLocal); il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stloc, ctx.SumLocal);
        // if (sum < 0) sum -= 0x60;
        {
            Label noCorr = il.DefineLabel();
            il.Emit(OpCodes.Ldloc, ctx.SumLocal);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Bge, noCorr);
            il.Emit(OpCodes.Ldloc, ctx.SumLocal);
            il.Emit(OpCodes.Ldc_I4, 0x60); il.Emit(OpCodes.Sub);
            il.Emit(OpCodes.Stloc, ctx.SumLocal);
            il.MarkLabel(noCorr);
        }
        // P = (P & 0x3C) | C | V | Z | N  — ALL from the binary temp; A = (byte)sum.
        EmitSbcFlagsFromTemp(ctx, 0x3C);
        EmitStoreAFromInt(ctx, ctx.SumLocal);
        il.Emit(OpCodes.Br, done);

        // ── binary arm ──
        // P = (P & 0xBE) | C | V; A = (byte)temp; P = (P & 0x7D) | Z | N
        il.MarkLabel(binary);
        EmitStoreP(ctx, () =>
        {
            EmitMaskedP(ctx, 0xBE);
            EmitGtFlag(ctx, ctx.TmpInt, 0xFF, 0x01);      // C from temp
            il.Emit(OpCodes.Or);
            EmitSbcOverflow(ctx, ctx.TmpInt);             // V from temp
            il.Emit(OpCodes.Or);
        });
        EmitStoreAFromInt(ctx, ctx.TmpInt);
        EmitSetNZFromA(ctx);
        il.MarkLabel(done);
    }

    // ── Shared decimal-arm IL fragments (each pushes/stores exactly its documented expression) ──

    /// <summary>Push (A &amp; mask) as int.</summary>
    private static void EmitMaskedA(EmitContext ctx, int mask)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0); ctx.Il.Emit(OpCodes.Ldfld, FA);
        ctx.Il.Emit(OpCodes.Ldc_I4, mask); ctx.Il.Emit(OpCodes.And);
    }

    /// <summary>Push (data &amp; mask) as int.</summary>
    private static void EmitMaskedData(EmitContext ctx, int mask)
    {
        ctx.Il.Emit(OpCodes.Ldloc, ctx.DataLocal);
        ctx.Il.Emit(OpCodes.Ldc_I4, mask); ctx.Il.Emit(OpCodes.And);
    }

    /// <summary>Push (P &amp; mask) as int.</summary>
    private static void EmitMaskedP(EmitContext ctx, int mask)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0); ctx.Il.Emit(OpCodes.Ldfld, FP);
        ctx.Il.Emit(OpCodes.Ldc_I4, mask); ctx.Il.Emit(OpCodes.And);
    }

    /// <summary>Push (P &amp; 0x01) — the carry-in.</summary>
    private static void EmitCarryIn(EmitContext ctx)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0); ctx.Il.Emit(OpCodes.Ldfld, FP);
        ctx.Il.Emit(OpCodes.Ldc_I4_1); ctx.Il.Emit(OpCodes.And);
    }

    /// <summary>Push (P &amp; 0x08) — the decimal-flag test value (Brfalse selects the binary arm).</summary>
    private static void EmitDecimalFlagSet(EmitContext ctx)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0); ctx.Il.Emit(OpCodes.Ldfld, FP);
        ctx.Il.Emit(OpCodes.Ldc_I4, 0x08); ctx.Il.Emit(OpCodes.And);
    }

    /// <summary>Push ((local &amp; 0xFF) == 0 ? 0x02 : 0x00) — the Z-from-binary term (reads TmpInt).</summary>
    private static void EmitZeroFromTemp(EmitContext ctx)
    {
        ILGenerator il = ctx.Il;
        Label nz = il.DefineLabel(), zdone = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, ctx.TmpInt);
        il.Emit(OpCodes.Ldc_I4, 0xFF); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Brtrue, nz);
        il.Emit(OpCodes.Ldc_I4_2); il.Emit(OpCodes.Br, zdone);
        il.MarkLabel(nz);
        il.Emit(OpCodes.Ldc_I4_0);
        il.MarkLabel(zdone);
    }

    /// <summary>Push (src &gt; threshold ? flag : 0).</summary>
    private static void EmitGtFlag(EmitContext ctx, LocalBuilder src, int threshold, int flag)
    {
        ILGenerator il = ctx.Il;
        Label yes = il.DefineLabel(), gdone = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, src);
        il.Emit(OpCodes.Ldc_I4, threshold);
        il.Emit(OpCodes.Bgt, yes);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Br, gdone);
        il.MarkLabel(yes);
        il.Emit(OpCodes.Ldc_I4, flag);
        il.MarkLabel(gdone);
    }

    /// <summary>Push (src &gt;= threshold ? flag : 0).</summary>
    private static void EmitGeFlag(EmitContext ctx, LocalBuilder src, int threshold, int flag)
    {
        ILGenerator il = ctx.Il;
        Label yes = il.DefineLabel(), gdone = il.DefineLabel();
        il.Emit(OpCodes.Ldloc, src);
        il.Emit(OpCodes.Ldc_I4, threshold);
        il.Emit(OpCodes.Bge, yes);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Br, gdone);
        il.MarkLabel(yes);
        il.Emit(OpCodes.Ldc_I4, flag);
        il.MarkLabel(gdone);
    }

    /// <summary>Push the ADC overflow term: ((~(A ^ data) &amp; (A ^ res) &amp; 0x80) != 0 ? 0x40 : 0).
    /// <paramref name="res"/> is 'sum' (decimal, pre-correction) or 'temp' (binary).</summary>
    private static void EmitAdcOverflow(EmitContext ctx, LocalBuilder res)
    {
        ILGenerator il = ctx.Il;
        Label yes = il.DefineLabel(), vdone = il.DefineLabel();
        // ~(A ^ data)
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, FA);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.Not);
        // & (A ^ res)
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, FA);
        il.Emit(OpCodes.Ldloc, res); il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.And);
        // & 0x80
        il.Emit(OpCodes.Ldc_I4, 0x80); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Brtrue, yes);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Br, vdone);
        il.MarkLabel(yes);
        il.Emit(OpCodes.Ldc_I4, 0x40);
        il.MarkLabel(vdone);
    }

    /// <summary>Push the SBC overflow term: (((A ^ data) &amp; (A ^ temp) &amp; 0x80) != 0 ? 0x40 : 0).</summary>
    private static void EmitSbcOverflow(EmitContext ctx, LocalBuilder temp)
    {
        ILGenerator il = ctx.Il;
        Label yes = il.DefineLabel(), vdone = il.DefineLabel();
        // (A ^ data)
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, FA);
        il.Emit(OpCodes.Ldloc, ctx.DataLocal); il.Emit(OpCodes.Xor);
        // & (A ^ temp)
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, FA);
        il.Emit(OpCodes.Ldloc, temp); il.Emit(OpCodes.Xor);
        il.Emit(OpCodes.And);
        // & 0x80
        il.Emit(OpCodes.Ldc_I4, 0x80); il.Emit(OpCodes.And);
        il.Emit(OpCodes.Brtrue, yes);
        il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Br, vdone);
        il.MarkLabel(yes);
        il.Emit(OpCodes.Ldc_I4, 0x40);
        il.MarkLabel(vdone);
    }

    /// <summary>Emit the SBC decimal flag store: P = (P &amp; mask) | C | V | Z | N, ALL from the
    /// binary temp (Ground truth E: decimal SBC's flags are the binary-path flags).</summary>
    private static void EmitSbcFlagsFromTemp(EmitContext ctx, int mask)
    {
        ILGenerator il = ctx.Il;
        EmitStoreP(ctx, () =>
        {
            EmitMaskedP(ctx, mask);
            EmitGtFlag(ctx, ctx.TmpInt, 0xFF, 0x01);      // C
            il.Emit(OpCodes.Or);
            EmitSbcOverflow(ctx, ctx.TmpInt);             // V
            il.Emit(OpCodes.Or);
            EmitZeroFromTemp(ctx);                        // Z (temp & 0xFF) == 0
            il.Emit(OpCodes.Or);
            // N = (temp & 0x80)
            il.Emit(OpCodes.Ldloc, ctx.TmpInt); il.Emit(OpCodes.Ldc_I4, 0x80); il.Emit(OpCodes.And);
            il.Emit(OpCodes.Or);
        });
    }

    /// <summary>Frame a P assignment: pushes cpu, runs <paramref name="pushValue"/> (which must leave
    /// the new P value on the stack), then <c>conv.u1; stfld P</c>.</summary>
    private static void EmitStoreP(EmitContext ctx, System.Action pushValue)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0);   // cpu (for the trailing Stfld)
        pushValue();
        ctx.Il.Emit(OpCodes.Conv_U1);
        ctx.Il.Emit(OpCodes.Stfld, FP);
    }

    /// <summary>A = (byte)src.</summary>
    private static void EmitStoreAFromInt(EmitContext ctx, LocalBuilder src)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0);
        ctx.Il.Emit(OpCodes.Ldloc, src);
        ctx.Il.Emit(OpCodes.Conv_U1);
        ctx.Il.Emit(OpCodes.Stfld, FA);
    }

    /// <summary>P = (P &amp; 0x7D) | (A == 0 ? 2 : 0) | (A &amp; 0x80) — the binary-arm trailing SetNZ
    /// computed from the freshly-stored A (matches the interpreter's <c>A == 0</c> / <c>A &amp; 0x80</c>).</summary>
    private static void EmitSetNZFromA(EmitContext ctx)
    {
        ctx.Il.Emit(OpCodes.Ldarg_0); ctx.Il.Emit(OpCodes.Ldfld, FA);
        EmitSetNZFromStack(ctx);
    }
}
