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
