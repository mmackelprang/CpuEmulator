using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;

namespace CpuEmulator.Cpus.Mos6502;

/// <summary>The MOS 6502 specification. Chunk 2 carries an 11-opcode proving subset;
/// chunk 3 scales to the full documented instruction set.</summary>
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
        Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0xA5, "LDA", AddrMode.ZeroPage,  [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0xAD, "LDA", AddrMode.Absolute,  [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0x85, "STA", AddrMode.ZeroPage,  [Store(Reg.A)]),
        Insn(0x8D, "STA", AddrMode.Absolute,  [Store(Reg.A)]),
        Insn(0xA2, "LDX", AddrMode.Immediate, [Load(Reg.X), SetNZ(Reg.X)]),
        Insn(0xAA, "TAX", AddrMode.Implied,   [Transfer(Reg.A, Reg.X), SetNZ(Reg.X)]),
        Insn(0xE8, "INX", AddrMode.Implied,   [Increment(Reg.X), SetNZ(Reg.X)]),
        Insn(0x4C, "JMP", AddrMode.Absolute,  [Jump()]),
        Insn(0xD0, "BNE", AddrMode.Relative,  [BranchIf(Flag.Z, false)]),
        Insn(0xEA, "NOP", AddrMode.Implied,   []),
    ];
}
