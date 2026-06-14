using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80CompoundDecodeTests
{
    // A synthetic spec declaring DD as a compound-with-CB, displacement-before-opcode prefix, plus ONE
    // compound row (the stub). The 2-prefix Insn overload names both DD and CB + the final opcode.
    private const string Spec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ddcb")]
        public static class DdcbSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status),
                new("WZ", 16), new("IX", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)],
                ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                // The compound stub row: DD CB d 7E. Bit mode (mnemonic-only disassembly). The Bit-class
                // op (CbBit) is the only op valid with AddrMode.Bit; the compound key is invariant to it.
                Insn(0xDD, 0xCB, 0x7E, "BIT", AddrMode.Bit, [CbBit("BIT", 7, "A")]),
            ];
        }
        """;

    // Same spec body as Spec, PLUS a hand-written partial class so CompileAndLoadType can build and
    // load the generated CPU type (the decode-walk test exercises the RUNTIME walk, not just text).
    // The partial mirrors SyntheticDecodeStructureTests' known-good shape.
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ddcb")]
        public static class DdcbSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status),
                new("WZ", 16), new("IX", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xDD, CompoundWith: 0xCB, DisplacementBeforeOpcode: true)],
                ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xDD, 0xCB, 0x7E, "BIT", AddrMode.Bit, [CbBit("BIT", 7, "A")]),
            ];
        }

        public sealed partial class DdcbCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public DdcbCpu(IAddressSpace bus) => _bus = bus;
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

    [Fact]
    public void Compound_row_emits_the_24bit_compound_key_in_table_and_dispatch()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        // The compound key is (0xDD << 16) | (0xCB << 8) | 0x7E = 0xDDCB7E. It must appear in the keyed
        // descriptor table, the Execute dispatch, and the Op-method name — all three must agree (B1).
        Assert.Contains("0xDDCB7E", result.GeneratedText);
        Assert.Contains("OpDDCB7E", result.GeneratedText);
    }

    [Fact]
    public void DD_CB_d_op_stream_decodes_to_compound_key_displacement_and_length_4()
    {
        var cpu = GeneratorTestHost.CompileAndLoadType(Source, "Demo.DdcbCpu");
        var decode = cpu.GetMethod("Decode", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(new byte[] { 0xDD, 0xCB, 0x05, 0x7E });
        var r = (CpuEmulator.Core.Jit.DecodeResult)decode.Invoke(null, new object[] { stream })!;
        Assert.Equal(0xDDCB7Eu, r.OperationKey);   // (0xDD<<16)|(0xCB<<8)|0x7E — the 24-bit compound key (B1)
        Assert.Equal(4, r.Length);                 // prefix + CB + displacement + opcode (B2)
        Assert.Equal((byte)0x05, r.Operands.Lo);   // the displacement d surfaced as the first operand
        Assert.Equal((byte)1, r.Operands.Count);

        // The descriptor's FixedLength is the Compound arm's 4 (Change 3).
        var descFor = cpu.GetMethod("DescriptorFor", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        var desc = (CpuEmulator.Core.Jit.OpcodeDescriptor)descFor.Invoke(null, new object[] { 0xDDCB7Eu })!;
        Assert.Equal(4, desc.FixedLength);
    }
}
