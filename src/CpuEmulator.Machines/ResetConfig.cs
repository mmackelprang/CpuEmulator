namespace CpuEmulator.Machines;

/// <summary>A single byte written into a ROM image (before mapping) to seed a reset/interrupt
/// vector. Address is an absolute bus address; it must land inside a mapped region.</summary>
public sealed record VectorPatch(uint Address, byte Value);

/// <summary>Board-level reset inputs. The per-CPU reset MECHANICS live in the core's Reset()
/// (the 6502 reads $FFFC/$FFFD; the Z80 sets PC=0), so this record carries only the optional
/// vector bytes a board pokes into its ROM image when the image does not already embed them.
/// Most boards (whose ROM image carries its own vectors, e.g. the breadboard demo ROM) use None.</summary>
public sealed record ResetConfig(IReadOnlyList<VectorPatch> VectorPatches)
{
    /// <summary>No board-level vector patches (the ROM image carries its own vectors, or the CPU
    /// resets to a fixed PC needing no vector — e.g. the Z80's PC=0).</summary>
    public static ResetConfig None { get; } = new([]);
}
