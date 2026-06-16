namespace CpuEmulator.Benchmarks;

/// <summary>The two canonical portable benchmark workloads (Ground truth F). Both are portable byte
/// images with no host I/O in the measured window, so every subject runs identical work.
/// <list type="bullet">
/// <item><b>W1 (Klaus-deterministic):</b> the Klaus 6502 functional-test image run to its $3469
/// success trap — 96,241,367 cycles of identical work, the integration-realistic mix
/// (loads/stores/branches/ADC/SBC/SMC). Loaded from the same vector cache the test suite uses (NOT
/// vendored — the TomHarte-vector pattern); absent =&gt; W1 is skipped with the fetch instruction.</item>
/// <item><b>W2 (arithmetic kernel):</b> a tight, hand-written ADC/SBC + branch loop committed as a
/// byte[] constant, run for a fixed cycle cap. ADC/SBC-and-branch-heavy, so it isolates the
/// decimal-arm + chaining payoff from the I/O-free hot path.</item>
/// </list></summary>
public static class Workloads
{
    /// <summary>The Klaus anchor cycle count (PR #8/#10/#11/#12 actual; M2-i reached it EXACTLY).</summary>
    public const long KlausExpectedCycles = 96_241_367;
    public const ushort KlausStart = 0x0400;
    public const ushort KlausSuccessTrap = 0x3469;

