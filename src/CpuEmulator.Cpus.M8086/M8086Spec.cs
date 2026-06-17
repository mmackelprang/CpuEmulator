// ─────────────────────────────────────────────────────────────────────────────────────────────────
//  HAND-AUTHORED for M5.1 — state only. This is NOT a generated file (yet): it declares the 8086
//  register file + the FLAGS bit layout and NOTHING ELSE. There is deliberately no FieldGrammar and no
//  DecodeStructure — decode is M5.2. The Instructions table is empty — op bodies are M5.3+.
//
//  M5.4 builds the SpecImporter arm that REGENERATES this file from the canonical opcode dataset and
//  pins it (the byte-identity regen guard is added in M5.4); until then this hand-written state-only
//  spec is what the source generator reads to emit M8086Cpu.g.cs (the register file + the degenerate
//  Step). ADR 0005 governs the design decisions cited below.
// ─────────────────────────────────────────────────────────────────────────────────────────────────

using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;

namespace CpuEmulator.Cpus.M8086;

[CpuSpecification("m8086")]
public static class M8086Spec
{
    public static readonly RegisterDef[] Registers =
    [
        // The eight 8-bit backing halves (declared first so the AX/BX/CX/DX pair-views resolve them).
        new("AH", 8), new("AL", 8),
        new("BH", 8), new("BL", 8),
        new("CH", 8), new("CL", 8),
        new("DH", 8), new("DL", 8),

        // The four 16-bit general-purpose pair-views over the halves above (high<<8 | low). The
        // generator emits each as a computed property over its halves — so writing AL leaves AH intact
        // (the partial-write hazard the M5.1 state tests prove).
        new("AX", 16, HighHalf: "AH", LowHalf: "AL"),
        new("BX", 16, HighHalf: "BH", LowHalf: "BL"),
        new("CX", 16, HighHalf: "CH", LowHalf: "CL"),
        new("DX", 16, HighHalf: "DH", LowHalf: "DL"),

        // The stack/index/base pointers — full 16-bit, NOT byte-decomposable. SP carries the
        // StackPointer role (ADR 0005 / plan §9 Q3 — the DEFAULT adopted).
        new("SP", 16, RegisterRole.StackPointer),
        new("BP", 16),
        new("SI", 16),
        new("DI", 16),

        // The four segment registers — General role; segmentation (the 20-bit physical resolution
        // seg<<4 + offset) lives in the hand-written partial, not the spec (ADR 0005 / plan §9 Q1 —
        // the DEFAULT adopted). The physical-address layer itself is M5.3.
        new("CS", 16),
        new("DS", 16),
        new("ES", 16),
        new("SS", 16),

        new("IP", 16, RegisterRole.ProgramCounter),
        new("FLAGS", 16, RegisterRole.Status),
    ];

    // The real 8086 FLAGS bit positions. C/P/Z/S/T/I/V reuse the existing Flag members at their 8086
    // bits; "H" carries AF (the BCD half-carry, bit 4); "Df" carries DF (the direction flag, bit 10).
    public static readonly FlagLayout Flags = new([
        new("C", 0), new("P", 2), new("H", 4), new("Z", 6), new("S", 7),
        new("T", 8), new("I", 9), new("Df", 10), new("V", 11),
    ]);

    // Empty — the instruction set is M5.3+. With an empty table the generated Step never decodes a real
    // op; every byte routes to HandleUndefinedOpcode (M5.1 never meaningfully calls Step).
    public static readonly InstructionDef[] Instructions = [];
}
