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

    /// <summary>The W3 Sieve kernel's fixed cycle cap (the measured window length, in emulated cycles).
    /// PINNED at 50,000,000 — mirrors <see cref="ArithKernelCycleCap"/>'s order of magnitude so the W3
    /// window is the same size as W2's. The M6 re-measure reuses this EXACT value; do NOT retune it.</summary>
    public const long SieveCycleCap = 50_000_000;

    /// <summary>W3 — the Sieve-of-Eratosthenes compute kernel (a Dhrystone-CLASS integer/branch/memory
    /// benchmark — the classic BYTE-magazine Jan-1983 Sieve; NOT literal Dhrystone). A 64 KiB image whose
    /// code at $0200 runs the canonical 8190-flag Sieve (one full pass yields 1899 primes — the canonical
    /// answer, VERIFIED against the merged Mos6502Cpu) and then JMPs back to the start so it spins forever
    /// (the cap terminates it, like W2). Integer- + branch- + memory-access-heavy: 16-bit zero-page index
    /// arithmetic (the 6502 has no 16-bit registers), an indexed flag-clear inner loop, and a taken
    /// back-edge per composite — the hot chain edge a future block-JIT stresses. ExpectedCycles is the cap.
    ///
    /// SIZE = 8190 (FROZEN), flag array $1000..$2FFD (one byte/flag), prime count = 1899 (FROZEN, verified).
    /// Zero page: $10/$11 = i, $12/$13 = prime, $14/$15 = kptr (= $1000 + k), $16/$17 = count,
    ///            $18/$19 = scratch pointer (flags[i] read + the clear loop).
    /// <code>
    ///         CLD                          D8
    ///         ; clear flags $1000..$2FFF (32 pages = 8192 bytes; covers SIZE=8190) to 1 (true)
    ///         LDA #$00 ; STA $18           A9 00 85 18    ptr lo = $00
    ///         LDA #$10 ; STA $19           A9 10 85 19    ptr hi = $10  -> ptr = $1000
    ///         LDX #$20                     A2 20          32 pages
    ///         LDA #$01                     A9 01          flag = true
    ///         LDY #$00                     A0 00
    /// clr:    STA ($18),Y                  91 18
    ///         INY                          C8
    ///         BNE clr                      D0 ..          256 bytes per page
    ///         INC $19                      E6 19          next page
    ///         DEX                          CA
    ///         BNE clr                      D0 ..
    ///         LDA #$00                     A9 00
    ///         STA $10 ; STA $11            85 10 85 11    i = 0
    ///         STA $16 ; STA $17            85 16 85 17    count = 0
    /// forI:   ; flags[i]? -> iptr = $1000 + i in $18/$19
    ///         CLC                          18
    ///         LDA $10 ; ADC #$00 ; STA $18 A5 10 69 00 85 18
    ///         LDA $11 ; ADC #$10 ; STA $19 A5 11 69 10 85 19
    ///         LDY #$00                     A0 00
    ///         LDA ($18),Y                  B1 18          A = flags[i]
    ///         BEQ notprime                 F0 ..          composite -> skip
    ///         ; prime = i + i + 3
    ///         CLC                          18
    ///         LDA $10 ; ADC $10 ; STA $12  A5 10 65 10 85 12   prime = 2*i (lo)
    ///         LDA $11 ; ADC $11 ; STA $13  A5 11 65 11 85 13   prime = 2*i (hi)
    ///         CLC                          18
    ///         LDA $12 ; ADC #$03 ; STA $12 A5 12 69 03 85 12   prime += 3 (lo)
    ///         LDA $13 ; ADC #$00 ; STA $13 A5 13 69 00 85 13   prime += 3 (hi)
    ///         ; k = i + prime ; kptr = k + $1000
    ///         CLC                          18
    ///         LDA $10 ; ADC $12 ; STA $14  A5 10 65 12 85 14   k = i + prime (lo)
    ///         LDA $11 ; ADC $13 ; STA $15  A5 11 65 13 85 15   k = i + prime (hi)
    ///         CLC                          18
    ///         LDA $14 ; ADC #$00 ; STA $14 A5 14 69 00 85 14   kptr = k + $1000 (lo)
    ///         LDA $15 ; ADC #$10 ; STA $15 A5 15 69 10 85 15   kptr = k + $1000 (hi)
    /// inner:  ; while kptr &lt; FEND ($2FFE = $1000 + SIZE)
    ///         LDA $15 ; CMP #$2F           A5 15 C9 2F    compare kptr-hi vs FEND-hi
    ///         BCC doclear                  90 ..          kptr-hi &lt; hi -> in range
    ///         BNE endinner                 D0 ..          kptr-hi &gt; hi -> done
    ///         LDA $14 ; CMP #$FE           A5 14 C9 FE    compare kptr-lo vs FEND-lo
    ///         BCS endinner                 B0 ..          kptr-lo &gt;= lo -> done
    /// doclear:LDA #$00 ; LDY #$00          A9 00 A0 00
    ///         STA ($14),Y                  91 14          flags[k] = 0 (false)
    ///         CLC                          18
    ///         LDA $14 ; ADC $12 ; STA $14  A5 14 65 12 85 14   kptr += prime (lo)
    ///         LDA $15 ; ADC $13 ; STA $15  A5 15 65 13 85 15   kptr += prime (hi)
    ///         JMP inner                    4C .. ..       (JMP — the inner span exceeds branch range)
    /// endinner:
    ///         INC $16 ; BNE cntdone        E6 16 D0 02    count++ (lo)
    ///         INC $17                      E6 17                 (hi carry)
    /// cntdone:
    /// notprime:
    ///         INC $10 ; BNE chkI           E6 10 D0 02    i++ (lo)
    ///         INC $11                      E6 11                 (hi carry)
    /// chkI:   ; if i &gt;= SIZE -> wrap ; else JMP forI
    ///         LDA $11 ; CMP #$1F           A5 11 C9 1F    compare i-hi vs SIZE-hi
    ///         BCC contI                    90 ..          i-hi &lt; hi -> still in range
    ///         BNE wrap                     D0 ..          i-hi &gt; hi -> done this pass
    ///         LDA $10 ; CMP #$FE           A5 10 C9 FE    compare i-lo vs SIZE-lo
    ///         BCS wrap                     B0 ..          i-lo &gt;= lo -> done this pass
    /// contI:  JMP forI                     4C .. ..
    /// wrap:   JMP start                    4C .. ..       restart the whole sieve forever
    /// </code></summary>
    public static BenchWorkload SieveKernel()
    {
        var image = new byte[0x10000];

        const ushort FlagBase = 0x1000;            // flag array base
        const ushort Size = 8190;                  // FROZEN — the canonical BYTE Jan-1983 Sieve size
        const ushort FlagEnd = FlagBase + Size;    // $2FFE — one past the last valid flag index

        ushort pc = 0x0200;
        ushort start = pc;
        void Emit(params byte[] bytes) { foreach (byte b in bytes) image[pc++] = b; }
        // Defensive guard on every hand-assembled 8-bit signed relative branch: compute the displacement
        // d = target - (opcodeAddr + 2), assert it fits a signed byte (so a future edit that pushes a
        // branch out of range FAILS LOUDLY rather than silently wrapping into a plausible-but-wrong image),
        // then emit (byte)(sbyte)d. For the CURRENT (verified-1899-primes) kernel none of these can fire.
        byte Rel8(int d, string name)
        {
            if ((sbyte)d != d)
                throw new InvalidOperationException($"6502 sieve {name} displacement {d} out of signed-byte range");
            return (byte)(sbyte)d;
        }

        Emit(0xD8);                                            // CLD

        // ── clear flags $1000..$2FFF (32 pages = 8192 bytes; covers SIZE=8190) to 1 (true) ──
        Emit(0xA9, (byte)(FlagBase & 0xFF)); Emit(0x85, 0x18);  // LDA #lo($1000) ; STA $18
        Emit(0xA9, (byte)(FlagBase >> 8));   Emit(0x85, 0x19);  // LDA #hi($1000) ; STA $19
        Emit(0xA2, 0x20);                                       // LDX #$20  (32 pages)
        Emit(0xA9, 0x01);                                       // LDA #$01  (flag = true)
        Emit(0xA0, 0x00);                                       // LDY #$00
        ushort clr = pc;
        Emit(0x91, 0x18);                                       // STA ($18),Y
        Emit(0xC8);                                             // INY
        Emit(0xD0, Rel8(clr - (pc + 2), "BNE clr (inner)"));    // BNE clr
        Emit(0xE6, 0x19);                                       // INC $19  (next page)
        Emit(0xCA);                                             // DEX
        Emit(0xD0, Rel8(clr - (pc + 2), "BNE clr (outer)"));    // BNE clr
        Emit(0xA9, 0x00);                                       // LDA #$00
        Emit(0x85, 0x10); Emit(0x85, 0x11);                     // i = 0
        Emit(0x85, 0x16); Emit(0x85, 0x17);                     // count = 0

        // ── for i in 0..SIZE-1 ──
        ushort forI = pc;
        Emit(0x18);                                             // CLC
        Emit(0xA5, 0x10); Emit(0x69, (byte)(FlagBase & 0xFF)); Emit(0x85, 0x18);  // iptr lo = i.lo + $00
        Emit(0xA5, 0x11); Emit(0x69, (byte)(FlagBase >> 8));   Emit(0x85, 0x19);  // iptr hi = i.hi + $10
        Emit(0xA0, 0x00);                                       // LDY #$00
        Emit(0xB1, 0x18);                                       // LDA ($18),Y  -> flags[i]
        int beqNotPrimeAt = pc; Emit(0xF0, 0x00);               // BEQ notprime (patched once notprime is known)

        // prime = i + i + 3
        Emit(0x18);                                             // CLC
        Emit(0xA5, 0x10); Emit(0x65, 0x10); Emit(0x85, 0x12);   // prime.lo = 2*i.lo
        Emit(0xA5, 0x11); Emit(0x65, 0x11); Emit(0x85, 0x13);   // prime.hi = 2*i.hi + carry
        Emit(0x18);                                             // CLC
        Emit(0xA5, 0x12); Emit(0x69, 0x03); Emit(0x85, 0x12);   // prime.lo += 3
        Emit(0xA5, 0x13); Emit(0x69, 0x00); Emit(0x85, 0x13);   // prime.hi += carry
        // k = i + prime ; kptr = k + $1000
        Emit(0x18);                                             // CLC
        Emit(0xA5, 0x10); Emit(0x65, 0x12); Emit(0x85, 0x14);   // k.lo = i.lo + prime.lo
        Emit(0xA5, 0x11); Emit(0x65, 0x13); Emit(0x85, 0x15);   // k.hi = i.hi + prime.hi + carry
        Emit(0x18);                                             // CLC
        Emit(0xA5, 0x14); Emit(0x69, (byte)(FlagBase & 0xFF)); Emit(0x85, 0x14);  // kptr.lo = k.lo + $00
        Emit(0xA5, 0x15); Emit(0x69, (byte)(FlagBase >> 8));   Emit(0x85, 0x15);  // kptr.hi = k.hi + $10

        // while kptr < FEND
        ushort inner = pc;
        Emit(0xA5, 0x15); Emit(0xC9, (byte)(FlagEnd >> 8));     // LDA $15 ; CMP #hi(FEND)
        int bccDoClearAt = pc; Emit(0x90, 0x00);                // BCC doclear (patched)
        int bneEndInnerAt = pc; Emit(0xD0, 0x00);               // BNE endinner (patched)
        Emit(0xA5, 0x14); Emit(0xC9, (byte)(FlagEnd & 0xFF));   // LDA $14 ; CMP #lo(FEND)
        int bcsEndInnerAt = pc; Emit(0xB0, 0x00);               // BCS endinner (patched)
        ushort doclear = pc;
        image[bccDoClearAt + 1] = Rel8(doclear - (bccDoClearAt + 2), "BCC doclear");   // patch BCC doclear
        Emit(0xA9, 0x00); Emit(0xA0, 0x00); Emit(0x91, 0x14);   // LDA #0 ; LDY #0 ; STA ($14),Y  flags[k]=0
        Emit(0x18);                                             // CLC
        Emit(0xA5, 0x14); Emit(0x65, 0x12); Emit(0x85, 0x14);   // kptr.lo += prime.lo
        Emit(0xA5, 0x15); Emit(0x65, 0x13); Emit(0x85, 0x15);   // kptr.hi += prime.hi + carry
        Emit(0x4C, (byte)(inner & 0xFF), (byte)(inner >> 8));   // JMP inner
        ushort endinner = pc;
        image[bneEndInnerAt + 1] = Rel8(endinner - (bneEndInnerAt + 2), "BNE endinner");  // patch BNE endinner
        image[bcsEndInnerAt + 1] = Rel8(endinner - (bcsEndInnerAt + 2), "BCS endinner");  // patch BCS endinner
        Emit(0xE6, 0x16);                                       // INC $16  (count.lo)
        Emit(0xD0, 0x02);                                       // BNE cntdone — fixed 2-byte skip over INC $17
        Emit(0xE6, 0x17);                                       // INC $17  (count.hi)
        // cntdone / notprime fall through together
        ushort notprime = pc;
        image[beqNotPrimeAt + 1] = Rel8(notprime - (beqNotPrimeAt + 2), "BEQ notprime");  // patch BEQ notprime
        Emit(0xE6, 0x10);                                       // INC $10  (i.lo)
        Emit(0xD0, 0x02);                                       // BNE chkI — fixed 2-byte skip over INC $11
        Emit(0xE6, 0x11);                                       // INC $11  (i.hi)
        // chkI: if i >= SIZE -> wrap ; else JMP forI
        Emit(0xA5, 0x11); Emit(0xC9, (byte)(Size >> 8));        // LDA $11 ; CMP #hi(SIZE)
        int bccContIAt = pc; Emit(0x90, 0x00);                  // BCC contI (patched)
        int bneWrapAt = pc; Emit(0xD0, 0x00);                   // BNE wrap (patched)
        Emit(0xA5, 0x10); Emit(0xC9, (byte)(Size & 0xFF));      // LDA $10 ; CMP #lo(SIZE)
        int bcsWrapAt = pc; Emit(0xB0, 0x00);                   // BCS wrap (patched)
        ushort contI = pc;
        image[bccContIAt + 1] = Rel8(contI - (bccContIAt + 2), "BCC contI");   // patch BCC contI
        Emit(0x4C, (byte)(forI & 0xFF), (byte)(forI >> 8));     // JMP forI
        ushort wrap = pc;
        image[bneWrapAt + 1] = Rel8(wrap - (bneWrapAt + 2), "BNE wrap");      // patch BNE wrap
        image[bcsWrapAt + 1] = Rel8(wrap - (bcsWrapAt + 2), "BCS wrap");      // patch BCS wrap
        Emit(0x4C, (byte)(start & 0xFF), (byte)(start >> 8));   // JMP start  (loop the whole sieve forever)

        // Reset/IRQ/NMI vectors (a fresh CPU that resets lands at the kernel; the bench sets PC explicitly,
        // but valid vectors keep the image well-formed — mirrors the W2 kernel).
        image[0xFFFC] = (byte)(start & 0xFF); image[0xFFFD] = (byte)(start >> 8);
        image[0xFFFA] = (byte)(start & 0xFF); image[0xFFFB] = (byte)(start >> 8);
        image[0xFFFE] = (byte)(start & 0xFF); image[0xFFFF] = (byte)(start >> 8);

        return new BenchWorkload(
            Name: "W3 sieve-kernel",
            Image: image,
            LoadAddress: 0x0000,
            StartPc: start,
            SuccessTrapPc: 0x0000,                 // unused — W3 terminates by the cycle cap
            FixedCycleCap: SieveCycleCap,
            ExpectedCycles: SieveCycleCap);
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

    /// <summary>The committed-and-FROZEN Z80-W3 (Sieve) cycle cap, in T-states (the measured window
    /// length). PINNED at 50,000,000 — mirrors the 6502/Z80 W2 cap order of magnitude. The M6 re-measure
    /// reuses this EXACT value; do NOT retune it.</summary>
    public const long Z80SieveCycleCap = 50_000_000;

    /// <summary>Z80-W3 — the Sieve-of-Eratosthenes compute kernel (a Dhrystone-CLASS integer/branch/memory
    /// benchmark — the classic BYTE-magazine Jan-1983 Sieve; NOT literal Dhrystone). A 64 KiB image whose
    /// code at 0x0100 runs the canonical 8190-flag Sieve (one full pass yields 1899 primes — the canonical
    /// answer, VERIFIED against the merged Z80Cpu) then JPs back to the start so it spins forever (the cap
    /// terminates it, like Z80-W2). Integer- + branch- + memory-heavy: 16-bit HL/DE/BC index arithmetic,
    /// an indexed flag-clear inner loop, and a taken back-edge per composite — the hot chain edge.
    ///
    /// SIZE = 8190 (FROZEN), flag array 0x4000..0x5FFD (one byte/flag), prime count = 1899 (FROZEN, verified).
    /// Word vars in RAM clear of code + flags: i @ 0x6000, prime @ 0x6002, count @ 0x6004.
    ///   Verified opcode bytes against a Z80 reference:
    ///   LD HL,nn = 21 lo hi; LD BC,nn = 01 lo hi; LD DE,nn = 11 lo hi; LD HL,(nn) = 2A lo hi;
    ///   LD (nn),HL = 22 lo hi; LD DE,(nn) = ED 5B lo hi; ADD HL,DE = 19; ADD HL,HL = 29; INC HL = 23;
    ///   DEC BC = 0B; LD A,B = 78; OR C = B1; OR A = B7; LD (HL),n = 36 n; LD A,(HL) = 7E;
    ///   PUSH HL = E5; POP HL = E1; SBC HL,BC = ED 42; JR cc,e = 20/28/30/38 e; JR e = 18 e;
    ///   JP cc,nn = C2/CA/D2/DA lo hi; JP nn = C3 lo hi.
    /// <code>
    /// start (0x0100):
    ///   ; clear flags 0x4000..0x5FFF (8192 bytes; covers SIZE=8190) to 1 (true)
    ///   LD HL,$4000             21 00 40
    ///   LD BC,8192              01 00 20
    /// clr:
    ///   LD (HL),1               36 01
    ///   INC HL                  23
    ///   DEC BC                  0B
    ///   LD A,B ; OR C           78 B1        BC == 0 ?
    ///   JR NZ,clr               20 ..
    ///   LD HL,0                 21 00 00
    ///   LD ($6000),HL           22 00 60     i = 0
    ///   LD ($6004),HL           22 04 60     count = 0
    /// forI:
    ///   LD HL,($6000)           2A 00 60     HL = i
    ///   LD DE,$4000             11 00 40
    ///   ADD HL,DE               19           HL = $4000 + i
    ///   LD A,(HL)               7E           A = flags[i]
    ///   OR A                    B7           set Z
    ///   JP Z,notprime           CA .. ..     composite -> skip
    ///   ; prime = i + i + 3
    ///   LD HL,($6000)           2A 00 60     HL = i
    ///   ADD HL,HL               29           HL = 2*i
    ///   LD DE,3 ; ADD HL,DE     11 03 00 19  HL = 2*i + 3
    ///   LD ($6002),HL           22 02 60     prime = HL
    ///   ; k = i + prime ; kptr = k + $4000 ; DE := prime (kept for the inner loop)
    ///   LD DE,($6002)           ED 5B 02 60  DE = prime
    ///   LD HL,($6000)           2A 00 60     HL = i
    ///   ADD HL,DE               19           HL = i + prime = k
    ///   LD DE,$4000 ; ADD HL,DE 11 00 40 19  HL = k + $4000 = kptr
    ///   LD DE,($6002)           ED 5B 02 60  DE = prime (for kptr += prime)
    /// inner:
    ///   ; while kptr &lt; FEND ($5FFE = $4000 + SIZE)
    ///   PUSH HL                 E5           save kptr
    ///   LD BC,$5FFE             01 FE 5F
    ///   OR A                    B7           clear carry
    ///   SBC HL,BC               ED 42        HL = kptr - FEND ; C=1 if kptr &lt; FEND
    ///   POP HL                  E1           restore kptr
    ///   JR NC,endinner          30 ..        kptr &gt;= FEND -> done
    ///   LD (HL),0               36 00        flags[k] = 0 (false)
    ///   ADD HL,DE               19           kptr += prime
    ///   JR inner                18 ..
    /// endinner:
    ///   LD HL,($6004) ; INC HL ; LD ($6004),HL   2A 04 60 23 22 04 60   count++
    /// notprime:
    ///   LD HL,($6000) ; INC HL ; LD ($6000),HL   2A 00 60 23 22 00 60   i++
    ///   ; compare i &lt; SIZE : (HL holds new i) BC=SIZE, OR A, SBC HL,BC -> C=1 if i &lt; SIZE
    ///   LD BC,8190              01 FE 1F
    ///   OR A                    B7
    ///   SBC HL,BC               ED 42
    ///   JP C,forI               DA .. ..     i &lt; SIZE -> loop next i
    ///   JP start                C3 00 01     restart the whole sieve forever
    /// </code></summary>
    public static BenchWorkload Z80SieveKernel()
    {
        var image = new byte[0x10000];

        const ushort FlagBase = 0x4000;            // flag array base (clear of code at 0x0100)
        const ushort Size = 8190;                  // FROZEN — the canonical BYTE Jan-1983 Sieve size
        const ushort FlagEnd = FlagBase + Size;    // 0x5FFE — one past the last valid flag index
        const ushort IVar = 0x6000, PrimeVar = 0x6002, CountVar = 0x6004;  // word vars in RAM

        ushort pc = 0x0100;
        ushort start = pc;
        void Emit(params byte[] bytes) { foreach (byte b in bytes) image[pc++] = b; }
        // Defensive guard on every hand-assembled 8-bit signed JR displacement: compute d, assert it fits
        // a signed byte (so a future edit that pushes a JR out of range FAILS LOUDLY rather than silently
        // wrapping into a plausible-but-wrong image), then emit (byte)(sbyte)d. (Absolute JP / JP cc are
        // 16-bit and need no guard.) For the CURRENT (verified-1899-primes) kernel none of these can fire.
        byte Rel8(int d, string name)
        {
            if ((sbyte)d != d)
                throw new InvalidOperationException($"Z80 sieve {name} displacement {d} out of signed-byte range");
            return (byte)(sbyte)d;
        }

        // ── clear flags 0x4000..0x5FFF (8192 bytes; covers SIZE=8190) to 1 (true) ──
        Emit(0x21, (byte)(FlagBase & 0xFF), (byte)(FlagBase >> 8));   // LD HL,$4000
        Emit(0x01, (byte)(8192 & 0xFF), (byte)(8192 >> 8));          // LD BC,8192
        ushort clr = pc;
        Emit(0x36, 0x01);                          // LD (HL),1
        Emit(0x23);                                // INC HL
        Emit(0x0B);                                // DEC BC
        Emit(0x78); Emit(0xB1);                    // LD A,B ; OR C  (BC == 0 ?)
        Emit(0x20, Rel8(clr - (pc + 2), "JR NZ,clr"));  // JR NZ,clr
        Emit(0x21, 0x00, 0x00);                    // LD HL,0
        Emit(0x22, (byte)(IVar & 0xFF), (byte)(IVar >> 8));        // LD (i),HL = 0
        Emit(0x22, (byte)(CountVar & 0xFF), (byte)(CountVar >> 8)); // LD (count),HL = 0

        // ── for i in 0..SIZE-1 ──
        ushort forI = pc;
        Emit(0x2A, (byte)(IVar & 0xFF), (byte)(IVar >> 8));        // LD HL,(i)
        Emit(0x11, (byte)(FlagBase & 0xFF), (byte)(FlagBase >> 8)); // LD DE,$4000
        Emit(0x19);                                // ADD HL,DE  -> $4000 + i
        Emit(0x7E);                                // LD A,(HL)  -> flags[i]
        Emit(0xB7);                                // OR A  (set Z)
        int jpNotPrimeAt = pc; Emit(0xCA, 0x00, 0x00);  // JP Z,notprime (patched)

        // prime = i + i + 3
        Emit(0x2A, (byte)(IVar & 0xFF), (byte)(IVar >> 8));        // LD HL,(i)
        Emit(0x29);                                // ADD HL,HL  -> 2*i
        Emit(0x11, 0x03, 0x00); Emit(0x19);        // LD DE,3 ; ADD HL,DE  -> 2*i+3
        Emit(0x22, (byte)(PrimeVar & 0xFF), (byte)(PrimeVar >> 8)); // LD (prime),HL
        // k = i + prime ; kptr = k + $4000 ; DE := prime (kept for the inner loop)
        Emit(0xED, 0x5B, (byte)(PrimeVar & 0xFF), (byte)(PrimeVar >> 8));  // LD DE,(prime)
        Emit(0x2A, (byte)(IVar & 0xFF), (byte)(IVar >> 8));        // LD HL,(i)
        Emit(0x19);                                // ADD HL,DE  -> k = i + prime
        Emit(0x11, (byte)(FlagBase & 0xFF), (byte)(FlagBase >> 8)); // LD DE,$4000
        Emit(0x19);                                // ADD HL,DE  -> kptr = k + $4000
        Emit(0xED, 0x5B, (byte)(PrimeVar & 0xFF), (byte)(PrimeVar >> 8));  // LD DE,(prime)  (for kptr += prime)

        // while kptr < FEND
        ushort inner = pc;
        Emit(0xE5);                                // PUSH HL  (save kptr)
        Emit(0x01, (byte)(FlagEnd & 0xFF), (byte)(FlagEnd >> 8));  // LD BC,$5FFE
        Emit(0xB7);                                // OR A  (clear carry)
        Emit(0xED, 0x42);                          // SBC HL,BC  -> kptr - FEND ; C=1 if kptr < FEND
        Emit(0xE1);                                // POP HL  (restore kptr)
        int jrEndInnerAt = pc; Emit(0x30, 0x00);   // JR NC,endinner (patched)
        Emit(0x36, 0x00);                          // LD (HL),0  flags[k] = 0
        Emit(0x19);                                // ADD HL,DE  kptr += prime
        Emit(0x18, Rel8(inner - (pc + 2), "JR inner"));  // JR inner
        ushort endinner = pc;
        image[jrEndInnerAt + 1] = Rel8(endinner - (jrEndInnerAt + 2), "JR NC,endinner");  // patch JR NC,endinner
        Emit(0x2A, (byte)(CountVar & 0xFF), (byte)(CountVar >> 8));  // LD HL,(count)
        Emit(0x23);                                // INC HL
        Emit(0x22, (byte)(CountVar & 0xFF), (byte)(CountVar >> 8));  // LD (count),HL

        ushort notprime = pc;
        image[jpNotPrimeAt + 1] = (byte)(notprime & 0xFF);   // patch JP Z,notprime (abs)
        image[jpNotPrimeAt + 2] = (byte)(notprime >> 8);
        Emit(0x2A, (byte)(IVar & 0xFF), (byte)(IVar >> 8));        // LD HL,(i)
        Emit(0x23);                                // INC HL
        Emit(0x22, (byte)(IVar & 0xFF), (byte)(IVar >> 8));        // LD (i),HL
        // compare i < SIZE : HL holds new i ; BC = SIZE ; OR A ; SBC HL,BC -> C=1 if i < SIZE
        Emit(0x01, (byte)(Size & 0xFF), (byte)(Size >> 8));        // LD BC,8190
        Emit(0xB7);                                // OR A  (clear carry)
        Emit(0xED, 0x42);                          // SBC HL,BC
        Emit(0xDA, (byte)(forI & 0xFF), (byte)(forI >> 8));        // JP C,forI  (i < SIZE -> loop)
        Emit(0xC3, (byte)(start & 0xFF), (byte)(start >> 8));      // JP start  (restart forever)

        return new BenchWorkload(
            Name: "Z80-W3 sieve-kernel",
            Image: image,
            LoadAddress: 0x0000,
            StartPc: start,
            SuccessTrapPc: 0x0000,                 // unused — W3 terminates by the cycle cap
            FixedCycleCap: Z80SieveCycleCap,
            ExpectedCycles: Z80SieveCycleCap,
            Architecture: "z80",
            UsesCpmBdos: false);
    }
}

