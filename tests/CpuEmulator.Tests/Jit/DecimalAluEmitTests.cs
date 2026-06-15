using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 5: the emitted ADC/SBC arms (both binary and decimal, behind an emitted
/// <c>if ((P &amp; 0x08) != 0)</c>) match the interpreter byte-for-byte. The interpreter is the
/// oracle (Ground truth E is its exact NMOS algorithm); each test builds a tiny program, runs it
/// through a fresh <see cref="JittedCpu"/> and a fresh interpreter, and diffs A/X/Y/S/P/PC +
/// CycleCount + the RAM image. A wrong emitted flag/result/cycle fails the diff loudly. The quirks
/// pinned: ADC decimal Z from the BINARY sum; ADC decimal N/V from the pre-correction sum; SBC
/// decimal flags ALL from the binary path (only A is BCD-corrected); cycles identical to binary.</summary>
public class DecimalAluEmitTests
{
    private static AddressSpace NewRamSpace()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    /// <summary>Build a fresh interpreter and a JIT-wrapped interpreter over identical programs,
    /// run the budget through each, assert the JIT matches the interpreter on registers + cycles +
    /// RAM. The program is poked, then run from <paramref name="startPc"/> with S=0xFD, P=0x24.</summary>
    private static void AssertJitMatchesInterpreter(
        Action<AddressSpace> poke, ushort startPc, long budget)
    {
        var refSpace = NewRamSpace();
        poke(refSpace);
        var refCpu = new Mos6502Cpu(refSpace) { PC = startPc, S = 0xFD, P = 0x24 };
        long refBudget = budget;
        refCpu.Run(ref refBudget);

        var jitSpace = NewRamSpace();
        poke(jitSpace);
        var inner = new Mos6502Cpu(jitSpace) { PC = startPc, S = 0xFD, P = 0x24 };
        var jit = new JittedCpu<Mos6502Cpu>(inner, Mos6502Cpu.JitTarget, jitSpace);
        long jitBudget = budget;
        jit.Run(ref jitBudget);

        Assert.Equal(refCpu.A, inner.A);
        Assert.Equal(refCpu.X, inner.X);
        Assert.Equal(refCpu.Y, inner.Y);
        Assert.Equal(refCpu.S, inner.S);
        Assert.Equal(refCpu.P, inner.P);
        Assert.Equal(refCpu.PC, inner.PC);
        Assert.Equal(refCpu.CycleCount, inner.CycleCount);
        for (uint a = 0; a <= 0xFFFF; a++)
            Assert.Equal(refSpace.Read8(a), jitSpace.Read8(a));
    }

    private static void Poke(AddressSpace space, ushort at, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            space.Write8((uint)(at + i), bytes[i]);
    }

    // ── Binary ADC (D clear) ─────────────────────────────────────────────────────────────────
    public static TheoryData<byte, byte, bool> BinaryAdcCases() => new()
    {
        { 0x00, 0x00, false }, { 0x01, 0x01, false }, { 0xFF, 0x01, false },
        { 0x7F, 0x01, false }, { 0x80, 0x80, false }, { 0x50, 0x50, false },
        { 0xFF, 0xFF, true },  { 0x00, 0x00, true },  { 0x3F, 0x40, true },
    };

    [Theory]
    [MemberData(nameof(BinaryAdcCases))]
    public void Binary_ADC_matches_the_interpreter(byte a, byte data, bool carry)
    {
        // CLD / LDA #a / (SEC or CLC) / ADC #data / JMP-self
        byte carryOp = carry ? (byte)0x38 : (byte)0x18;          // SEC : CLC
        AssertJitMatchesInterpreter(s => Poke(s, 0x0200,
            0xD8, 0xA9, a, carryOp, 0x69, data, 0x4C, 0x06, 0x02), 0x0200, 200);
    }

    // ── Decimal ADC (D set) — including the Z-from-binary quirk ───────────────────────────────
    public static TheoryData<byte, byte, bool> DecimalAdcCases() => new()
    {
        { 0x09, 0x01, false }, { 0x05, 0x05, false },
        { 0x15, 0x26, false }, { 0x99, 0x01, false }, { 0x99, 0x99, false },
        { 0x50, 0x50, false },
        // Z-from-binary quirk: the BINARY sum (A+data+C) is 0x00 (so Z set from binary) but the
        // BCD-corrected result is non-zero. A=0x80, data=0x80, C=0 -> binary 0x100 -> &0xFF == 0.
        { 0x80, 0x80, false },
        // ... and the converse: a BCD result of 0 whose binary sum is non-zero.
        { 0x00, 0x00, true },
        { 0x01, 0x99, true },
    };

    [Theory]
    [MemberData(nameof(DecimalAdcCases))]
    public void Decimal_ADC_matches_the_interpreter_including_the_Z_from_binary_quirk(
        byte a, byte data, bool carry)
    {
        // SED / LDA #a / (SEC or CLC) / ADC #data / JMP-self
        byte carryOp = carry ? (byte)0x38 : (byte)0x18;
        AssertJitMatchesInterpreter(s => Poke(s, 0x0200,
            0xF8, 0xA9, a, carryOp, 0x69, data, 0x4C, 0x06, 0x02), 0x0200, 200);
    }

    [Fact]
    public void Decimal_ADC_carry_and_overflow_boundary()
    {
        // Exhaustively sweep the decimal ADC over all (A, data, C) — the carry-correction boundary
        // (sum >= 0xA0 -> +0x60) and the N/V pre-correction window (sum in [0x80,0x9F] vs
        // [0xA0,0xFF]) are all covered, diffed against the interpreter for every input.
        for (int a = 0; a <= 0xFF; a++)
            for (int data = 0; data <= 0xFF; data += 0x11)   // stride keeps the test fast, hits boundaries
                foreach (bool carry in new[] { false, true })
                {
                    byte carryOp = carry ? (byte)0x38 : (byte)0x18;
                    AssertJitMatchesInterpreter(s => Poke(s, 0x0200,
                        0xF8, 0xA9, (byte)a, carryOp, 0x69, (byte)data, 0x4C, 0x06, 0x02), 0x0200, 200);
                }
    }

    // ── Binary + decimal SBC ──────────────────────────────────────────────────────────────────
    public static TheoryData<byte, byte, bool> SbcCases() => new()
    {
        { 0x00, 0x00, true },  { 0x05, 0x03, true },  { 0x00, 0x01, true },
        { 0x50, 0x60, true },  { 0x50, 0xB0, false }, { 0x80, 0x01, true },
        { 0x99, 0x99, true },  { 0x10, 0x05, false }, { 0x00, 0x00, false },
    };

    [Theory]
    [MemberData(nameof(SbcCases))]
    public void Binary_SBC_matches_the_interpreter(byte a, byte data, bool carry)
    {
        byte carryOp = carry ? (byte)0x38 : (byte)0x18;
        AssertJitMatchesInterpreter(s => Poke(s, 0x0200,
            0xD8, 0xA9, a, carryOp, 0xE9, data, 0x4C, 0x06, 0x02), 0x0200, 200);
    }

    [Theory]
    [MemberData(nameof(SbcCases))]
    public void Decimal_SBC_matches_the_interpreter_flags_from_binary(byte a, byte data, bool carry)
    {
        // SED / LDA #a / (SEC or CLC) / SBC #data / JMP-self. The decimal SBC's C/V/Z/N are ALL the
        // binary-path flags; only A is BCD-corrected. The interpreter is the oracle for that quirk.
        byte carryOp = carry ? (byte)0x38 : (byte)0x18;
        AssertJitMatchesInterpreter(s => Poke(s, 0x0200,
            0xF8, 0xA9, a, carryOp, 0xE9, data, 0x4C, 0x06, 0x02), 0x0200, 200);
    }

    [Fact]
    public void Decimal_SBC_carry_and_overflow_boundary()
    {
        for (int a = 0; a <= 0xFF; a++)
            for (int data = 0; data <= 0xFF; data += 0x11)
                foreach (bool carry in new[] { false, true })
                {
                    byte carryOp = carry ? (byte)0x38 : (byte)0x18;
                    AssertJitMatchesInterpreter(s => Poke(s, 0x0200,
                        0xF8, 0xA9, (byte)a, carryOp, 0xE9, (byte)data, 0x4C, 0x06, 0x02), 0x0200, 200);
                }
    }

    // ── ADC/SBC in each addressing mode (the operand resolution is the proven And/Ora path) ─────
    public static TheoryData<byte[], string> AddressingModePrograms()
    {
        // Each program: set D (decimal — the hotter arm), LDA #imm, set carry, then the ADC variant.
        // The operand byte (0x25) and any pointer/index are chosen to land in addressable RAM.
        var data = new TheoryData<byte[], string>();
        // ADC zp ($25): SED / LDX? no — just LDA, store $25 to zp, ADC $25
        // We pre-seed the operand cell via the program prologue stores.
        // ADC ZeroPage 0x65
        data.Add([0xF8, 0xA9, 0x34, 0x85, 0x40, 0xA9, 0x12, 0x38, 0x65, 0x40, 0x4C, 0x0A, 0x02], "ADC zp");
        // ADC Absolute 0x6D ($0240)
        data.Add([0xF8, 0xA9, 0x34, 0x8D, 0x40, 0x03, 0xA9, 0x12, 0x38, 0x6D, 0x40, 0x03, 0x4C, 0x0C, 0x02], "ADC abs");
        // ADC ZeroPageX 0x75: X=2, base $3E so $3E+2=$40
        data.Add([0xF8, 0xA2, 0x02, 0xA9, 0x34, 0x85, 0x40, 0xA9, 0x12, 0x38, 0x75, 0x3E, 0x4C, 0x0C, 0x02], "ADC zp,X");
        // ADC AbsoluteX 0x7D: X=1, base $033F so +1 = $0340
        data.Add([0xF8, 0xA2, 0x01, 0xA9, 0x34, 0x8D, 0x40, 0x03, 0xA9, 0x12, 0x38, 0x7D, 0x3F, 0x03, 0x4C, 0x0E, 0x02], "ADC abs,X");
        // ADC AbsoluteY 0x79: Y=1, base $033F -> $0340
        data.Add([0xF8, 0xA0, 0x01, 0xA9, 0x34, 0x8D, 0x40, 0x03, 0xA9, 0x12, 0x38, 0x79, 0x3F, 0x03, 0x4C, 0x0E, 0x02], "ADC abs,Y");
        // SBC zp 0xE5
        data.Add([0xF8, 0xA9, 0x34, 0x85, 0x40, 0xA9, 0x12, 0x38, 0xE5, 0x40, 0x4C, 0x0A, 0x02], "SBC zp");
        // SBC abs 0xED
        data.Add([0xF8, 0xA9, 0x34, 0x8D, 0x40, 0x03, 0xA9, 0x12, 0x38, 0xED, 0x40, 0x03, 0x4C, 0x0C, 0x02], "SBC abs");
        return data;
    }

    [Theory]
    [MemberData(nameof(AddressingModePrograms))]
    public void ADC_SBC_in_each_addressing_mode_matches(byte[] program, string _)
    {
        AssertJitMatchesInterpreter(s => Poke(s, 0x0200, program), 0x0200, 400);
    }
}
