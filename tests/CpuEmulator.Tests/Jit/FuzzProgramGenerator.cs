using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Jit;

/// <summary>Deterministic, seeded generator of small SMC-biased 6502 programs for the differential
/// fuzzer (Task 7, Ground truth D). Same seed -&gt; same program + same initial state (no DateTime,
/// Guid, or unseeded Random). A tunable fraction of stores target the program's own code region (the
/// SMC bias) to exercise the chaining-sever + SMC-guard paths the M2-i bugs lived in. A divergence in
/// the differential fuzzer reproduces from the seed integer alone.</summary>
internal static class FuzzProgramGenerator
{
    public sealed record FuzzProgram(
        byte[] Ram,            // full 64 KiB image: code + the random initial RAM
        ushort StartPc,
        byte A, byte X, byte Y, byte S, byte P,
        int StoresToCode);     // for the SMC-bias self-test

    private const ushort CodeBase = 0x0200;
    private const int CodeLen = 0x0180;        // the code region (also the SMC target window)

    /// <summary>Generate a deterministic SMC-biased program for <paramref name="seed"/>.</summary>
    public static FuzzProgram Generate(int seed, double smcBias = 0.20)
    {
        var rng = new System.Random(seed);
        var ram = new byte[0x10000];
        // Random initial RAM in a scratch region (NOT the code region, NOT the stack page/vectors)
        // so reads return varied data without corrupting the program or the stack the run uses.
        for (int a = 0x4000; a < 0x8000; a++)
            ram[a] = (byte)rng.Next(256);

        int storesToCode = 0;
        int pc = CodeBase;
        // Emit instructions of implemented opcodes until the code window is nearly full; bias some
        // stores into the code region (SMC). Leave room (-4) for the terminal JMP-* park.
        while (pc < CodeBase + CodeLen - 4)
        {
            byte opcode = ImplementedOpcodes[rng.Next(ImplementedOpcodes.Length)];
            ram[pc++] = opcode;
            int len = Mos6502Cpu.InstructionLength(opcode);
            if (len >= 2)
            {
                // For a store opcode, bias the operand toward the code region (SMC).
                if (IsZeroPageStore(opcode) && rng.NextDouble() < smcBias)
                {
                    ram[pc++] = (byte)(CodeBase & 0xFF); storesToCode++;       // a zp store into code
                }
                else if (IsAbsoluteStore(opcode) && len == 3 && rng.NextDouble() < smcBias)
                {
                    ushort t = (ushort)(CodeBase + rng.Next(CodeLen));
                    ram[pc++] = (byte)(t & 0xFF); ram[pc++] = (byte)(t >> 8); storesToCode++;
                }
                else
                {
                    ram[pc++] = (byte)rng.Next(256);
                    if (len == 3) ram[pc++] = (byte)rng.Next(256);
                }
            }
        }

        // Park: JMP * so the run is bounded (the PC-unchanged trap idiom ends it).
        ram[pc] = 0x4C; ram[pc + 1] = (byte)(pc & 0xFF); ram[pc + 2] = (byte)(pc >> 8);

        // Initial registers: random A/X/Y; a sane stack pointer; P with the unused bit set + the
        // break bit clear (the interpreter's canonical P shape — matches RunInterpreter's P handling).
        byte a0 = (byte)rng.Next(256);
        byte x0 = (byte)rng.Next(256);
        byte y0 = (byte)rng.Next(256);
        byte p0 = (byte)((rng.Next(256) & 0xEF) | 0x20);
        return new FuzzProgram(ram, CodeBase, a0, x0, y0, S: 0xFD, P: p0, storesToCode);
    }

    /// <summary>The same implemented-opcode probe the TomHarte tests use (Disassemble != "???"),
    /// minus the opcodes that would run the program away or out of its bounded window: JSR/RTS/RTI/BRK
    /// (stack/vector machinery whose targets are not constrained to the code window) and JMP (which
    /// can jump anywhere — the terminal JMP-* park is appended explicitly instead). ADC/SBC ARE in the
    /// set (they emit after Task 5), so the fuzzer stresses the decimal arms too.</summary>
    public static readonly byte[] ImplementedOpcodes = BuildImplementedOpcodes();

    private static byte[] BuildImplementedOpcodes()
    {
        var list = new System.Collections.Generic.List<byte>();
        for (int op = 0; op <= 0xFF; op++)
        {
            byte opcode = (byte)op;
            if (Mos6502Cpu.Disassemble(opcode, 0, 0) == "???") continue;
            if (IsControlFlow(opcode)) continue;   // no runaway / out-of-window transfer
            list.Add(opcode);
        }
        return [.. list];
    }

    /// <summary>JMP (abs+ind), JSR, RTS, RTI, BRK, and the conditional branches — excluded so a
    /// generated program never transfers control outside its bounded code window (branches CAN stay
    /// in-window, but a backward branch can spin; excluding them keeps the run strictly forward to the
    /// terminal park, which is the bounded-run contract). Chaining + branch parity are pinned
    /// elsewhere (ChainingTests); the fuzzer's job is straight-line ALU/load/store/SMC stress.</summary>
    private static bool IsControlFlow(byte opcode) => opcode switch
    {
        0x4C or 0x6C => true,                          // JMP abs / JMP (ind)
        0x20 or 0x60 or 0x40 or 0x00 => true,          // JSR / RTS / RTI / BRK
        0x10 or 0x30 or 0x50 or 0x70 => true,          // BPL BMI BVC BVS
        0x90 or 0xB0 or 0xD0 or 0xF0 => true,          // BCC BCS BNE BEQ
        _ => false,
    };

    private static bool IsZeroPageStore(byte opcode) => opcode is 0x85 or 0x86 or 0x84;  // STA/STX/STY zp
    private static bool IsAbsoluteStore(byte opcode) => opcode is 0x8D or 0x8E or 0x8C;   // STA/STX/STY abs
}
