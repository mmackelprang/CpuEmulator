using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;

namespace CpuEmulator.Cpus.Mos6502;

/// <summary>The MOS 6502 specification. Hand-grown through Tasks 3-7 of the 3b-i plan;
/// Task 8 replaces this with committed importer output at 149 rows.</summary>
[CpuSpecification("mos6502")]
public static class Mos6502Spec
{
    public static readonly RegisterDef[] Registers =
    [
        new("A", 8),
        new("X", 8),
        new("Y", 8),
        new("S", 8, RegisterRole.StackPointer),
        new("P", 8, RegisterRole.Status),
        new("PC", 16, RegisterRole.ProgramCounter),
    ];

    public static readonly InstructionDef[] Instructions =
    [
        // ── Load class ──────────────────────────────────────────────────────

        // LDA
        Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0xA5, "LDA", AddrMode.ZeroPage,  [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0xB5, "LDA", AddrMode.ZeroPageX, [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0xAD, "LDA", AddrMode.Absolute,  [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0xBD, "LDA", AddrMode.AbsoluteX, [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0xB9, "LDA", AddrMode.AbsoluteY, [Load(Reg.A), SetNZ(Reg.A)]),

        // LDX
        Insn(0xA2, "LDX", AddrMode.Immediate, [Load(Reg.X), SetNZ(Reg.X)]),
        Insn(0xA6, "LDX", AddrMode.ZeroPage,  [Load(Reg.X), SetNZ(Reg.X)]),
        Insn(0xB6, "LDX", AddrMode.ZeroPageY, [Load(Reg.X), SetNZ(Reg.X)]),
        Insn(0xAE, "LDX", AddrMode.Absolute,  [Load(Reg.X), SetNZ(Reg.X)]),
        Insn(0xBE, "LDX", AddrMode.AbsoluteY, [Load(Reg.X), SetNZ(Reg.X)]),

        // LDY
        Insn(0xA0, "LDY", AddrMode.Immediate, [Load(Reg.Y), SetNZ(Reg.Y)]),
        Insn(0xA4, "LDY", AddrMode.ZeroPage,  [Load(Reg.Y), SetNZ(Reg.Y)]),
        Insn(0xB4, "LDY", AddrMode.ZeroPageX, [Load(Reg.Y), SetNZ(Reg.Y)]),
        Insn(0xAC, "LDY", AddrMode.Absolute,  [Load(Reg.Y), SetNZ(Reg.Y)]),
        Insn(0xBC, "LDY", AddrMode.AbsoluteX, [Load(Reg.Y), SetNZ(Reg.Y)]),

        // ── Store class ──────────────────────────────────────────────────────

        // STA
        Insn(0x85, "STA", AddrMode.ZeroPage,  [Store(Reg.A)]),
        Insn(0x95, "STA", AddrMode.ZeroPageX, [Store(Reg.A)]),
        Insn(0x8D, "STA", AddrMode.Absolute,  [Store(Reg.A)]),
        Insn(0x9D, "STA", AddrMode.AbsoluteX, [Store(Reg.A)]),
        Insn(0x99, "STA", AddrMode.AbsoluteY, [Store(Reg.A)]),

        // STX
        Insn(0x86, "STX", AddrMode.ZeroPage,  [Store(Reg.X)]),
        Insn(0x96, "STX", AddrMode.ZeroPageY, [Store(Reg.X)]),
        Insn(0x8E, "STX", AddrMode.Absolute,  [Store(Reg.X)]),

        // STY
        Insn(0x84, "STY", AddrMode.ZeroPage,  [Store(Reg.Y)]),
        Insn(0x94, "STY", AddrMode.ZeroPageX, [Store(Reg.Y)]),
        Insn(0x8C, "STY", AddrMode.Absolute,  [Store(Reg.Y)]),

        // ── Register / transfer class ────────────────────────────────────────

        Insn(0xAA, "TAX", AddrMode.Implied, [Transfer(Reg.A, Reg.X), SetNZ(Reg.X)]),
        Insn(0xA8, "TAY", AddrMode.Implied, [Transfer(Reg.A, Reg.Y), SetNZ(Reg.Y)]),
        Insn(0x8A, "TXA", AddrMode.Implied, [Transfer(Reg.X, Reg.A), SetNZ(Reg.A)]),
        Insn(0x98, "TYA", AddrMode.Implied, [Transfer(Reg.Y, Reg.A), SetNZ(Reg.A)]),
        Insn(0xBA, "TSX", AddrMode.Implied, [Transfer(Reg.S, Reg.X), SetNZ(Reg.X)]),
        Insn(0x9A, "TXS", AddrMode.Implied, [Transfer(Reg.X, Reg.S)]),          // no SetNZ

        Insn(0xE8, "INX", AddrMode.Implied, [Increment(Reg.X), SetNZ(Reg.X)]),
        Insn(0xC8, "INY", AddrMode.Implied, [Increment(Reg.Y), SetNZ(Reg.Y)]),

        Insn(0xEA, "NOP", AddrMode.Implied, []),

        // ── Jump class ───────────────────────────────────────────────────────

        Insn(0x4C, "JMP", AddrMode.Absolute, [Jump()]),

        // ── Branch class ─────────────────────────────────────────────────────

        Insn(0xD0, "BNE", AddrMode.Relative, [BranchIf(Flag.Z, false)]),
        Insn(0xF0, "BEQ", AddrMode.Relative, [BranchIf(Flag.Z, true)]),
        Insn(0xB0, "BCS", AddrMode.Relative, [BranchIf(Flag.C, true)]),
        Insn(0x90, "BCC", AddrMode.Relative, [BranchIf(Flag.C, false)]),
        Insn(0x10, "BPL", AddrMode.Relative, [BranchIf(Flag.N, false)]),
        Insn(0x30, "BMI", AddrMode.Relative, [BranchIf(Flag.N, true)]),
        Insn(0x50, "BVC", AddrMode.Relative, [BranchIf(Flag.V, false)]),
        Insn(0x70, "BVS", AddrMode.Relative, [BranchIf(Flag.V, true)]),
    ];
}
