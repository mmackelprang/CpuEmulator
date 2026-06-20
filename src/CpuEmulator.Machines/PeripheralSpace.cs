namespace CpuEmulator.Machines;

/// <summary>Which CPU bus a board region or peripheral slot lives on. Default <see cref="Program"/>
/// is the memory/program space (every existing board). <see cref="Io"/> is a separate I/O PORT space
/// — used by CPUs that have one (the Z80's IN/OUT port range). A board declares an I/O space by
/// setting <see cref="BoardSpec.IoAddressBits"/> and placing <see cref="Io"/> regions/slots in it.</summary>
public enum PeripheralSpace
{
    Program,
    Io,
}
