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
        Insn(0xA1, "LDA", AddrMode.IndirectX, [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0xB1, "LDA", AddrMode.IndirectY, [Load(Reg.A), SetNZ(Reg.A)]),

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
        Insn(0x81, "STA", AddrMode.IndirectX, [Store(Reg.A)]),
        Insn(0x91, "STA", AddrMode.IndirectY, [Store(Reg.A)]),

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
        Insn(0x6C, "JMP", AddrMode.Indirect, [Jump()]),

        // ── Branch class ─────────────────────────────────────────────────────

        Insn(0xD0, "BNE", AddrMode.Relative, [BranchIf(Flag.Z, false)]),
        Insn(0xF0, "BEQ", AddrMode.Relative, [BranchIf(Flag.Z, true)]),
        Insn(0xB0, "BCS", AddrMode.Relative, [BranchIf(Flag.C, true)]),
        Insn(0x90, "BCC", AddrMode.Relative, [BranchIf(Flag.C, false)]),
        Insn(0x10, "BPL", AddrMode.Relative, [BranchIf(Flag.N, false)]),
        Insn(0x30, "BMI", AddrMode.Relative, [BranchIf(Flag.N, true)]),
        Insn(0x50, "BVC", AddrMode.Relative, [BranchIf(Flag.V, false)]),
        Insn(0x70, "BVS", AddrMode.Relative, [BranchIf(Flag.V, true)]),

        // ── ALU class ────────────────────────────────────────────────────────

        // ADC (8 rows)
        Insn(0x69, "ADC", AddrMode.Immediate, [Adc()]),
        Insn(0x65, "ADC", AddrMode.ZeroPage,  [Adc()]),
        Insn(0x75, "ADC", AddrMode.ZeroPageX, [Adc()]),
        Insn(0x6D, "ADC", AddrMode.Absolute,  [Adc()]),
        Insn(0x7D, "ADC", AddrMode.AbsoluteX, [Adc()]),
        Insn(0x79, "ADC", AddrMode.AbsoluteY, [Adc()]),
        Insn(0x61, "ADC", AddrMode.IndirectX, [Adc()]),
        Insn(0x71, "ADC", AddrMode.IndirectY, [Adc()]),

        // SBC (8 rows)
        Insn(0xE9, "SBC", AddrMode.Immediate, [Sbc()]),
        Insn(0xE5, "SBC", AddrMode.ZeroPage,  [Sbc()]),
        Insn(0xF5, "SBC", AddrMode.ZeroPageX, [Sbc()]),
        Insn(0xED, "SBC", AddrMode.Absolute,  [Sbc()]),
        Insn(0xFD, "SBC", AddrMode.AbsoluteX, [Sbc()]),
        Insn(0xF9, "SBC", AddrMode.AbsoluteY, [Sbc()]),
        Insn(0xE1, "SBC", AddrMode.IndirectX, [Sbc()]),
        Insn(0xF1, "SBC", AddrMode.IndirectY, [Sbc()]),

        // AND (8 rows)
        Insn(0x29, "AND", AddrMode.Immediate, [And()]),
        Insn(0x25, "AND", AddrMode.ZeroPage,  [And()]),
        Insn(0x35, "AND", AddrMode.ZeroPageX, [And()]),
        Insn(0x2D, "AND", AddrMode.Absolute,  [And()]),
        Insn(0x3D, "AND", AddrMode.AbsoluteX, [And()]),
        Insn(0x39, "AND", AddrMode.AbsoluteY, [And()]),
        Insn(0x21, "AND", AddrMode.IndirectX, [And()]),
        Insn(0x31, "AND", AddrMode.IndirectY, [And()]),

        // ORA (8 rows)
        Insn(0x09, "ORA", AddrMode.Immediate, [Ora()]),
        Insn(0x05, "ORA", AddrMode.ZeroPage,  [Ora()]),
        Insn(0x15, "ORA", AddrMode.ZeroPageX, [Ora()]),
        Insn(0x0D, "ORA", AddrMode.Absolute,  [Ora()]),
        Insn(0x1D, "ORA", AddrMode.AbsoluteX, [Ora()]),
        Insn(0x19, "ORA", AddrMode.AbsoluteY, [Ora()]),
        Insn(0x01, "ORA", AddrMode.IndirectX, [Ora()]),
        Insn(0x11, "ORA", AddrMode.IndirectY, [Ora()]),

        // EOR (8 rows)
        Insn(0x49, "EOR", AddrMode.Immediate, [Eor()]),
        Insn(0x45, "EOR", AddrMode.ZeroPage,  [Eor()]),
        Insn(0x55, "EOR", AddrMode.ZeroPageX, [Eor()]),
        Insn(0x4D, "EOR", AddrMode.Absolute,  [Eor()]),
        Insn(0x5D, "EOR", AddrMode.AbsoluteX, [Eor()]),
        Insn(0x59, "EOR", AddrMode.AbsoluteY, [Eor()]),
        Insn(0x41, "EOR", AddrMode.IndirectX, [Eor()]),
        Insn(0x51, "EOR", AddrMode.IndirectY, [Eor()]),

        // CMP (8 rows)
        Insn(0xC9, "CMP", AddrMode.Immediate, [Compare(Reg.A)]),
        Insn(0xC5, "CMP", AddrMode.ZeroPage,  [Compare(Reg.A)]),
        Insn(0xD5, "CMP", AddrMode.ZeroPageX, [Compare(Reg.A)]),
        Insn(0xCD, "CMP", AddrMode.Absolute,  [Compare(Reg.A)]),
        Insn(0xDD, "CMP", AddrMode.AbsoluteX, [Compare(Reg.A)]),
        Insn(0xD9, "CMP", AddrMode.AbsoluteY, [Compare(Reg.A)]),
        Insn(0xC1, "CMP", AddrMode.IndirectX, [Compare(Reg.A)]),
        Insn(0xD1, "CMP", AddrMode.IndirectY, [Compare(Reg.A)]),

        // CPX (3 rows)
        Insn(0xE0, "CPX", AddrMode.Immediate, [Compare(Reg.X)]),
        Insn(0xE4, "CPX", AddrMode.ZeroPage,  [Compare(Reg.X)]),
        Insn(0xEC, "CPX", AddrMode.Absolute,  [Compare(Reg.X)]),

        // CPY (3 rows)
        Insn(0xC0, "CPY", AddrMode.Immediate, [Compare(Reg.Y)]),
        Insn(0xC4, "CPY", AddrMode.ZeroPage,  [Compare(Reg.Y)]),
        Insn(0xCC, "CPY", AddrMode.Absolute,  [Compare(Reg.Y)]),

        // BIT (2 rows)
        Insn(0x24, "BIT", AddrMode.ZeroPage,  [Bit()]),
        Insn(0x2C, "BIT", AddrMode.Absolute,  [Bit()]),
    ];
}
