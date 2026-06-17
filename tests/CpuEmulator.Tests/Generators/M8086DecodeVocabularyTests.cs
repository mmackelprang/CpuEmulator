using CpuEmulator.Core.Specification;
using Microsoft.CodeAnalysis;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M5.2 — the x86 decode-structure CARRIER vocabulary (ADR 0006 Decision 1): the
/// <see cref="X86DecodeStructure"/> / <see cref="X86Prefix"/> / <see cref="X86Opcode"/> records + the two
/// enums round-trip their fields; a declared structure parses clean; a malformed one reports CPUGEN016.
/// The carrier is the M4.3a FieldGrammar analogue — a third, structurally distinct, byte-unit decode SHAPE
/// that is opt-in (ABSENT on 6502/Z80/68000, so their walks are byte-identical).</summary>
public class M8086DecodeVocabularyTests
{
    [Fact]
    public void X86Prefix_carries_value_and_role()
    {
        var p = new X86Prefix(0x26, X86PrefixRole.SegmentOverride);
        Assert.Equal((byte)0x26, p.Value);
        Assert.Equal(X86PrefixRole.SegmentOverride, p.Role);
    }

    [Fact]
    public void X86Opcode_defaults_are_plain_no_modrm_no_immediate()
    {
        var op = new X86Opcode(0x90);
        Assert.Equal((byte)0x90, op.Value);
        Assert.False(op.HasModRm);
        Assert.False(op.RegIsExtension);
        Assert.Equal(-1, op.WBit);
        Assert.Equal(-1, op.SBit);
        Assert.Equal(X86ImmediateRule.None, op.Immediate);
    }

    [Fact]
    public void X86Opcode_carries_modrm_group_and_immediate_metadata()
    {
        var op = new X86Opcode(0x83, HasModRm: true, RegIsExtension: true, WBit: 0, SBit: 1,
            Immediate: X86ImmediateRule.SWBit);
        Assert.True(op.HasModRm);
        Assert.True(op.RegIsExtension);
        Assert.Equal(0, op.WBit);
        Assert.Equal(1, op.SBit);
        Assert.Equal(X86ImmediateRule.SWBit, op.Immediate);
    }

    [Fact]
    public void X86DecodeStructure_carries_prefixes_and_opcodes()
    {
        var d = new X86DecodeStructure(
            Prefixes: [new X86Prefix(0xF3, X86PrefixRole.Repeat)],
            Opcodes: [new X86Opcode(0x88, HasModRm: true)]);
        Assert.Single(d.Prefixes);
        Assert.Single(d.Opcodes);
        Assert.Equal(X86PrefixRole.Repeat, d.Prefixes[0].Role);
        Assert.True(d.Opcodes[0].HasModRm);
    }

    [Fact]
    public void A_spec_declaring_an_x86_decode_structure_parses_clean()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
    }

    [Fact]
    public void An_orphan_opcode_with_no_Insn_row_reports_CPUGEN016()
    {
        // Declaring an opcode (0x77) that backs NO Insn row is a CPUGEN016 — the cross-check that keeps the
        // decode metadata and the instruction table consistent (the ParseDecodeStructure discipline).
        string source = GeneratorTestHost.ReplaceSection(
            Spec,
            "new X86Opcode(0x90),",
            "new X86Opcode(0x90), new X86Opcode(0x77),");
        var result = GeneratorTestHost.Run(source);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN016");
    }

    [Fact]
    public void A_group_opcode_with_no_group_Insn_row_reports_CPUGEN016()
    {
        // A RegIsExtension opcode must back at least one OpcodeGroup Insn row; declaring it as a group
        // opcode while only a plain (OpcodeByte) row exists is a CPUGEN016.
        string source = GeneratorTestHost.ReplaceSection(
            Spec,
            "new X86Opcode(0x88, HasModRm: true),",
            "new X86Opcode(0x88, HasModRm: true, RegIsExtension: true),");
        var result = GeneratorTestHost.Run(source);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN016");
    }

    [Fact]
    public void A_RegIsExtension_opcode_without_HasModRm_reports_CPUGEN016()
    {
        // RegIsExtension REQUIRES HasModRm: the reg field that extends the opcode IS the ModR/M reg field,
        // so a group opcode necessarily carries a ModR/M byte. Declaring RegIsExtension without HasModRm is
        // an incoherent encoding (the generated walk would never form the group key) — a CPUGEN016.
        string source = GeneratorTestHost.ReplaceSection(
            Spec,
            "new X86Opcode(0x90),",
            "new X86Opcode(0x90), new X86Opcode(0x80, RegIsExtension: true),");
        var result = GeneratorTestHost.Run(source);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN016");
    }

    [Fact]
    public void A_WBit_immediate_without_a_WBit_position_reports_CPUGEN016()
    {
        // Immediate.WBit needs a WBit position to read; omitting it is a CPUGEN016 (the walk could not size
        // the immediate otherwise).
        string source = GeneratorTestHost.ReplaceSection(
            Spec,
            "new X86Opcode(0x90),",
            "new X86Opcode(0x90), new X86Opcode(0xB0, Immediate: X86ImmediateRule.WBit),");
        var result = GeneratorTestHost.Run(source);
        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN016");
    }

    // A minimal x86-decode spec: one prefix, a plain opcode, a ModR/M opcode, and a group opcode (with the
    // matching Insn rows the cross-check requires).
    private const string Spec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("x86vocab")]
        public static class X86VocabSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 16),
                new("IP", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly X86DecodeStructure Decode = new(
                Prefixes: [ new X86Prefix(0xF3, X86PrefixRole.Repeat) ],
                Opcodes:
                [
                    new X86Opcode(0x90),
                    new X86Opcode(0x88, HasModRm: true),
                    new X86Opcode(0x80, HasModRm: true, RegIsExtension: true, WBit: 0, Immediate: X86ImmediateRule.WBit),
                ]);

            public static readonly InstructionDef[] Instructions =
            [
                Insn(0x90, "NOP",   AddrMode.Implied, []),
                Insn(0x88, "MOVRM", AddrMode.Implied, []),
                Insn(0x80, subfield: 0, "ADD80", AddrMode.Implied, []),
            ];
        }

        public sealed partial class X86VocabCpu
        {
            private readonly IAddressSpace _bus;
            public X86VocabCpu(IAddressSpace bus) => _bus = bus;
            public void Reset() { }
            public void SetIrqLine(bool asserted) { }
            public void SetNmiLine(bool asserted) { }
            private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
            private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }
            private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;
}
