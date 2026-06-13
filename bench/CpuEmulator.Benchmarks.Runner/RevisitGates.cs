using System.Diagnostics;

namespace CpuEmulator.Benchmarks.Runner;

/// <summary>The two M2 revisit-gate micro-benches (Task 9), measured + printed so the decision is
/// data-driven, not a prior. These are MODEL micro-benches (isolated dispatch / state-layout shapes),
/// not full-CPU runs — the realized Tier-0 interpreter uses a dense <c>switch (opcode)</c> with
/// per-opcode methods + register FIELDS on the class; the JIT (Tier-1) emits straight-line IL and
/// uses NEITHER, so both gates are purely Tier-0 questions. The bar (recorded): change the
/// implementation only on a MATERIAL win; otherwise "measured, kept current."</summary>
public static class RevisitGates
{
    public static void RunAndPrint()
    {
        Console.WriteLine("== Task 9 revisit-gate micro-benches ==\n");
        GateA_SwitchVsDelegatePointer();
        Console.WriteLine();
        GateB_FieldsVsStruct();
    }

    // ── Gate A: switch-on-opcode vs delegate*<void>[256] dispatch ────────────────────────────────
    // The realized interpreter dispatches with `switch (opcode)`. The recorded M2 question: would a
    // function-pointer table be faster (a dense switch may already lower to a jump table; an indirect
    // call may mispredict)? We dispatch the SAME pseudo-random opcode stream through both shapes,
    // each handler doing a tiny, identical state update, over a large iteration count.

    private static int _accA;
    private static readonly unsafe delegate*<void>[] _table = BuildTable();

    private static void GateA_SwitchVsDelegatePointer()
    {
        const int N = 200_000_000;
        byte[] stream = PseudoRandomOpcodes(4096);

        // Warmup both.
        DispatchSwitch(stream, 1_000_000);
        DispatchTable(stream, 1_000_000);

        _accA = 0;
        var sw1 = Stopwatch.StartNew();
        DispatchSwitch(stream, N);
        sw1.Stop();
        int switchAcc = _accA;

        _accA = 0;
        var sw2 = Stopwatch.StartNew();
        DispatchTable(stream, N);
        sw2.Stop();
        int tableAcc = _accA;

        double switchRate = N / sw1.Elapsed.TotalSeconds;
        double tableRate = N / sw2.Elapsed.TotalSeconds;
        Console.WriteLine("Gate A — dispatch: switch (realized) vs delegate*<void>[256]");
        Console.WriteLine($"  switch   : {switchRate,15:N0} dispatches/sec ({sw1.Elapsed.TotalMilliseconds:F0} ms)");
        Console.WriteLine($"  delegate*: {tableRate,15:N0} dispatches/sec ({sw2.Elapsed.TotalMilliseconds:F0} ms)");
        Console.WriteLine($"  delegate*/switch = {tableRate / switchRate:F3}x  (acc check {switchAcc}=={tableAcc})");
        Console.WriteLine($"  => {Verdict(tableRate, switchRate, "delegate*", "switch")}");
    }

    private static void DispatchSwitch(byte[] stream, int n)
    {
        int i = 0;
        for (int k = 0; k < n; k++)
        {
            byte op = stream[i];
            i = (i + 1) & (stream.Length - 1);
            // A dense switch over 16 representative buckets (the realized dispatch is 151 cases; the
            // shape — jump-table lowering — is what we measure, not the case count).
            switch (op & 0x0F)
            {
                case 0x0: _accA += 1; break;
                case 0x1: _accA ^= 2; break;
                case 0x2: _accA += 3; break;
                case 0x3: _accA ^= 4; break;
                case 0x4: _accA += 5; break;
                case 0x5: _accA ^= 6; break;
                case 0x6: _accA += 7; break;
                case 0x7: _accA ^= 8; break;
                case 0x8: _accA += 9; break;
                case 0x9: _accA ^= 10; break;
                case 0xA: _accA += 11; break;
                case 0xB: _accA ^= 12; break;
                case 0xC: _accA += 13; break;
                case 0xD: _accA ^= 14; break;
                case 0xE: _accA += 15; break;
                default: _accA ^= 16; break;
            }
        }
    }

    private static unsafe void DispatchTable(byte[] stream, int n)
    {
        int i = 0;
        for (int k = 0; k < n; k++)
        {
            byte op = stream[i];
            i = (i + 1) & (stream.Length - 1);
            _table[op & 0x0F]();
        }
    }

    private static unsafe delegate*<void>[] BuildTable()
    {
        return
        [
            &H0, &H1, &H2, &H3, &H4, &H5, &H6, &H7,
            &H8, &H9, &HA, &HB, &HC, &HD, &HE, &HF,
        ];
    }

