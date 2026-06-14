using CpuEmulator.Core.Specification;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M3.4a Task 4 — the 5 new Z80 register-shape AddrModes + the class/mode matrix (Ground truth D).
/// The new modes parse and the Z80 base-plane shapes classify + validate; the 6502 class/mode rules
/// stay unchanged (additive). Proven by running the generator over small synthetic Z80-shaped specs
/// and asserting NO CPUGEN diagnostics for valid shapes (and CPUGEN010 for an illegal mode/class).
/// </summary>
public class Z80AddrModeParserTests
{
    // A minimal Z80-shaped spec scaffold: A/F + pairs, a flag layout, a StackPointer + PC. The
    // {INSN} placeholder is replaced per-test with the instruction under examination.
    private const string Scaffold = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticZ80;

        [CpuSpecification("z80like")]
        public static class Z80LikeSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status),
                new("B", 8), new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16),   // M3.4c: the MEMPTR register every Z80 flow/LD/EX op writes
                new("SP", 16, RegisterRole.StackPointer),
                new("PC", 16, RegisterRole.ProgramCounter),
                new("BC", 16, HighHalf: "B", LowHalf: "C"),
                new("DE", 16, HighHalf: "D", LowHalf: "E"),
                new("HL", 16, HighHalf: "H", LowHalf: "L"),
            ];

            public static readonly FlagLayout Flags = new(
                [ new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                  new("X", 3), new("P", 2), new("N", 1), new("C", 0) ]);

            public static readonly InstructionDef[] Instructions =
            [
                {INSN}
            ];
        }

        public sealed partial class Z80LikeCpu
        {
            private readonly IAddressSpace _bus;
            private bool _iff1, _iff2;
            public byte Q;   // M3.4b: the Q pseudo-register every Z80 op maintains (Q=F / Q=0)
            public bool Iff1 { get => _iff1; set => _iff1 = value; }
            public bool Iff2 { get => _iff2; set => _iff2 = value; }
            public Z80LikeCpu(IAddressSpace bus) => _bus = bus;
            public void Reset() { }
            public void SetIrqLine(bool asserted) { }
            public void SetNmiLine(bool asserted) { }
            private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
            private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
            private byte ReadIo(uint port) { _cycles++; return _bus.Read8(port); }
            private void WriteIo(uint port, byte value) { _cycles++; _bus.Write8(port, value); }
            private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    private static GeneratorRunResult RunWith(string insns) =>
        GeneratorTestHost.Run(Scaffold.Replace("{INSN}", insns));

    [Fact]
    public void New_modes_parse_and_the_base_plane_shapes_generate_cleanly()
    {
        // One row per new mode/class shape — all valid Z80 base-plane shapes. NO CPUGEN diagnostics.
        var result = RunWith("""
                Insn(0x80, "ADD", AddrMode.Register, [Add8()]),
                Insn(0x86, "ADD", AddrMode.RegisterIndirect, [Add8()]),
                Insn(0xC6, "ADD", AddrMode.Immediate, [Add8()]),
                Insn(0x09, "ADDHL", AddrMode.Register, [Add16("HL","BC")]),
                Insn(0x04, "INC", AddrMode.Register, [IncReg("B")]),
                Insn(0x34, "INCM", AddrMode.RegisterIndirect, [IncMem8()]),
                Insn(0x41, "LD", AddrMode.Register, [Transfer("C","B")]),
                Insn(0x46, "LD", AddrMode.RegisterIndirect, [Load("B")]),
                Insn(0x21, "LDI", AddrMode.ImmediateExtended, [Load16("HL")]),
                Insn(0x22, "LDS", AddrMode.ExtendedAddress, [Store16("HL")]),
                Insn(0x3A, "LDA", AddrMode.ExtendedAddress, [Load("A")]),
                Insn(0xC5, "PUSH", AddrMode.Register, [Push16("BC")]),
                Insn(0xEB, "EX", AddrMode.Register, [ExDeHl()]),
                Insn(0xE3, "EXSP", AddrMode.RegisterIndirect, [ExSpHl()]),
                Insn(0xC3, "JP", AddrMode.ExtendedAddress, [JumpAbs()]),
                Insn(0xC2, "JPCC", AddrMode.ExtendedAddress, [JumpIf(Flag.Z, false)]),
                Insn(0xE9, "JPHL", AddrMode.RegisterIndirect, [JumpIndirect()]),
                Insn(0x18, "JR", AddrMode.RelativeJump, [RelJump()]),
                Insn(0x20, "JRCC", AddrMode.RelativeJump, [RelJumpIf(Flag.Z, false)]),
                Insn(0x10, "DJNZ", AddrMode.RelativeJump, [Djnz("B")]),
                Insn(0xCD, "CALL", AddrMode.ExtendedAddress, [CallAbs()]),
                Insn(0xC4, "CALLCC", AddrMode.ExtendedAddress, [CallIf(Flag.Z, false)]),
                Insn(0xC0, "RETCC", AddrMode.Implied, [RetCc(Flag.Z, false)]),
                Insn(0xC7, "RST", AddrMode.Implied, [Rst()]),
                Insn(0x27, "DAA", AddrMode.Implied, [Daa()]),
                Insn(0xF3, "DI", AddrMode.Implied, [Di()]),
            """);
        Assert.Empty(result.GeneratorDiagnostics);
        Assert.Empty(result.AllErrors);
    }

    [Fact]
    public void Z80_ALU_in_an_illegal_mode_is_rejected()
    {
        // Z80 ALU class requires Register/RegisterIndirect/Immediate — Absolute is illegal (CPUGEN010).
        var result = RunWith("""Insn(0x80, "ADD", AddrMode.Absolute, [Add8()]),""");
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Z80_stack_in_a_non_register_mode_is_rejected()
    {
        // Push16 requires Register mode — Implied is illegal.
        var result = RunWith("""Insn(0xC5, "PUSH", AddrMode.Implied, [Push16("BC")]),""");
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }

    [Fact]
    public void Z80_flow_in_an_illegal_mode_is_rejected()
    {
        // RelJump requires RelativeJump mode — Register is illegal.
        var result = RunWith("""Insn(0x18, "JR", AddrMode.Register, [RelJump()]),""");
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN010");
    }
}
