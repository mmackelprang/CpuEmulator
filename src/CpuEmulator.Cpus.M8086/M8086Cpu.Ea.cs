using CpuEmulator.Core;

namespace CpuEmulator.Cpus.M8086;

/// <summary>M5.3 (ADR 0005 Decision 2) — the 8086 SEGMENTATION layer: the part of the effective-address
/// pipeline that lives in the hand-written partial (the 16-bit offset table itself is generated as
/// <c>ComputeX86Ea</c> by <c>EmitX86Ea</c>). This file owns:
///
/// <list type="bullet">
///   <item>the 20-bit physical-address formation <c>(segment &lt;&lt; 4) + offset</c> masked to 20 bits
///     (<see cref="Physical"/>);</item>
///   <item>the segment SELECTION — the default-segment-per-mode rule (the generated
///     <c>DefaultSegmentForX86Rm</c> picks SS for BP-based forms, DS otherwise) threaded with the
///     segment-OVERRIDE prefix the M5.2 decode walk accumulated (<see cref="ResolveSegment"/>);</item>
///   <item>the EA-operand byte/word read+write helpers over the resolved physical address
///     (<see cref="ReadEaByte"/>/<see cref="WriteEaByte"/>/<see cref="ReadEaWordPhysical"/>/<see cref="WriteEaWordPhysical"/>).</item>
/// </list>
///
/// The bus is UNCHANGED — the flat 20-bit little-endian <see cref="IAddressSpace"/> is the physical bus
/// (ADR 0005 Decision 2(A): segmentation is CPU state the bus never learns; the segment shift + 16-bit-offset
/// wrap + 20-bit mask is pure arithmetic in the CPU). The 8088 two-byte-cycle 16-bit access falls out of the
/// LE wide-method composition (Read16 = two Read8) for free — the byte-bus trace the vectors record.
///
/// NO op body consumes this yet — M5.3 ships the EA/segmentation layer + its synthetic+unit proof; the MOV
/// pipeline that drives it end-to-end (decode → ModR/M → EA → segment → bus) is M5.5a.</summary>
public sealed partial class M8086Cpu
{
    /// <summary>A segment-override prefix the decode walk accumulated (ADR 0005 Decision 2): one of the four
    /// override bytes (26=ES, 2E=CS, 36=SS, 3E=DS) or <see cref="None"/> when no override is in force. The
    /// M5.2 walk stacks these as prefix bytes; M5.5a threads the active one to <see cref="ResolveSegment"/>
    /// (which replaces the per-mode default, with the documented non-overridable exceptions — code fetch, the
    /// string ES:DI destination, the stack push/pop — enforced by the op body that calls in, NOT here).</summary>
    public enum X86SegmentOverride
    {
        None = 0,
        Es,   // 0x26
        Cs,   // 0x2E
        Ss,   // 0x36
        Ds,   // 0x3E
    }

    /// <summary>Form the 20-bit PHYSICAL address from a segment register value and a 16-bit offset
    /// (ADR 0005 Decision 2): <c>physical = ((segment &lt;&lt; 4) + offset) &amp; 0xFFFFF</c>. The offset has
    /// ALREADY wrapped at 16 bits in the EA computation (the generated <c>ComputeX86Ea</c> — the segment-
    /// relative wrap quirk); this only does the shift + the 20-bit mask. The <c>(seg&lt;&lt;4)+offset</c> sum
    /// CAN carry into the 20th bit, and a near-top sum wraps via the 20-bit mask (e.g.
    /// <c>seg=0xFFFF, offset=0xFFFF</c> → <c>0xFFFEF</c>, the classic high-memory wrap).</summary>
    internal static uint Physical(ushort segment, ushort offset)
        => (uint)(((segment << 4) + offset) & 0xFFFFF);