    /// <summary>Locate the Klaus functional-test binary in the shared vector cache (the same scheme
    /// the test suite uses — CPUEMULATOR_TESTVECTORS or ~/.cache/cpuemulator/vectors). Returns null
    /// when absent so the caller skips W1 with the fetch instruction.</summary>
    public static string? KlausBinaryPathOrNull()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "klaus", "6502_functional_test.bin");
        return File.Exists(path) ? path : null;
    }

    /// <summary>W1 — the Klaus deterministic run. Returns null when the binary is not in the vector
    /// cache (run tools/get-klaus.ps1 or set CPUEMULATOR_TESTVECTORS); the caller skips W1 then.</summary>
    public static BenchWorkload? KlausOrNull()
    {
        string? path = KlausBinaryPathOrNull();
        if (path is null) return null;
        byte[] image = File.ReadAllBytes(path);
        if (image.Length != 0x10000) return null;
        return new BenchWorkload(
            Name: "W1 Klaus-deterministic",
            Image: image,
            LoadAddress: 0x0000,
            StartPc: KlausStart,
            SuccessTrapPc: KlausSuccessTrap,
            FixedCycleCap: null,
            ExpectedCycles: KlausExpectedCycles);
    }

    /// <summary>The W2 kernel's fixed cycle cap (the measured window length, in emulated cycles).
    /// Large enough that subprocess-launch overhead amortizes (Ground truth F fairness note).</summary>
    public const long ArithKernelCycleCap = 50_000_000;

    /// <summary>W2 — the arithmetic kernel. A 64 KiB image whose code at $0200 is a tight ADC/SBC +
    /// branch loop (assembled by hand below); reset/IRQ vectors point at the start. Run for
    /// <see cref="ArithKernelCycleCap"/> cycles (no success trap — the loop spins forever, the cap
    /// terminates it). ExpectedCycles is the cap (a subject that diverges typically over/undershoots
    /// the cap boundary, caught by the adapter's verification).</summary>
    public static BenchWorkload ArithmeticKernel()
    {
        var image = new byte[0x10000];

        // ── The kernel: a sum-of-products / running-checksum inner loop, ADC/SBC + branch heavy.
        // It touches zero-page scratch ($10..$12), exercises the carry/decimal ALU path, and loops
        // via DEX/BNE (the branch the chainer + the Tier-0 dispatch both stress). CLD up front keeps
        // it binary-mode (the decimal arm is exercised by the TomHarte decimal sweep + the fuzzer;
        // W2's value is the hot binary ADC/SBC + branch path under chaining, the common case).
        //
        //         CLD                ; D8        binary mode
        //         LDA #$00           ; A9 00     acc = 0
        //         STA $10            ; 85 10
        //         LDX #$00           ; A2 00     outer counter
        // outer:  LDY #$80           ; A0 80     inner counter (128 iterations)
        // inner:  CLC                ; 18
        //         ADC $10            ; 65 10     acc += scratch
        //         ADC #$25           ; 69 25     acc += 0x25
        //         SEC                ; 38
        //         SBC #$11           ; E9 11     acc -= 0x11
        //         EOR #$5A           ; 49 5A     mix
        //         STA $10            ; 85 10     write back (RAM store, no SMC: $10 not in code)
        //         DEY                ; 88
        //         BNE inner          ; D0 F1     loop inner (taken branch — the hot chain edge)
        //         INX                ; E8
        //         BNE outer          ; D0 EB     loop outer
        //         JMP start          ; 4C xx xx  restart the whole kernel (keeps it running forever)
        ushort pc = 0x0200;
        ushort start = pc;
        void Emit(params byte[] bytes) { foreach (byte b in bytes) image[pc++] = b; }

        Emit(0xD8);                     // CLD
        Emit(0xA9, 0x00);               // LDA #$00
        Emit(0x85, 0x10);               // STA $10
        Emit(0xA2, 0x00);               // LDX #$00
        ushort outer = pc;
        Emit(0xA0, 0x80);               // LDY #$80
        ushort inner = pc;
        Emit(0x18);                     // CLC
        Emit(0x65, 0x10);               // ADC $10
        Emit(0x69, 0x25);               // ADC #$25
        Emit(0x38);                     // SEC
        Emit(0xE9, 0x11);               // SBC #$11
        Emit(0x49, 0x5A);               // EOR #$5A
        Emit(0x85, 0x10);               // STA $10
        Emit(0x88);                     // DEY
        Emit(0xD0, (byte)(inner - (pc + 2)));   // BNE inner
        Emit(0xE8);                     // INX
        Emit(0xD0, (byte)(outer - (pc + 2)));   // BNE outer
        Emit(0x4C, (byte)(start & 0xFF), (byte)(start >> 8));  // JMP start

        // Reset/IRQ/NMI vectors (so a fresh CPU that resets lands at the kernel; the bench sets PC
        // explicitly, but valid vectors keep the image well-formed for any subject that uses them).
        image[0xFFFC] = (byte)(start & 0xFF); image[0xFFFD] = (byte)(start >> 8);
        image[0xFFFA] = (byte)(start & 0xFF); image[0xFFFB] = (byte)(start >> 8);
        image[0xFFFE] = (byte)(start & 0xFF); image[0xFFFF] = (byte)(start >> 8);

        return new BenchWorkload(
            Name: "W2 arithmetic-kernel",
            Image: image,
            LoadAddress: 0x0000,
            StartPc: start,
            SuccessTrapPc: 0x0000,                 // unused — W2 terminates by the cycle cap
            FixedCycleCap: ArithKernelCycleCap,
            ExpectedCycles: ArithKernelCycleCap);
    }
}

