namespace CpuEmulator.Core;

/// <summary>
/// Per-CPU monitor adapter. Implementations are GENERATED from the ISA spec table (the same
/// table that generates the interpreter and disassembler), so the monitor's assembler,
/// disassembler, and length table can never drift from the executing CPU. The generated CPU
/// class implements this interface; a 6502 monitor is built over (cpu, bus, cpu).
/// </summary>
public interface IMonitorSupport
{
    /// <summary>Format one instruction as canonical assembly text (e.g. "LDA #$42",
    /// "BNE *-4"; "???" for undefined opcodes). Extra operand bytes are ignored.</summary>
    string Disassemble(byte opcode, byte operandLo, byte operandHi);

    /// <summary>Total instruction length in bytes (1-3), from the addressing mode.
    /// Undefined opcodes return 1 so a disassembly walk always advances.</summary>
    int InstructionLength(byte opcode);

    /// <summary>Assemble one instruction: mnemonic + canonical operand text → opcode bytes
    /// (the inverse of <see cref="Disassemble"/> — see the grammar table in the plan/docs).
    /// The operand's SHAPE selects the addressing mode; its hex-digit WIDTH selects
    /// zero-page (2 digits) vs absolute (4) — never promoted. A mnemonic+mode pair either
    /// exists in the spec table or assembly fails with a diagnostic in
    /// <paramref name="error"/>. Mnemonics/hex are case-insensitive; whitespace ignored.</summary>
    bool TryAssemble(string mnemonic, string operandText, out byte[] bytes, out string? error);

    /// <summary>True exactly when the next <see cref="ICpuCore.Step"/> will service a
    /// pending interrupt instead of executing the instruction at PC (monitor displays must
    /// say so). The hand-written CPU partial implements this with the same predicate its
    /// interrupt-service hook gates on.</summary>
    bool InterruptPending { get; }

    /// <summary>Name of the ProgramCounter-role register in
    /// <see cref="ICpuCore.RegisterNames"/>.</summary>
    string ProgramCounterName { get; }

    /// <summary>Architectural width in bits of a named register (display formatting).</summary>
    /// <exception cref="System.ArgumentException">The name is not a register.</exception>
    int RegisterBits(string name);
}