    /// <summary>Select the SEGMENT-register value for a memory operand: the per-mode default (passed in by the
    /// caller from the generated <c>DefaultSegmentForX86Rm</c>) UNLESS a segment-override prefix is in force,
    /// in which case the override's register replaces it (ADR 0005 Decision 2). The non-overridable exceptions
    /// (code fetch ⇒ always CS; the string ES:DI destination ⇒ always ES; the stack push/pop ⇒ always SS) are
    /// enforced by the op body that decides whether to consult an override AT ALL — this helper applies an
    /// override only when the caller passes one (so a non-overridable access simply passes
    /// <see cref="X86SegmentOverride.None"/>).</summary>
    internal ushort ResolveSegment(ushort defaultSegment, X86SegmentOverride over) => over switch
    {
        X86SegmentOverride.Es => ES,
        X86SegmentOverride.Cs => CS,
        X86SegmentOverride.Ss => SS,
        X86SegmentOverride.Ds => DS,
        _ => defaultSegment,   // None ⇒ keep the per-mode default
    };

    /// <summary>Compose the full memory-operand pipeline for a ModR/M (mod, r/m) + displacement: compute the
    /// 16-bit offset (the generated <c>ComputeX86Ea</c>), select the segment (the generated
    /// <c>DefaultSegmentForX86Rm</c> default, threaded with the override), and form the 20-bit physical address
    /// (<see cref="Physical"/>). The single entry point a MOV/ALU memory operand resolves through (M5.5a).</summary>
    internal uint ResolveEaPhysical(uint mod, uint rm, ushort disp, X86SegmentOverride over)
    {
        ushort offset = ComputeX86Ea(mod, rm, disp);
        ushort segment = ResolveSegment(DefaultSegmentForX86Rm(mod, rm), over);
        return Physical(segment, offset);
    }

    // ── EA-operand read/write over the resolved physical address (the M68000 ReadEaOperand/WriteEaOperand
    //    analogue — ADR 0006 Decision 2). Byte + word; the word forms compose two byte cycles via the LE bus
    //    (the 8088 byte-bus trace). These charge a cycle per byte (the cycle invariant), matching ReadBus/
    //    WriteBus. The OP BODIES (M5.5a) call these once they have the resolved physical address. ───────────

    /// <summary>Read one byte from the resolved physical EA. Charges one cycle (the cycle invariant).</summary>
    internal byte ReadEaByte(uint physical) => ReadBus(physical);

    /// <summary>Write one byte to the resolved physical EA. Charges one cycle.</summary>
    internal void WriteEaByte(uint physical, byte value) => WriteBus(physical, value);

    /// <summary>Read one 16-bit word from a PHYSICAL EA — LITTLE-ENDIAN, two byte cycles (low byte at the lower
    /// address), incrementing the PHYSICAL address (<c>(physical + 1) &amp; 0xFFFFF</c>). Charges two cycles.
    /// <para>DOES NOT implement the 8086 segment-relative offset wrap — do NOT use for data operands: at segment
    /// offset 0xFFFF the 8086 wraps the OFFSET within the 64 KB segment (the high byte lands at offset 0x0000),
    /// NOT the physical address. The wrapped variants in M8086Cpu.Mov.cs (ReadEaWordWrapped/WriteEaWordWrapped)
    /// are the correct data-operand helpers. This physical-increment form exists only to back the M5.3 synthetic
    /// EA proof.</para></summary>
    internal ushort ReadEaWordPhysical(uint physical)
    {
        byte lo = ReadBus(physical);
        byte hi = ReadBus((physical + 1) & 0xFFFFF);
        return (ushort)(lo | (hi << 8));
    }

    /// <summary>Write one 16-bit word to a PHYSICAL EA — LITTLE-ENDIAN, two byte cycles (low byte at the lower
    /// address), incrementing the PHYSICAL address (<c>(physical + 1) &amp; 0xFFFFF</c>). Charges two cycles.
    /// <para>DOES NOT implement the 8086 segment-relative offset wrap — do NOT use for data operands: at segment
    /// offset 0xFFFF the 8086 wraps the OFFSET within the 64 KB segment (the high byte lands at offset 0x0000),
    /// NOT the physical address. The wrapped variants in M8086Cpu.Mov.cs (ReadEaWordWrapped/WriteEaWordWrapped)
    /// are the correct data-operand helpers. This physical-increment form exists only to back the M5.3 synthetic
    /// EA proof.</para></summary>
    internal void WriteEaWordPhysical(uint physical, ushort value)
    {
        WriteBus(physical, (byte)value);
        WriteBus((physical + 1) & 0xFFFFF, (byte)(value >> 8));
    }
}
