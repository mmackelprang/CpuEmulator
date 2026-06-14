using System.Reflection;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Tests.Generators;

/// <summary>Task 7 (Ground truths F + G) — the M3 thesis made testable. A GENERATOR/JIT fixture
/// (NOT a shipped CPU, NOT the Z80, NOT the 8086) whose spec declares a DecodeStructure exercising
/// ALL THREE decoder properties the 8086 brief §10.1 names: a PREFIX byte (property 1+2), a
/// length-determining mid-stream byte (property 1 — genuine variable COMPUTED length), and a
/// non-first-byte sub-field key (property 3); plus the fetch-unit word micro-proof (Ground truth D).
/// Modeled on the M3.1a SyntheticRegisterSetTests precedent. The proof is the DECODE SHAPE (key +
/// length), not operand semantics — the synthetic CPU computes NO real EA (scope honesty).</summary>
public class SyntheticDecodeStructureTests
{
    // The Ground truth F.1 spec, authoritative spelling. Registers A/PC; a DecodeStructure declaring
    // prefix 0xCB, ModRm opcode 0x80, sub-field opcode 0xF6; the six rows (A) NOP, (B) PFXOP,
    // (C) BARE, (D) MODRMOP, (E) GRP0/GRP2. The 6502 declares NO DecodeStructure — this fixture is
    // the only user of the new Insn overloads.
    private const string DecodeTestCpuSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("decodetest")]
        public static class DecodeTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];

            // Decode structure: one prefix byte (0xCB); 0x80 carries a length-determining mid-stream
            // byte; 0xF6 is an opcode group keyed on a non-first-byte sub-field.
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xCB)],
                ModRmOpcodes: [0x80],
                SubFieldOpcodes: [0xF6]);

            // The synthetic ops are trivial (Implied, no micro-ops) — the proof is the DECODE SHAPE
            // (key + length), NOT operand semantics (Ground truth F scope honesty). Implied requires
            // no operand bytes, so every row's fixed length comes purely from its key bytes.
            public static readonly InstructionDef[] Instructions =
            [
                // (A) DEGENERATE — a plain single-byte op (the 6502 shape; key == opcode, length 1).
                Insn(0xEA, "NOP", AddrMode.Implied, []),

                // (B) PROPERTY 1+2 — a PREFIXED, MULTI-BYTE opcode (0xCB 0x10).
                Insn(0xCB, 0x10, "PFXOP", AddrMode.Implied, []),

                // (C) the UNPREFIXED 0x10 — same opcode byte, DIFFERENT operation.
                Insn(0x10, "BARE", AddrMode.Implied, []),

                // (D) PROPERTY 1 (mid-stream length byte) — 0x80 reads ONE more byte whose low 2 bits
                //     == disp-count; length is COMPUTED (1 opcode + 1 modrm + dispCount).
                Insn(0x80, "MODRMOP", AddrMode.Implied, []),

                // (E) PROPERTY 3 — 0xF6 is an opcode GROUP selected by bits 5-3 of the NEXT byte.
                Insn(0xF6, subfield: 0, "GRP0", AddrMode.Implied, []),
                Insn(0xF6, subfield: 2, "GRP2", AddrMode.Implied, []),
            ];
        }

        public sealed partial class DecodeTestCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;   // M3.4c: every structured-CPU op now sets Q (Q=F / Q=0) — declare the field
            public DecodeTestCpu(IAddressSpace bus) => _bus = bus;
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

    // Cache the compiled+loaded generated type (compilation is expensive).
    private static readonly Lazy<Type> s_cpu =
        new(() => GeneratorTestHost.CompileAndLoadType(DecodeTestCpuSpec, "SyntheticCpu.DecodeTestCpu"));

    private static DecodeResult Decode(params byte[] bytes)
    {
        var stream = new BufferFetchStream(bytes);
        var method = s_cpu.Value.GetMethod("Decode", BindingFlags.Public | BindingFlags.Static)!;
        return (DecodeResult)method.Invoke(null, [stream])!;
    }

    private static OpcodeDescriptor DescriptorFor(uint key)
    {
        var method = s_cpu.Value.GetMethod("DescriptorFor", BindingFlags.Public | BindingFlags.Static)!;
        return (OpcodeDescriptor)method.Invoke(null, [key])!;
    }

    // ── Property 1: length is a COMPUTED OUTPUT of the walk (8086 §10.1, 789-796) ────────────

    [Fact]
    public void Property1_MODRMOP_length_is_computed_from_the_midstream_byte()
    {
        // Same opcode (0x80), DIFFERENT length, set by the mid-stream byte's low 2 bits.
        // 0x02 → 2 tail bytes → length 1 (opcode) + 1 (modrm) + 2 = 4.
        Assert.Equal(4, Decode(0x80, 0x02, 0x00, 0x00).Length);
        // 0x00 → 0 tail bytes → length 2.
        Assert.Equal(2, Decode(0x80, 0x00).Length);
        // 0x01 → 1 tail byte → length 3 (proves it is genuinely VARIABLE, not two cases).
        Assert.Equal(3, Decode(0x80, 0x01, 0x00).Length);
        // The ModR/M row carries LengthRule.ModRmDetermined (not Fixed) — the seam under test.
        Assert.Equal(LengthRule.ModRmDetermined, DescriptorFor(0x80).LengthRule);
    }

    // ── Property 1+2: a multi-byte opcode via a prefix (8086 §10.1, 798-806; 0001 117-119) ────

    [Fact]
    public void Property1and2_prefixed_differs_from_unprefixed()
    {
        DecodeResult pfx = Decode(0xCB, 0x10);
        Assert.Equal(0xCB10u, pfx.OperationKey);   // (0xCB << 8) | 0x10
        Assert.Equal(2, pfx.Length);
        Assert.Equal("PFXOP", DescriptorFor(pfx.OperationKey).Mnemonic);

        DecodeResult bare = Decode(0x10);
        Assert.Equal(0x10u, bare.OperationKey);
        Assert.Equal(1, bare.Length);
        Assert.Equal("BARE", DescriptorFor(bare.OperationKey).Mnemonic);

        // The SAME second byte (0x10) resolves to DIFFERENT operations prefixed vs. unprefixed —
        // the case a single 256-table cannot express (0001-…:119).
        Assert.NotEqual(pfx.OperationKey, bare.OperationKey);
    }

    // ── Property 3: the key includes a sub-field of a NON-FIRST byte (8086 §10.1, 807-810) ────

    [Fact]
    public void Property3_subfield_of_a_nonfirst_byte_selects_the_operation()
    {
        // bits 5-3 of the second byte select the group member. 0b00_000_000 → sub-field 0 → GRP0.
        DecodeResult g0 = Decode(0xF6, 0b00_000_000);
        Assert.Equal((0xF6u << 3) | 0, g0.OperationKey);
        Assert.Equal("GRP0", DescriptorFor(g0.OperationKey).Mnemonic);

        // 0b00_010_000 → sub-field 2 → GRP2. Same opcode byte (0xF6), different operation.
        DecodeResult g2 = Decode(0xF6, 0b00_010_000);
        Assert.Equal((0xF6u << 3) | 2, g2.OperationKey);
        Assert.Equal("GRP2", DescriptorFor(g2.OperationKey).Mnemonic);

        Assert.NotEqual(g0.OperationKey, g2.OperationKey);
    }

    // ── Degenerate: the 6502 case (key == opcode, fixed length) (0001-…:158-159) ──────────────

    [Fact]
    public void Degenerate_NOP_is_the_6502_walk()
    {
        DecodeResult r = Decode(0xEA);
        Assert.Equal(0xEAu, r.OperationKey);
        Assert.Equal(1, r.Length);
        Assert.Equal("NOP", DescriptorFor(r.OperationKey).Mnemonic);
    }

    // ── Fetch-unit parameterization (byte vs word) (68000-…:747-750) ──────────────────────────

    [Fact]
    public void FetchUnit_word_makes_length_two_times_units()
    {
        // A word stream (UnitBytes == 2) over a 1-unit op returns Length == 2 — the walk does not
        // assume bytes (Ground truth D). The opcode-byte path reads exactly one unit for NOP (0xEA),
        // so the COMPUTED length is 1 unit × 2 bytes = 2.
        var stream = new BufferFetchStream(new byte[] { 0xEA, 0x00 }, unitBytes: 2);
        var method = s_cpu.Value.GetMethod("Decode", BindingFlags.Public | BindingFlags.Static)!;
        var r = (DecodeResult)method.Invoke(null, [stream])!;

        Assert.Equal(2, r.Length);
    }

    // ── JIT-reachable proof (Ground truth F.3 — the JIT-side half of property 1) ─────────────

    [Fact]
    public void Discover_advances_by_the_computed_length_over_MODRMOP()
    {
        // Recorded judgement call (same posture as M3.1a Task 6 Step 3): wiring a SECOND generated
        // CPU type through the live BlockCompiler requires J1 (typeof(Mos6502Cpu) is baked, deferred
        // to M3.5), so this drives the generated Decode walk DIRECTLY — the load-bearing floor the
        // plan names. It simulates discovery's cursor advance: Discover does `pc += r.Length`, so a
        // walk that returns a COMPUTED length proves discovery advances by the walk's output, NOT a
        // static d.Length field.
        //
        // A run starting at MODRMOP (0x80) with mid-stream byte 0x02 (disp-count 2) computes a length
        // of 4; mid-stream 0x00 computes 2. The discovery cursor would advance by exactly those.
        Assert.Equal(4, Decode(0x80, 0x02, 0xAA, 0xBB, 0xEA).Length);  // advance to the 5th byte (the NOP)
        Assert.Equal(2, Decode(0x80, 0x00, 0xEA).Length);              // advance to the 3rd byte (the NOP)

        // Confirm the advanced cursor lands on the next instruction: re-decoding from the computed
        // offset resolves the trailing NOP — i.e. pc += r.Length is the correct successor PC.
        byte[] program = [0x80, 0x02, 0xAA, 0xBB, 0xEA];
        int firstLen = Decode(program).Length;            // 4
        DecodeResult next = Decode(program[firstLen..]);  // decode at pc + computed length
        Assert.Equal(0xEAu, next.OperationKey);           // the NOP — discovery advanced correctly
    }

    // ── The abstraction generates clean (M3.1a precedent posture) ─────────────────────────────

    [Fact]
    public void Synthetic_spec_generates_a_compiling_class()
    {
        var result = GeneratorTestHost.Run(DecodeTestCpuSpec);

        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n", result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
        Assert.Contains("partial class DecodeTestCpu", result.GeneratedText);
        // The structured walk + the keyed resolver are emitted (not the degenerate dense-array path).
        Assert.Contains("JitDescriptorsByKey", result.GeneratedText);
        Assert.Contains("s_prefixBytes", result.GeneratedText);
    }

    [Fact]
    public void A_prefix_with_no_prefixed_row_reports_CPUGEN012()
    {
        // A malformed decode structure (a declared prefix byte that backs NO prefixed Insn row) is a
        // CPUGEN012 — the cross-check that keeps the structure and the instruction table consistent.
        string source = GeneratorTestHost.ReplaceSection(
            DecodeTestCpuSpec,
            "Prefixes: [new PrefixByte(0xCB)],",
            "Prefixes: [new PrefixByte(0xDD)],");   // 0xDD has no prefixed row

        var result = GeneratorTestHost.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN012");
    }
}