/// <summary>The two 68000 benchmark workloads (Milestone B), mirroring the 6502/Z80 W1/W2 shape with
/// one structural difference: the 68000 board is 16 MiB (R4), so each workload carries a SMALL image (a
/// few words) copied at <see cref="BenchWorkload.LoadAddress"/> by <c>M68000TierDriver</c> — NOT a 16
/// MiB byte[]. The 68000 is big-endian + word-decoded, so each opword is two bytes, high-byte first.
/// <list type="bullet">
/// <item><b>m68k-W2 (arithmetic/branch kernel):</b> a tight hand-written ALU + DBcc-style branch loop
/// committed as a byte[], run to a FROZEN cycle cap (with a FROZEN instruction cap for the
/// cycle-axis-independent instructions/sec window). The taken back-edge is the hot chain edge a future
/// block-JIT stresses (the 6502/Z80 W2 rationale).</item>
/// <item><b>m68k-W1 (deterministic mixed-instruction stream):</b> Option A (the plan's default) — a
/// larger hand-written synthetic MIXED kernel (MOVE variants, ALU reg/EA, shift, Bcc/DBcc, JSR/RTS,
/// LINK/UNLK), run to a FROZEN instruction cap. Dependency-free + deterministic, so it ALWAYS runs (the
/// 68000 has no in-repo Klaus/ZEX-equivalent runnable exerciser — the 680x0 SingleStep vectors are
/// per-instruction cases, not a runnable stream). Option B (a fetchable 68000 exerciser) is a future
/// enhancement (§8 Q2); the baseline ships on Option A regardless.</item>
/// </list>
/// The 68000 cycle/timing axis is PARTIAL on `main` (M4.5d-2b foundation; the 2b-continuation is
/// deferred, R5): <c>CycleCount</c> is exact for the cycle-exact families, not the whole ISA — so the
/// trustworthy headline is INSTRUCTIONS/sec (data-axis-correct on the merged M4.6 core); cycles/sec is
/// reported with the timing-axis caveat (ReportWriter, B4). These window constants are FROZEN: the M6
/// re-measure (Milestone C) reuses them byte-identically — a git diff of them must show no change.</summary>
public static class M68000Workloads
{
    public const ushort M68000LoadAddress = 0x1000;   // a low address so the ushort PC view stays exact