    /// <summary>A deterministic pseudo-random opcode stream of length <paramref name="len"/> (a power
    /// of two, so the dispatch loop masks the index instead of branching). Deterministic so the two
    /// dispatch shapes see identical input.</summary>
    private static byte[] PseudoRandomOpcodes(int len)
    {
        var rng = new Random(12345);
        var s = new byte[len];
        rng.NextBytes(s);
        return s;
    }

    private static void H0() => _accA += 1;
    private static void H1() => _accA ^= 2;
    private static void H2() => _accA += 3;
    private static void H3() => _accA ^= 4;
    private static void H4() => _accA += 5;
    private static void H5() => _accA ^= 6;
    private static void H6() => _accA += 7;
    private static void H7() => _accA ^= 8;
    private static void H8() => _accA += 9;
    private static void H9() => _accA ^= 10;
    private static void HA() => _accA += 11;
    private static void HB() => _accA ^= 12;
    private static void HC() => _accA += 13;
    private static void HD() => _accA ^= 14;
    private static void HE() => _accA += 15;
    private static void HF() => _accA ^= 16;

    // ── Gate B: register state as class FIELDS (realized) vs a mutable STRUCT ─────────────────────
    // The realized Mos6502Cpu holds A/X/Y/S/P/PC as fields on the class. The recorded M2 question:
    // would a `ref struct` state register-allocate better? We run an identical tight register-update
    // loop with the state held both ways, over a large iteration count.

    private sealed class ClassState { public byte A, X, Y, S, P; public ushort PC; }
    private struct StructState { public byte A, X, Y, S, P; public ushort PC; }

    private static void GateB_FieldsVsStruct()
    {
        const int N = 400_000_000;

        // Warmup.
        RunClass(1_000_000);
        RunStruct(1_000_000);

        var sw1 = Stopwatch.StartNew();
        int classAcc = RunClass(N);
        sw1.Stop();

        var sw2 = Stopwatch.StartNew();
        int structAcc = RunStruct(N);
        sw2.Stop();

        double classRate = N / sw1.Elapsed.TotalSeconds;
        double structRate = N / sw2.Elapsed.TotalSeconds;
        Console.WriteLine("Gate B — state layout: fields-on-class (realized) vs mutable struct");
        Console.WriteLine($"  fields-on-class: {classRate,15:N0} updates/sec ({sw1.Elapsed.TotalMilliseconds:F0} ms)");
        Console.WriteLine($"  struct         : {structRate,15:N0} updates/sec ({sw2.Elapsed.TotalMilliseconds:F0} ms)");
        Console.WriteLine($"  struct/class = {structRate / classRate:F3}x  (acc check {classAcc}=={structAcc})");
        Console.WriteLine($"  => {Verdict(structRate, classRate, "struct", "fields-on-class")}");
    }

    private static int RunClass(int n)
    {
        var s = new ClassState { A = 1, X = 2, Y = 3, S = 0xFD, P = 0x34, PC = 0x0200 };
        for (int k = 0; k < n; k++)
        {
            s.A = unchecked((byte)(s.A + s.X));
            s.X = unchecked((byte)(s.X ^ s.Y));
            s.Y = unchecked((byte)(s.Y + 1));
            s.P = unchecked((byte)((s.P & 0x7D) | (s.A == 0 ? 0x02 : 0) | (s.A & 0x80)));
            s.PC = unchecked((ushort)(s.PC + 1));
        }
        return s.A + s.X + s.Y + s.P + s.PC;
    }

    private static int RunStruct(int n)
    {
        var s = new StructState { A = 1, X = 2, Y = 3, S = 0xFD, P = 0x34, PC = 0x0200 };
        for (int k = 0; k < n; k++)
        {
            s.A = unchecked((byte)(s.A + s.X));
            s.X = unchecked((byte)(s.X ^ s.Y));
            s.Y = unchecked((byte)(s.Y + 1));
            s.P = unchecked((byte)((s.P & 0x7D) | (s.A == 0 ? 0x02 : 0) | (s.A & 0x80)));
            s.PC = unchecked((ushort)(s.PC + 1));
        }
        return s.A + s.X + s.Y + s.P + s.PC;
    }

    // The recorded bar: a candidate is a MATERIAL win only above +10%. Below that it is "in the
    // noise — keep current" (the realized switch / fields-on-class).
    private static string Verdict(double candidate, double current, string candName, string curName)
    {
        double ratio = candidate / current;
        if (ratio > 1.10) return $"MATERIAL win for {candName} ({ratio:F2}x) — record as a follow-up";
        if (ratio < 0.90) return $"{curName} is materially faster ({1 / ratio:F2}x) — keep {curName}";
        return $"in the noise ({ratio:F2}x) — measured, KEEP CURRENT ({curName})";
    }
}