/// <summary>The two Z80 benchmark workloads, mirroring the 6502 W1/W2 shape:
/// <list type="bullet">
/// <item><b>Z80-W1 (ZEXDOC prefix):</b> the ZEXDOC instruction-set exerciser run to a fixed,
/// committed T-state WINDOW (NOT run-to-banner) as the "integration-realistic mixed stream" analog of
/// Klaus. Loaded from the same vector cache the test suite uses (NOT vendored — the ZEX fetch
/// pattern); absent =&gt; W1 is skipped with the fetch instruction. <see cref="BenchWorkload.UsesCpmBdos"/>
/// is true so the driver services the CP/M BDOS CALL + warm-boot sentinel.</item>
/// <item><b>Z80-W2 (arithmetic kernel):</b> a tight hand-written ADD/SUB/DJNZ loop committed as a
/// byte[] constant, run for a fixed cycle cap — the analog of the 6502 W2 kernel (a hot arithmetic +
/// taken-branch loop, the chain edge a future block-JIT stresses).</item>
/// </list>
/// Z80 cycles are <b>T-states</b>, not directly comparable to 6502 machine cycles (see the report's
/// unit label); the headline is the per-CPU before/after ratio (D4).</summary>
public static class Z80Workloads
{
    /// <summary>The committed-and-FROZEN Z80-W1 window, in T-states — a deterministic ZEXDOC PREFIX (a
    /// fixed cycle window, NOT run-to-banner). This is the M6 re-measure contract: Milestone C reuses
    /// this EXACT value so the "before" + "after" runs do byte-identical work; do NOT retune it.
    /// <para>PINNED at 2,000,000,000 after the first measured run (2026-06-15). At this window the
    /// stream is a deterministic ~2B-T-state slice of ZEXDOC's first sub-test, the
    /// <c>&lt;adc,sbc&gt; hl,&lt;bc,de,hl,sp&gt;</c> exerciser — which cycles every register pair × both
    /// ops × the full flag matrix with the test harness's CRC accumulation: a genuinely realistic
    /// mixed Z80 instruction stream (the Klaus-W1 analog). Measured note: ZEX's first sub-test does not
    /// PRINT its OK banner until ~3.5B T-states (its sub-tests are long + uneven), so this prefix runs
    /// real ZEX code WITHOUT depending on a banner — exactly the "fixed-T-state-window throughput
    /// stream" the plan (D1) specifies. Both our tiers complete it in well under a minute on the
    /// canonical host; a larger window only lengthens the run for marginal extra spread.</para></summary>
    public const long Z80W1WindowTStates = 2_000_000_000;

    /// <summary>The committed-and-FROZEN Z80-W2 cycle cap, in T-states (the measured window length).
    /// The M6 re-measure reuses this EXACT value; do NOT retune it. PINNED at 50,000,000 (2026-06-15) —
    /// it mirrors the 6502 W2 cap's order of magnitude and runs in ~2s (Tier-0) / ~0.8s (Tier-1).</summary>
    public const long Z80W2CycleCap = 50_000_000;

    public const ushort ZexTpa = 0x0100;          // .com load + entry (the CP/M TPA)

    /// <summary>Locate the ZEXDOC binary in the shared vector cache (the same scheme the test suite
    /// uses via ZexVectors — CPUEMULATOR_TESTVECTORS or ~/.cache/cpuemulator/vectors, then
    /// zex/zexdoc.com). Returns null when absent so the caller skips W1 with the fetch instruction.</summary>
    public static string? ZexdocBinaryPathOrNull()
    {
        string root = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            ".cache", "cpuemulator", "vectors");
        string path = Path.Combine(root, "zex", "zexdoc.com");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Z80-W1 — the ZEXDOC prefix. Builds a 64 KiB image: zero it, load the .com at 0x0100,
    /// seed Page Zero like CpmBdosHost (0x0000 = HALT sentinel but the driver stops on PC==0 BEFORE
    /// executing it; 0x0005 = RET so any path that does not hit the host intercept returns harmlessly).
    /// Returns null when the binary is not in the vector cache (run tools/get-zexall.ps1 or set
    /// CPUEMULATOR_TESTVECTORS); the caller skips W1 then, exactly like Workloads.KlausOrNull().</summary>
    public static BenchWorkload? Z80ZexPrefixOrNull()
    {
        string? path = ZexdocBinaryPathOrNull();
        if (path is null) return null;
        byte[] com = File.ReadAllBytes(path);

        var image = new byte[0x10000];
        // Load the .com into the TPA.
        for (int i = 0; i < com.Length && ZexTpa + i < image.Length; i++)
            image[ZexTpa + i] = com[i];
        // Seed Page Zero (mirrors CpmBdosHost): a warm-boot sentinel byte at 0x0000 (PC reaching here
        // terminates BEFORE it executes) and a RET at 0x0005 (a harmless real BDOS target for any path
        // that does not hit the host intercept).
        image[0x0000] = 0x76;  // HALT byte at 0 — never executed (the driver stops on PC==0 first)
        image[0x0005] = 0xC9;  // RET

        return new BenchWorkload(
            Name: "Z80-W1 ZEXDOC-prefix",
            Image: image,
            LoadAddress: 0x0000,
            StartPc: ZexTpa,
            SuccessTrapPc: 0x0000,                 // unused — W1 terminates on the cycle WINDOW (D1)
            FixedCycleCap: Z80W1WindowTStates,
            ExpectedCycles: Z80W1WindowTStates,
            Architecture: "z80",
            UsesCpmBdos: true);
    }