    /// <summary>The committed-and-FROZEN m68k-W2 cycle cap, in 68000 cycles (the cycles/sec window —
    /// caveated: the 68000 cycle axis is partial, B4). PINNED at 50,000,000 (mirrors the 6502/Z80 W2
    /// cap order of magnitude). The M6 re-measure reuses this EXACT value; do NOT retune it.</summary>
    public const long M68000W2CycleCap = 50_000_000;

    /// <summary>The committed-and-FROZEN m68k-W2 instruction cap (the cycle-axis-INDEPENDENT
    /// instructions/sec window — the 68000 baseline's trustworthy headline). PINNED at 50,000,000
    /// (the recommended start, the 6502/Z80 W2 order of magnitude). The TierRunner drives by the cycle
    /// cap; this instruction cap is recorded as the frozen window the instructions/sec metric reports
    /// over, and the M6 re-measure reuses it byte-identically; do NOT retune it.</summary>
    public const long M68000W2InstructionCap = 50_000_000;

    /// <summary>The committed-and-FROZEN m68k-W1 instruction cap (the deterministic mixed stream's
    /// frozen window). PINNED at 50,000,000. The M6 re-measure reuses it byte-identically; do NOT
    /// retune it.</summary>
    public const long M68000W1InstructionCap = 50_000_000;

