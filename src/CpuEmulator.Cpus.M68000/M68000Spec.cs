// M4.1: the 68000 register-file specification — HAND-WRITTEN (register-only), not importer-generated.
//
// TODO(M4.4): fold into the importer pipeline when the field-pattern dataset + register-only-CPU support
// land. The importer currently REJECTS a zero-row opcode dataset (OpcodeDataset.Parse throws
// "Opcode dataset is empty." on an empty array), which protects the 6502/Z80 from an accidentally-empty
// real dataset. The M4.1 68000 has ZERO instruction rows (state-only; decode/EA/ops are M4.3+), so it
// cannot go through the importer without weakening that guard — see plan Decision D4. This file is
// therefore a guarded hand-write matching the EXACT shape the importer would emit (the Registers table +
// FlagLayout + an empty Instructions array, and NO DecodeStructure because there are no prefix bytes).
//
// Register model (ADR 0003 §1.2): D0–D7 (data, 32-bit) and A0–A6 (address, 32-bit) are General; USP and
// SSP are the two physical 32-bit stack pointers (USP General, SSP the StackPointer role — the reset
// vector loads the supervisor stack). A7 is NOT a spec register (Decision D2): the TomHarte schema names
// usp/ssp, never a7 — A7 is a hand-written mode-selected VIEW on the M68000Cpu partial (the SR S-bit
// selects USP vs SSP). PC is the 32-bit ProgramCounter; SR is the 16-bit Status register.
//
// SR/CCR FlagLayout (Decision D5 — Flag-enum members ONLY): the CCR is the low byte (C=0 V=1 Z=2 N=3 X=4)
// and the supervisor S bit is at 13 (the one banking selector the partial reads). The trace (T) bit and
// the 3-bit interrupt mask (I0–I2) are NOT Flag-enum members, so they are modeled as raw SR bits — the
// full 16-bit SR round-trips losslessly via SetRegister("SR", …)/GetRegister("SR") regardless of the
// layout, which only names the bits the eventual flag-emitting ALU ops (M4.5) reference symbolically.

using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;

namespace CpuEmulator.Cpus.M68000;

[CpuSpecification("m68000")]
public static class M68000Spec
{
    public static readonly RegisterDef[] Registers =
    [
        new("D0", 32),
        new("D1", 32),
        new("D2", 32),
        new("D3", 32),
        new("D4", 32),
        new("D5", 32),
        new("D6", 32),
        new("D7", 32),
        new("A0", 32),
        new("A1", 32),
        new("A2", 32),
        new("A3", 32),
        new("A4", 32),
        new("A5", 32),
        new("A6", 32),
        new("USP", 32),
        new("SSP", 32, RegisterRole.StackPointer),
        new("PC", 32, RegisterRole.ProgramCounter),
        new("SR", 16, RegisterRole.Status),
    ];

    public static readonly FlagLayout Flags = new([new("C", 0), new("V", 1), new("Z", 2), new("N", 3), new("X", 4), new("S", 13)]);

    public static readonly InstructionDef[] Instructions = [];
}
