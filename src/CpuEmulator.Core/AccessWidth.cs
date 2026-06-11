namespace CpuEmulator.Core;

/// <summary>Width of a single bus access, in bytes.</summary>
public enum AccessWidth : byte
{
    Byte = 1,
    Word = 2,
    Long = 4,
}
