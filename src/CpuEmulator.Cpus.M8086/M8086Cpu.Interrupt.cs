namespace CpuEmulator.Cpus.M8086;

/// <summary>
/// M5.5d — the 8086 INTERRUPT seam (hand-written): the synchronous software interrupts INT n (0xCD imm8),
/// INT3 (0xCC, vector 3), INTO (0xCE, vector 4 — only when OF is set), and IRET (0xCF), plus the shared
/// <see cref="RaiseInterrupt"/> IVT push sequence the divide-error (INT0) in M8086Cpu.Alu.cs also calls.
/// Dispatched by the generated <c>ExecuteX86</c> to <see cref="InterruptExecute"/>.
///
/// <para><b>The IVT push sequence (ADR 0005 Decision 4 — reconciled byte-exact against the 8088 corpus).</b>
/// An interrupt of type <c>n</c>:
/// <list type="number">
///   <item>PUSH FLAGS (the whole 16-bit word — the corpus seeds the reserved high bits, so pushing FLAGS
///     as-is is byte-exact, exactly as PUSHF);</item>
///   <item>clear IF and TF (the 8086 masks further maskable interrupts + single-step during the handler);</item>
///   <item>PUSH CS (the current code segment);</item>
///   <item>PUSH IP (the RETURN ip — already advanced past the INT instruction by Step's <c>IP += length</c>);</item>
///   <item>fetch the new IP from <c>[0:n*4]</c> and the new CS from <c>[0:n*4+2]</c> (the IVT lives in the
///     first 1 KB of physical memory, segment 0, little-endian).</item>
/// </list>
/// All pushes go through the SS:SP stack (the M5.5c <see cref="PushWord"/>), so SP decrements by 6 total. The
/// IVT reads use segment 0 (NOT CS/DS) — a flat low-memory table.</para>
///
/// <para><b>IF polarity (ADR 0005 Decision 3).</b> The 8086 IF is the OPPOSITE polarity of the 6502 I: IF=1
/// ENABLES maskable interrupts; the interrupt entry CLEARS it (disables). The <see cref="FlagIF"/>/<see cref="FlagTF"/>
/// masks are the M8086Spec layout (IF=bit9, TF=bit8). The no-wait-state 8088 corpus does not exercise async
/// INTR/NMI or the TF single-step trap, so the data axis needs only the synchronous push + the IF/TF clear.</para>
/// </summary>
public sealed partial class M8086Cpu
{
    /// <summary>TF — the trap (single-step) flag (bit 8 of FLAGS, the M8086Spec <c>T</c> member). Cleared on
    /// interrupt entry alongside IF.</summary>
    private const ushort FlagTF = 1 << 8;

    /// <summary>Raise interrupt type <paramref name="vector"/>: the full IVT push sequence (PUSH FLAGS, clear
    /// IF/TF, PUSH CS, PUSH IP, then load CS:IP from the vector table at segment 0). Shared by the software
    /// INT ops here and the divide-error (INT0) in M8086Cpu.Alu.cs. The pushed IP is the CURRENT <see cref="IP"/>
    /// — the caller is responsible for having advanced it to the return point (Step advances past the INT
    /// instruction before dispatch; the divide-error caller raises with IP already past the faulting op).</summary>
    internal void RaiseInterrupt(byte vector)
    {
        PushWord(FLAGS);                                   // 1. push the flags
        SetFlag(FlagIF, false);                            // 2. clear IF (mask maskable interrupts)
        SetFlag(FlagTF, false);                            //    clear TF (no single-step into the handler)
        PushWord(CS);                                      // 3. push the current code segment
        PushWord(IP);                                      // 4. push the return IP
        // 5. fetch the new CS:IP from the IVT (segment 0, vector*4 ⇒ IP, vector*4+2 ⇒ CS), little-endian.
        ushort tableOffset = (ushort)(vector * 4);
        IP = ReadEaWordWrapped(0, tableOffset);
        CS = ReadEaWordWrapped(0, (ushort)(tableOffset + 2));
    }

    /// <summary>M5.5d: execute one interrupt instruction. INT n / INT3 push + vector; INTO conditional on OF;
    /// IRET pops IP, CS, FLAGS.</summary>
    partial void InterruptExecute(uint key, CpuEmulator.Core.Jit.DecodeResult r)
    {
        switch (key)
        {
            // ── CD INT imm8 — the general software interrupt (vector = the imm8). IP is already past the
            //    2-byte instruction (Step advanced it), so the pushed IP is the return point. ──────────────────
            case 0xCDu:
                RaiseInterrupt((byte)r.X86.Imm);
                break;

            // ── CC INT3 — the one-byte breakpoint interrupt, vector 3 (fixed). ──────────────────────────────
            case 0xCCu:
                RaiseInterrupt(3);
                break;

            // ── CE INTO — interrupt-on-overflow, vector 4, ONLY when OF is set; otherwise a no-op (IP already
            //    advanced past the one-byte op). ──────────────────────────────────────────────────────────────
            case 0xCEu:
                if ((FLAGS & FlagOF) != 0) RaiseInterrupt(4);
                break;

            // ── CF IRET — pop IP, then CS, then FLAGS (the reverse of the entry push). FLAGS applies the SAME
            //    8086 reserved-bit forcing as POPF: only the nine DEFINED flag bits (mask 0x0FD5) take the popped
            //    value; bits 12-15 + bit 1 force to 1 (0xF002), bits 3 & 5 force to 0. Reconciled byte-exact
            //    against the CF corpus (popped 0x28CF → FLAGS 0xF8C7) — IRET's flags-mask is 0xFFFF (all bits
            //    asserted), so the forcing is load-bearing, not masked away. ──────────────────────────────────
            case 0xCFu:
            {
                IP = PopWord();
                CS = PopWord();
                FLAGS = (ushort)((PopWord() & FlagsDefinedMask) | FlagsForcedBits);
                break;
            }
        }
    }
}