    /// <summary>Z80-W2 — the arithmetic kernel. A 64 KiB image whose code at 0x0100 is a tight
    /// ADD/SUB + DJNZ inner loop with a JP back-edge so it spins forever (the cap terminates it). It
    /// touches only A + B (no memory store, so no SMC), exercises the carry/flag ALU path, and loops
    /// via DJNZ — the hot taken branch a future block-JIT's chaining stresses. ExpectedCycles is the
    /// cap (a subject that diverges typically over/undershoots the cap boundary, caught by an
    /// adapter's verification).</summary>
    public static BenchWorkload Z80ArithmeticKernel()
    {
        var image = new byte[0x10000];

        // ── The kernel (assembled by hand below) at 0x0100 ───────────────────────────────────────
        //   Verified opcode bytes against a Z80 reference:
        //   LD A,n = 3E n; LD B,n = 06 n; ADD A,n = C6 n; SUB n = D6 n; INC A = 3C; DEC A = 3D;
        //   DJNZ rel = 10 rel (rel measured from the byte AFTER the instruction); JP nn = C3 lo hi.
        //
        //   0x0100  LD A,$00     3E 00      A = 0
        //   0x0102  LD B,$80     06 80      B = 0x80 (inner counter — 128 iterations)
        //   inner (0x0104):
        //   0x0104  ADD A,$25    C6 25      A += 0x25      (carry/flag ALU path)
        //   0x0106  SUB $11      D6 11      A -= 0x11
        //   0x0108  INC A        3C         A++
        //   0x0109  DEC A        3D         A--
        //   0x010A  DJNZ inner   10 F8      dec B; jump to 0x0104 if B!=0  (0x0104-0x010C = -8 = 0xF8)
        //   0x010C  JP $0100     C3 00 01   restart the whole kernel (keeps it running forever)
        ushort pc = 0x0100;
        ushort start = pc;
        void Emit(params byte[] bytes) { foreach (byte b in bytes) image[pc++] = b; }

        Emit(0x3E, 0x00);                       // LD A,$00
        Emit(0x06, 0x80);                       // LD B,$80
        ushort inner = pc;                      // 0x0104
        Emit(0xC6, 0x25);                       // ADD A,$25
        Emit(0xD6, 0x11);                       // SUB $11
        Emit(0x3C);                             // INC A
        Emit(0x3D);                             // DEC A
        Emit(0x10, (byte)(inner - (pc + 2)));   // DJNZ inner  (rel from pc+2 = 0x010C back to 0x0104)
        Emit(0xC3, (byte)(start & 0xFF), (byte)(start >> 8));  // JP start

        return new BenchWorkload(
            Name: "Z80-W2 arithmetic-kernel",
            Image: image,
            LoadAddress: 0x0000,
            StartPc: start,
            SuccessTrapPc: 0x0000,                 // unused — W2 terminates by the cycle cap
            FixedCycleCap: Z80W2CycleCap,
            ExpectedCycles: Z80W2CycleCap,
            Architecture: "z80",
            UsesCpmBdos: false);
    }
}