    /// <summary>m68k-W2 — the arithmetic/branch kernel. A tight ALU + branch inner loop at
    /// <see cref="M68000LoadAddress"/> with a BRA back-edge so it spins forever (the cap terminates it).
    /// Assembled + verified against the merged M68000Cpu (the loop advances + D0/D1 evolve sanely).
    /// <code>
    ///   D0 = accumulator, D1 = inner counter
    ///   0x1000  MOVEQ #0,D0        7000           D0 = 0
    ///   0x1002  MOVE.W #$0100,D1   323C 0100      D1 = 256 (inner counter)
    ///   inner (0x1006):
    ///   0x1006  ADDQ.W #7,D0       5E40           D0 += 7   (ALU + flags)
    ///   0x1008  SUBQ.W #3,D0       5740           D0 -= 3
    ///   0x100A  EORI.W #$5A5A,D0   0A40 5A5A      D0 ^= 0x5A5A  (mix)
    ///   0x100E  SUBQ.W #1,D1       5341           D1--
    ///   0x1010  BNE.S inner        66F4           loop inner (the taken back-edge — the hot chain edge)
    ///   0x1012  BRA.S start        60EC           restart forever (the cap terminates)
    /// </code></summary>
    public static BenchWorkload ArithmeticKernel()
    {
        var code = new List<byte>();
        void W(ushort opword) { code.Add((byte)(opword >> 8)); code.Add((byte)(opword & 0xFF)); }

        const ushort baseAddr = M68000LoadAddress;
        int start = code.Count;                 // 0x1000
        W(0x7000);                              // MOVEQ #0,D0
        W(0x323C); W(0x0100);                   // MOVE.W #$0100,D1
        int inner = code.Count;                 // 0x1006
        W(0x5E40);                              // ADDQ.W #7,D0
        W(0x5740);                              // SUBQ.W #3,D0
        W(0x0A40); W(0x5A5A);                   // EORI.W #$5A5A,D0
        W(0x5341);                              // SUBQ.W #1,D1
        // BNE.S inner — 8-bit displacement from (this opword address + 2) to the inner label.
        int bnePc = code.Count + 2;             // the PC the 68000 uses as the branch base
        sbyte bneDisp = (sbyte)(inner - bnePc);
        W((ushort)(0x6600 | (byte)bneDisp));    // BNE.S inner
        // BRA.S start — restart the whole kernel forever (the cap terminates the run).
        int braPc = code.Count + 2;
        sbyte braDisp = (sbyte)(start - braPc);
        W((ushort)(0x6000 | (byte)braDisp));    // BRA.S start

        return new BenchWorkload(
            Name: "m68k-W2 arithmetic-kernel",
            Image: code.ToArray(),
            LoadAddress: baseAddr,
            StartPc: baseAddr,
            SuccessTrapPc: 0x0000,                 // unused — W2 terminates by the cycle cap
            FixedCycleCap: M68000W2CycleCap,
            ExpectedCycles: M68000W2CycleCap,
            Architecture: "m68000",
            UsesCpmBdos: false);
    }

