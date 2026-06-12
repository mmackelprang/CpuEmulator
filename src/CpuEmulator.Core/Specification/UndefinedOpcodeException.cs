namespace CpuEmulator.Core.Specification;

/// <summary>Raised by <see cref="UndefinedOpcodePolicy.Throw"/> when an unspecified opcode
/// is fetched. A guest-world event escalated to a host exception by policy
/// (<see cref="UndefinedOpcodePolicy.Throw"/>, the development default).</summary>
public sealed class UndefinedOpcodeException(byte opcode, uint address)
    : EmulationException($"Undefined opcode 0x{opcode:X2} at address 0x{address:X4}.")
{
    public byte Opcode { get; } = opcode;
    public uint Address { get; } = address;
}
