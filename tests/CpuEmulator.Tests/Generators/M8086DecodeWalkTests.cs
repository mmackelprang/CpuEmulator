using System.Reflection;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M5.2 (ADR 0006 Decision 1) — the x86 byte-granular VARIABLE-LENGTH decode walk, proven against
/// a SYNTHETIC x86-shaped CPU (NOT the shipped 8086 — the real M8086Spec stays state-only until M5.4) the
/// way M3.1b proved the prefix/ModR/M shape and M4.3a proved the field walk. The fixture's spec declares an
/// <c>X86DecodeStructure</c> exercising the four load-bearing properties the ADR names:
///   1. PREFIX-STACKING — 0..N segment-override / repeat / lock bytes are consumed before the opcode and
///      the length counts every one (26 2E F3 &lt;op&gt;);
///   2. the real ModR/M 16-bit DISPLACEMENT-LENGTH table — each (mod, r/m) row yields the right consumed
///      length, including the mod=00,r/m=110 ⇒ disp16 DIRECT exception;
///   3. the IMMEDIATE-LENGTH rule — a w=0 opcode consumes 1 imm byte, w=1 consumes 2, an s-bit sign-extend
///      form consumes 1;
///   4. OPCODE-GROUP keying — (opcode &lt;&lt; 3) | reg distinguishes the eight group operations;
/// plus the unknown-opcode → Undefined sentinel. The proof is the DECODE SHAPE (key + length), NOT operand
/// semantics or EA/segmentation (those are M5.3) — the synthetic CPU computes no real address.</summary>
public class M8086DecodeWalkTests
{
    // A synthetic x86-shaped CPU. Registers A/IP (a real 8086 needs no full register file to prove the
    // DECODE walk). The X86DecodeStructure declares: the segment-override (26/2E/36/3E) + repeat (F3/F2) +
    // lock (F0) prefixes; a handful of opcodes spanning every decode shape. Each opcode has a matching Insn
    // row (the parser cross-check requires it). The op bodies are trivial Implied/Register (the proof is the
    // walk's key + length).
    private const string X86DecodeCpuSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("x86decodetest")]
        public static class X86DecodeTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 16),
                new("IP", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly X86DecodeStructure Decode = new(
                Prefixes:
                [
                    new X86Prefix(0x26, X86PrefixRole.SegmentOverride),   // ES:
                    new X86Prefix(0x2E, X86PrefixRole.SegmentOverride),   // CS:
                    new X86Prefix(0x36, X86PrefixRole.SegmentOverride),   // SS:
                    new X86Prefix(0x3E, X86PrefixRole.SegmentOverride),   // DS:
                    new X86Prefix(0xF0, X86PrefixRole.Lock),              // LOCK
                    new X86Prefix(0xF2, X86PrefixRole.Repeat),            // REPNE
                    new X86Prefix(0xF3, X86PrefixRole.Repeat),            // REP/REPE
                ],
                Opcodes:
                [
                    new X86Opcode(0x90),                                            // NOP — plain, no ModR/M, no imm
                    new X86Opcode(0x88, HasModRm: true),                            // MOV r/m8,r8 — ModR/M, no imm
                    new X86Opcode(0x8B, HasModRm: true),                            // MOV r16,r/m16 — ModR/M, no imm
                    new X86Opcode(0x04, WBit: 0, Immediate: X86ImmediateRule.WBit), // ADD AL,imm8 (w=0 ⇒ 1)
                    new X86Opcode(0x05, WBit: 0, Immediate: X86ImmediateRule.WBit), // ADD AX,imm16 (w=1 ⇒ 2)
                    new X86Opcode(0xB0, WBit: 3, Immediate: X86ImmediateRule.WBit), // MOV AL,imm8 (w=0 ⇒ 1)
                    new X86Opcode(0xB8, WBit: 3, Immediate: X86ImmediateRule.WBit), // MOV AX,imm16 (w=1 ⇒ 2)
                    new X86Opcode(0xCD, Immediate: X86ImmediateRule.Fixed8),        // INT n — fixed imm8
                    new X86Opcode(0xE9, Immediate: X86ImmediateRule.Fixed16),       // JMP rel16 — fixed imm16
                    // The 80/81/83 ALU group: ModR/M + reg-extends-opcode + a w/s-driven immediate.
                    new X86Opcode(0x80, HasModRm: true, RegIsExtension: true, WBit: 0, Immediate: X86ImmediateRule.WBit),         // imm8  (w=0)
                    new X86Opcode(0x81, HasModRm: true, RegIsExtension: true, WBit: 0, Immediate: X86ImmediateRule.WBit),         // imm16 (w=1)
                    new X86Opcode(0x83, HasModRm: true, RegIsExtension: true, WBit: 0, SBit: 1, Immediate: X86ImmediateRule.SWBit), // imm8 sign-ext (s=1)
                    new X86Opcode(0xFE, HasModRm: true, RegIsExtension: true),      // INC/DEC group — ModR/M, no imm
                ]);

            // One matching Insn row per declared opcode (the cross-check requires each). The op bodies are
            // trivial (Register/Implied, no micro-ops) — the proof is the DECODE SHAPE, not semantics.
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0x90, "NOP",    AddrMode.Implied, []),
                Insn(0x88, "MOVRM",  AddrMode.Implied, []),
                Insn(0x8B, "MOVMR",  AddrMode.Implied, []),
                Insn(0x04, "ADDIB",  AddrMode.Implied, []),
                Insn(0x05, "ADDIW",  AddrMode.Implied, []),
                Insn(0xB0, "MOVIB",  AddrMode.Implied, []),
                Insn(0xB8, "MOVIW",  AddrMode.Implied, []),
                Insn(0xCD, "INT",    AddrMode.Implied, []),
                Insn(0xE9, "JMP",    AddrMode.Implied, []),
                // The group rows: reg field selects the operation (subfield 0..7). Two rows per group opcode
                // prove (opcode<<3)|reg distinguishes group members; the rest of the eight resolve Undefined.
                Insn(0x80, subfield: 0, "ADD80", AddrMode.Implied, []),
                Insn(0x80, subfield: 5, "SUB80", AddrMode.Implied, []),
                Insn(0x81, subfield: 0, "ADD81", AddrMode.Implied, []),
                Insn(0x83, subfield: 0, "ADD83", AddrMode.Implied, []),
                Insn(0xFE, subfield: 0, "INC",   AddrMode.Implied, []),
                Insn(0xFE, subfield: 1, "DEC",   AddrMode.Implied, []),
            ];
        }

        public sealed partial class X86DecodeTestCpu
        {
            private readonly IAddressSpace _bus;
            public X86DecodeTestCpu(IAddressSpace bus) => _bus = bus;
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
        new(() => GeneratorTestHost.CompileAndLoadType(X86DecodeCpuSpec, "SyntheticCpu.X86DecodeTestCpu"));

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

    // ── Property 1: prefix-stacking — 0..N prefix bytes are consumed and counted (ADR 0006 D1) ───────

    [Fact]
    public void Prefix_stacking_counts_every_prefix_byte_before_the_opcode()
    {
        // NOP (0x90) alone is 1 byte.
        Assert.Equal(1, Decode(0x90).Length);
        // One segment-override prefix (26 ES:) then NOP → 2 bytes.
        Assert.Equal(2, Decode(0x26, 0x90).Length);
        // Three stacked prefixes (26 ES: , 2E CS:, F3 REP) then NOP → 4 bytes — every prefix counts.
        Assert.Equal(4, Decode(0x26, 0x2E, 0xF3, 0x90).Length);
        // The key is still the OPCODE byte (the prefixes do not change which operation resolves).
        Assert.Equal(0x90u, Decode(0x26, 0x2E, 0xF3, 0x90).OperationKey);
        Assert.Equal("NOP", DescriptorFor(Decode(0x26, 0xF0, 0x90).OperationKey).Mnemonic);
    }

    [Fact]
    public void Prefix_stacking_composes_with_a_modrm_opcode_and_its_displacement()
    {
        // F3 (REP) + 88 (MOV r/m8,r8) with mod=01 (disp8): 1 prefix + 1 opcode + 1 modrm + 1 disp8 = 4.
        byte modrmMod01 = 0b01_000_000;   // mod=01, reg=000, r/m=000 → disp8
        Assert.Equal(4, Decode(0xF3, 0x88, modrmMod01, 0x12).Length);
    }

    // ── Property 2: the real ModR/M 16-bit displacement-length table (ADR 0006 D1 / D2) ──────────────

    [Theory]
    // mod  r/m   modrm byte      expected disp bytes (total = 1 opcode + 1 modrm + disp)
    [InlineData(0b00_000_000, 0)]   // mod=00, r/m=000 [BX+SI]      → 0 disp
    [InlineData(0b00_000_111, 0)]   // mod=00, r/m=111 [BX]         → 0 disp
    [InlineData(0b00_000_110, 2)]   // mod=00, r/m=110 disp16 DIRECT → 2 disp (THE EXCEPTION)
    [InlineData(0b01_000_000, 1)]   // mod=01, r/m=000             → 1 disp8
    [InlineData(0b01_000_110, 1)]   // mod=01, r/m=110 [BP+disp8]  → 1 disp8 (NOT the exception)
    [InlineData(0b10_000_000, 2)]   // mod=10, r/m=000             → 2 disp16
    [InlineData(0b10_000_110, 2)]   // mod=10, r/m=110 [BP+disp16] → 2 disp16
    [InlineData(0b11_000_000, 0)]   // mod=11 register direct      → 0 disp
    [InlineData(0b11_000_110, 0)]   // mod=11 register direct      → 0 disp
    public void ModRm_displacement_length_follows_the_8086_table(int modrm, int dispBytes)
    {
        // 0x88 (MOV r/m8,r8): no immediate, so length = 1 (opcode) + 1 (modrm) + dispBytes.
        var bytes = new byte[] { 0x88, (byte)modrm, 0, 0, 0 };
        Assert.Equal(2 + dispBytes, Decode(bytes).Length);
        // The ModR/M byte is surfaced on Operands.Lo (the proof-of-shape; full mod/reg/r/m EA is M5.3).
        Assert.Equal((byte)modrm, Decode(bytes).Operands.Lo);
        Assert.Equal((byte)1, Decode(bytes).Operands.Count);
    }

    [Fact]
    public void ModRm_mod00_rm110_is_the_disp16_direct_exception_specifically()
    {
        // The famous exception: mod=00 normally has NO displacement, but r/m=110 is disp16 DIRECT.
        int withoutException = Decode(0x88, 0b00_000_101, 0, 0).Length;  // mod=00, r/m=101 [DI] → 0 disp
        int withException    = Decode(0x88, 0b00_000_110, 0, 0).Length;  // mod=00, r/m=110 disp16 → 2 disp
        Assert.Equal(2, withoutException);   // opcode + modrm
        Assert.Equal(4, withException);      // opcode + modrm + disp16
        Assert.Equal(2, withException - withoutException);   // the exception adds exactly 2 bytes
    }

    // ── Property 3: the immediate-length rule (w / s bit driven) (ADR 0006 D1) ───────────────────────

    [Fact]
    public void Immediate_w_bit_drives_1_or_2_bytes()
    {
        // ADD AL,imm8 (0x04, w bit 0 = 0) → 1 imm byte → length 2.
        Assert.Equal(2, Decode(0x04, 0xAA).Length);
        // ADD AX,imm16 (0x05, w bit 0 = 1) → 2 imm bytes → length 3.
        Assert.Equal(3, Decode(0x05, 0xAA, 0xBB).Length);
        // MOV AL,imm8 (0xB0, w bit 3 = 0) → 1 imm byte → length 2.
        Assert.Equal(2, Decode(0xB0, 0xAA).Length);
        // MOV AX,imm16 (0xB8, w bit 3 = 1) → 2 imm bytes → length 3.
        Assert.Equal(3, Decode(0xB8, 0xAA, 0xBB).Length);
    }

    [Fact]
    public void Immediate_fixed_rules_ignore_the_w_bit()
    {
        // INT n (0xCD) — a FIXED imm8 → length 2.
        Assert.Equal(2, Decode(0xCD, 0x21).Length);
        // JMP rel16 (0xE9) — a FIXED imm16 → length 3.
        Assert.Equal(3, Decode(0xE9, 0x34, 0x12).Length);
    }

    [Fact]
    public void Immediate_sign_extend_form_consumes_one_byte()
    {
        // The 80/81/83 ALU group, mod=11 (register direct, no displacement), reg=0 (ADD):
        //   0x80 (s=0, w=0): imm8  → 1 + 1 modrm + 1 imm  = 3
        //   0x81 (s=0, w=1): imm16 → 1 + 1 modrm + 2 imm  = 4
        //   0x83 (s=1, w=1): imm8 sign-extended → 1 + 1 modrm + 1 imm = 3 (the SWBit rule)
        byte modrmReg0 = 0b11_000_000;   // mod=11, reg=000 (ADD), r/m=000
        Assert.Equal(3, Decode(0x80, modrmReg0, 0xAA).Length);
        Assert.Equal(4, Decode(0x81, modrmReg0, 0xAA, 0xBB).Length);
        Assert.Equal(3, Decode(0x83, modrmReg0, 0xAA).Length);   // s-bit → imm8, NOT imm16
    }

    // ── Property 4: opcode-group keying — (opcode << 3) | reg distinguishes the group members ─────────

    [Fact]
    public void Opcode_group_key_packs_opcode_and_reg_field()
    {
        // 0x80 group, reg=0 (ADD) vs reg=5 (SUB) — same opcode byte, DIFFERENT operation.
        byte modrmReg0 = 0b11_000_000;   // mod=11, reg=000
        byte modrmReg5 = 0b11_101_000;   // mod=11, reg=101
        DecodeResult add = Decode(0x80, modrmReg0, 0xAA);
        DecodeResult sub = Decode(0x80, modrmReg5, 0xAA);
        Assert.Equal((0x80u << 3) | 0u, add.OperationKey);
        Assert.Equal((0x80u << 3) | 5u, sub.OperationKey);
        Assert.Equal("ADD80", DescriptorFor(add.OperationKey).Mnemonic);
        Assert.Equal("SUB80", DescriptorFor(sub.OperationKey).Mnemonic);
        Assert.NotEqual(add.OperationKey, sub.OperationKey);
    }

    [Fact]
    public void Opcode_group_without_immediate_keys_on_reg()
    {
        // 0xFE (INC/DEC) group, no immediate: reg=0 (INC) vs reg=1 (DEC), mod=11 register direct.
        DecodeResult inc = Decode(0xFE, 0b11_000_000);   // reg=000 → INC
        DecodeResult dec = Decode(0xFE, 0b11_001_000);   // reg=001 → DEC
        Assert.Equal((0xFEu << 3) | 0u, inc.OperationKey);
        Assert.Equal((0xFEu << 3) | 1u, dec.OperationKey);
        Assert.Equal("INC", DescriptorFor(inc.OperationKey).Mnemonic);
        Assert.Equal("DEC", DescriptorFor(dec.OperationKey).Mnemonic);
        Assert.Equal(2, inc.Length);   // opcode + modrm (no disp at mod=11, no imm)
    }

    [Fact]
    public void A_modrm_opcode_carries_LengthRule_ModRmDetermined()
    {
        // The ModR/M rows compute their length from the mid-stream byte — LengthRule.ModRmDetermined, not
        // Fixed. (0x88 plain ModR/M; the 0x80-group ModR/M rows too.)
        Assert.Equal(LengthRule.ModRmDetermined, DescriptorFor(0x88u).LengthRule);
        Assert.Equal(LengthRule.ModRmDetermined, DescriptorFor((0x80u << 3) | 0u).LengthRule);
        // A non-ModR/M opcode stays Fixed.
        Assert.Equal(LengthRule.Fixed, DescriptorFor(0x90u).LengthRule);
    }

    // ── Unknown opcode → the Undefined sentinel ───────────────────────────────────────────────────────

    [Fact]
    public void Unknown_opcode_resolves_to_the_Undefined_sentinel()
    {
        // 0x07 is not declared. The walk still returns a (key, length) — DescriptorFor yields Undefined.
        DecodeResult r = Decode(0x07);
        Assert.Equal(JitOpClass.Undefined, DescriptorFor(r.OperationKey).Class);
        // An unmapped GROUP member (0x80 reg=2, which has no Insn row) is Undefined too.
        DecodeResult g = Decode(0x80, 0b11_010_000, 0xAA);   // reg=010 → no row
        Assert.Equal(JitOpClass.Undefined, DescriptorFor(g.OperationKey).Class);
    }

    // ── Discovery advances by the COMPUTED length (the JIT-side half — M3.1b/M4.3a precedent) ─────────

    [Fact]
    public void Decode_advances_by_the_computed_length_over_a_full_instruction()
    {
        // 26 (ES:) 8B (MOV r16,r/m16) mod=10 r/m=000 (disp16) — 1 prefix + 1 opcode + 1 modrm + 2 disp = 5.
        byte[] program = [0x26, 0x8B, 0b10_000_000, 0x34, 0x12, 0x90];
        int firstLen = Decode(program).Length;
        Assert.Equal(5, firstLen);
        // pc += computed length lands on the trailing NOP.
        DecodeResult next = Decode(program[firstLen..]);
        Assert.Equal(0x90u, next.OperationKey);
    }

    // ── The abstraction generates clean + the x86 arm is emitted ──────────────────────────────────────

    [Fact]
    public void Synthetic_x86_spec_generates_a_compiling_class()
    {
        var result = GeneratorTestHost.Run(X86DecodeCpuSpec);
        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n", result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
        Assert.Contains("partial class X86DecodeTestCpu", result.GeneratedText);
        // The x86 walk + the keyed resolver are emitted (the new arm, not the byte/field walk).
        Assert.Contains("s_x86Prefixes", result.GeneratedText);
        Assert.Contains("s_x86HasModRm", result.GeneratedText);
        Assert.Contains("JitDescriptorsByKey", result.GeneratedText);
    }
}