    /// <summary>m68k-W1 — the deterministic MIXED-instruction kernel (Option A — dependency-free, always
    /// runs). A broader spread than W2's tight hot loop: data moves, ALU on a data register, a shift, a
    /// subroutine call/return, and a DBcc-style counted back-edge — a representative integration-realistic
    /// 68000 stream. Assembled + verified against the merged M68000Cpu (the loop advances; the subroutine
    /// returns; D-registers evolve sanely). Run to <see cref="M68000W1InstructionCap"/> via the shared cap.
    /// <code>
    ///   D0/D1/D2 scratch, A0 scratch; the inner loop spins forever (the cap terminates it).
    ///   0x1000  MOVEQ #0,D0        7000           D0 = 0
    ///   0x1002  MOVE.W #$0200,D2   343C 0200      D2 = 512 (outer counter — DBF target below)
    ///   outer (0x1006):
    ///   0x1006  MOVE.L D0,D1       2200           D1 = D0   (data move, long)
    ///   0x1008  ADDI.W #$1234,D1   0641 1234      D1 += 0x1234  (ALU immediate to Dn)
    ///   0x100C  LSL.W #3,D1        E749           D1 <<= 3      (shift)
    ///   0x100E  ADD.W D1,D0        D041           D0 += D1      (ALU reg-to-reg)
    ///   0x1010  BSR.S sub          6104           call sub (push return, the stack path)
    ///   0x1012  EORI.W #$00FF,D0   0A40 00FF      D0 ^= 0x00FF  (mix)
    ///   0x1016  DBF D2,outer       51CA FFEE      D2--; branch to outer while D2 != -1 (counted loop)
    ///   0x101A  BRA.S start        60E4           restart forever (the cap terminates)
    ///   sub (0x101C):
    ///   0x101C  ADDQ.L #1,D0       5280           D0 += 1
    ///   0x101E  RTS                4E75           return
    /// </code></summary>
    public static BenchWorkload MixedKernel()
    {
        var code = new List<byte>();
        void W(ushort opword) { code.Add((byte)(opword >> 8)); code.Add((byte)(opword & 0xFF)); }

        const ushort baseAddr = M68000LoadAddress;
        int start = code.Count;                 // 0x1000
        W(0x7000);                              // MOVEQ #0,D0
        W(0x343C); W(0x0200);                   // MOVE.W #$0200,D2
        int outer = code.Count;                 // 0x1006
        W(0x2200);                              // MOVE.L D0,D1
        W(0x0641); W(0x1234);                   // ADDI.W #$1234,D1
        W(0xE749);                              // LSL.W #3,D1
        W(0xD041);                              // ADD.W D1,D0
        // BSR.S sub — 8-bit displacement from (opword address + 2) to the sub label (filled after we know it).
        int bsrOpIndex = code.Count;            // byte index of the BSR opword
        W(0x6100);                              // BSR.S (displacement patched below)
        W(0x0A40); W(0x00FF);                   // EORI.W #$00FF,D0
        // DBF D2,outer — DBcc with cc=F (false → never terminates early; pure counted back-edge). The
        // 16-bit displacement follows the opword and is measured from (opword address + 2).
        int dbfOpAddr = code.Count;             // byte index (== address offset) of the DBF opword
        W(0x51CA);                              // DBF (DBRA) D2
        int dbfDispBase = dbfOpAddr + 2;        // the PC base for the 16-bit displacement
        short dbfDisp = (short)(outer - dbfDispBase);
        W((ushort)dbfDisp);                     // DBF 16-bit displacement word
        // BRA.S start — restart the whole kernel forever.
        int braPc = code.Count + 2;
        sbyte braDisp = (sbyte)(start - braPc);
        W((ushort)(0x6000 | (byte)braDisp));    // BRA.S start
        int sub = code.Count;                   // 0x101C
        W(0x5280);                              // ADDQ.L #1,D0
        W(0x4E75);                              // RTS

        // Patch the BSR.S displacement now that the sub label is known.
        int bsrPc = bsrOpIndex + 2;             // PC base = opword address + 2
        sbyte bsrDisp = (sbyte)(sub - bsrPc);
        code[bsrOpIndex] = 0x61;
        code[bsrOpIndex + 1] = (byte)bsrDisp;

        return new BenchWorkload(
            Name: "m68k-W1 mixed-kernel",
            Image: code.ToArray(),
            LoadAddress: baseAddr,
            StartPc: baseAddr,
            SuccessTrapPc: 0x0000,                 // unused — W1 terminates by the (instruction-bounded) cycle cap
            FixedCycleCap: M68000W2CycleCap,       // the TierRunner drives by a cycle cap; the instruction cap
            ExpectedCycles: M68000W2CycleCap,      // (M68000W1InstructionCap) is the frozen instructions/sec window
            Architecture: "m68000",
            UsesCpmBdos: false);
    }

