namespace CpuEmulator.Core.Jit;

/// <summary>A unit-granular fetch stream the decode walk reads through. Default unit = byte
/// (6502/Z80/8086). Word-capable for the 68000 (M4) — the walk never hardcodes Read8 (Ground
/// truth D). NextUnit advances the cursor by one unit and returns it zero-extended to a uint;
/// PeekUnit reads without advancing (the walk peeks the prefix/opcode to decide the table).</summary>
public interface IFetchStream
{
    /// <summary>Bytes per unit: 1 (byte-granular) or 2 (word-granular). The walk multiplies its
    /// unit count by this to get a byte Length.</summary>
    int UnitBytes { get; }

    /// <summary>Read the unit at the current cursor and advance the cursor by one unit.</summary>
    uint NextUnit();

    /// <summary>Read the unit at the current cursor WITHOUT advancing (lookahead).</summary>
    uint PeekUnit();

    /// <summary>How many units have been consumed so far (× UnitBytes = byte length).</summary>
    int UnitsConsumed { get; }
}

/// <summary>The generated per-CPU decode walk. Run from a stream positioned at an instruction's
/// first unit; consumes prefix/opcode/operand units per the spec's decode structure and returns
/// the (key, computed-length, operands) triple. ONE decode model the interpreter Step, the JIT
/// Discover, the disassembler, and InstructionLength all call — not four switch(opcode) sites.</summary>
public interface IDecoder
{
    DecodeResult Decode(IFetchStream stream);
}