    /// <summary>The committed-and-FROZEN m68k-W3 (Sieve) cycle cap, in 68000 cycles (the cycles/sec
    /// window — caveated: the 68000 cycle axis is partial, B4). PINNED at 50,000,000 (mirrors the
    /// 6502/Z80 W2/W3 cap order of magnitude). The M6 re-measure reuses this EXACT value; do NOT retune.</summary>
    public const long M68000SieveCycleCap = 50_000_000;

    /// <summary>The committed-and-FROZEN m68k-W3 (Sieve) instruction cap (the cycle-axis-INDEPENDENT
    /// instructions/sec window — the 68000 baseline's trustworthy headline; the 68000 leads with
    /// instructions/sec because its cycle axis is partial, M4.5d-2 gating). PINNED at 50,000,000
    /// (mirrors <see cref="M68000W1InstructionCap"/>/<see cref="M68000W2InstructionCap"/>). The TierRunner
    /// drives by the cycle cap; this instruction cap is the frozen window the instructions/sec metric
    /// reports over. The M6 re-measure reuses it byte-identically; do NOT retune it.</summary>
    public const long M68000SieveInstructionCap = 50_000_000;

    /// <summary>m68k-W3 — the Sieve-of-Eratosthenes compute kernel (a Dhrystone-CLASS integer/branch/memory
    /// benchmark — the classic BYTE-magazine Jan-1983 Sieve; NOT literal Dhrystone). A small image copied at
    /// <see cref="M68000LoadAddress"/> (0x1000) that runs the canonical 8190-flag Sieve (one full pass yields
    /// 1899 primes — the canonical answer, VERIFIED against the merged M68000Cpu) then BRAs back to the start
    /// so it spins forever (the cap terminates it, like m68k-W2). The flag array lives at 0x2000 (clear of the
    /// code at 0x1000 and the SSP near 0x00FFFC), well within the 16 MiB board. Integer- + branch- +
    /// memory-heavy: indexed flag access via <c>(A0,Dn.W)</c>, an inner clear loop, and a taken back-edge per
    /// composite — the hot chain edge a future 68000 hot-op emit subtracts from. Big-endian, word-decoded
    /// (each opword is two bytes, high byte first — the <see cref="ArithmeticKernel"/> W(ushort) idiom).
    ///
    /// SIZE = 8190 (FROZEN), flag array 0x2000..0x3FFD (one byte/flag), prime count = 1899 (FROZEN, verified).
    ///   D0 = i, D1 = prime, D2 = k, D3 = count, D7 = 0 (clear-source) ; A0 = flag base (0x2000).
    /// <code>
    /// start (0x1000):
    ///   LEA ($2000).W,A0        41F8 2000     A0 = flag base
    ///   ; clear flags 0x2000..(0x2000+8190) to 1 (true): D6 = 8189 (DBF count-1), MOVE.B #1,(A0)+ loop
    ///   LEA ($2000).W,A1        43F8 2000     A1 = running clear pointer
    ///   MOVE.W #8189,D6         3C3C 1FFD     D6 = SIZE-1 (DBF terminates at -1 → SIZE iterations)
    /// clr:
    ///   MOVE.B #1,(A1)+         12FC 0001     flags[*] = 1 (true)
    ///   DBF D6,clr              51CE ....     loop SIZE times
    ///   MOVEQ #0,D0             7000          i = 0
    ///   MOVEQ #0,D3             7600          count = 0
    ///   MOVEQ #0,D7             7E00          D7 = 0 (the clear source for flags[k])
    /// forI:
    ///   MOVE.B (A0,D0.W),D6     1C30 0000     D6 = flags[i] (sets Z)
    ///   BEQ.W notprime          6700 ....     composite -> skip
    ///   ; prime = i + i + 3
    ///   MOVE.W D0,D1            3200          D1 = i
    ///   ADD.W D1,D1            D241          D1 = 2*i
    ///   ADDQ.W #3,D1           5641          D1 = 2*i + 3 = prime
    ///   ; k = i + prime
    ///   MOVE.W D0,D2           3400          D2 = i
    ///   ADD.W D1,D2           D441          D2 = i + prime = k
    /// inner:
    ///   ; while k &lt; SIZE
    ///   CMPI.W #8190,D2        0C42 1FFE     D2 - SIZE
    ///   BCC.W endinner         6400 ....     k &gt;= SIZE (unsigned) -> done
    ///   CLR.B (A0,D2.W)        4230 2000     flags[k] = 0 (false)
    ///   ADD.W D1,D2           D441          k += prime
    ///   BRA.W inner            6000 ....
    /// endinner:
    ///   ADDQ.W #1,D3          5243          count++
    /// notprime:
    ///   ADDQ.W #1,D0          5240          i++
    ///   CMPI.W #8190,D0       0C40 1FFE     i - SIZE
    ///   BCS.W forI            6500 ....     i &lt; SIZE (unsigned) -> loop next i
    ///   BRA.W start           6000 ....     restart the whole sieve forever
    /// </code></summary>
    public static BenchWorkload SieveKernel()
    {
        var code = new List<byte>();
        void W(ushort opword) { code.Add((byte)(opword >> 8)); code.Add((byte)(opword & 0xFF)); }
        // Patch a previously-emitted 16-bit displacement word (BSR/Bcc.W/BRA.W) at byte index `at` to the
        // 16-bit signed displacement from (the opword address + 2) to the byte offset `target`. All sieve
        // branches are .W (16-bit), so they cannot overflow for this image size — but guard the displacement
        // against the signed-short range anyway, so a future edit that pushes a branch out of range FAILS
        // LOUDLY rather than silently truncating into a plausible-but-wrong image. For the CURRENT
        // (verified-1899-primes) kernel this can never fire.
        void Patch16(int at, int target)
        {
            int disp = target - at;
            if ((short)disp != disp)
                throw new InvalidOperationException($"68000 sieve .W branch at {at} displacement {disp} out of signed-short range");
            code[at] = (byte)((disp >> 8) & 0xFF);
            code[at + 1] = (byte)(disp & 0xFF);
        }

        const ushort baseAddr = M68000LoadAddress; // 0x1000
        const ushort FlagBase = 0x2000;            // flag array base (clear of code + stack)
        const ushort Size = 8190;                  // FROZEN — the canonical BYTE Jan-1983 Sieve size

        int start = code.Count;                    // 0x1000
        W(0x41F8); W(FlagBase);                    // LEA ($2000).W,A0   (A0 = flag base)
        // clear flags 0x2000..(0x2000+SIZE) to 1 (true)
        W(0x43F8); W(FlagBase);                    // LEA ($2000).W,A1   (A1 = running clear pointer)
        W(0x3C3C); W(Size - 1);                    // MOVE.W #SIZE-1,D6  (DBF count-1 → SIZE iterations)
        int clr = code.Count;
        W(0x12FC); W(0x0001);                      // MOVE.B #1,(A1)+    flags[*] = 1
        W(0x51CE);                                 // DBF D6,clr (16-bit displacement follows)
        int clrDispAt = code.Count; W(0x0000); Patch16(clrDispAt, clr);
        W(0x7000);                                 // MOVEQ #0,D0   i = 0
        W(0x7600);                                 // MOVEQ #0,D3   count = 0
        W(0x7E00);                                 // MOVEQ #0,D7   D7 = 0 (clear source)

        int forI = code.Count;
        W(0x1C30); W(0x0000);                      // MOVE.B (A0,D0.W),D6  -> flags[i] (sets Z)
        W(0x6700); int beqNotPrimeAt = code.Count; W(0x0000);   // BEQ.W notprime (patched)
        // prime = i + i + 3
        W(0x3200);                                 // MOVE.W D0,D1   D1 = i
        W(0xD241);                                 // ADD.W D1,D1    D1 = 2*i
        W(0x5641);                                 // ADDQ.W #3,D1   D1 = prime
        // k = i + prime
        W(0x3400);                                 // MOVE.W D0,D2   D2 = i
        W(0xD441);                                 // ADD.W D1,D2    D2 = k
        int inner = code.Count;
        W(0x0C42); W(Size);                        // CMPI.W #SIZE,D2
        W(0x6400); int bccEndInnerAt = code.Count; W(0x0000);   // BCC.W endinner (patched)
        W(0x4230); W(0x2000);                      // CLR.B (A0,D2.W)   flags[k] = 0 (D2 index brief-ext = 0x2000)
        W(0xD441);                                 // ADD.W D1,D2       k += prime
        W(0x6000); int braInnerAt = code.Count; W(0x0000);      // BRA.W inner (patched)
        int endinner = code.Count;
        Patch16(bccEndInnerAt, endinner);
        Patch16(braInnerAt, inner);
        W(0x5243);                                 // ADDQ.W #1,D3   count++
        int notprime = code.Count;
        Patch16(beqNotPrimeAt, notprime);
        W(0x5240);                                 // ADDQ.W #1,D0   i++
        W(0x0C40); W(Size);                        // CMPI.W #SIZE,D0
        W(0x6500); int bcsForIAt = code.Count; W(0x0000);       // BCS.W forI (patched)
        Patch16(bcsForIAt, forI);
        W(0x6000); int braStartAt = code.Count; W(0x0000);      // BRA.W start (patched)
        Patch16(braStartAt, start);

        return new BenchWorkload(
            Name: "m68k-W3 sieve-kernel",
            Image: code.ToArray(),
            LoadAddress: baseAddr,
            StartPc: baseAddr,
            SuccessTrapPc: 0x0000,                 // unused — W3 terminates by the cycle cap
            FixedCycleCap: M68000SieveCycleCap,
            ExpectedCycles: M68000SieveCycleCap,
            Architecture: "m68000",
            UsesCpmBdos: false);
    }
}
