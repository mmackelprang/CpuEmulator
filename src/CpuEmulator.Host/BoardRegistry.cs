using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Host;

/// <summary>
/// The host's catalog of bootable boards, keyed by lowercase name. Each entry builds a
/// validated <see cref="BoardSpec"/> (via <see cref="ReferenceSbc"/> or
/// <see cref="Breadboard6502Board"/>), compiles it to a <see cref="Machine"/> through
/// <see cref="BoardMachineFactory"/>, and returns a <see cref="BootedBoard"/> the host runs.
/// Adding a CPU is adding one row. The default board (no --board given) is "6502".
/// </summary>
public static class BoardRegistry
{
    /// <summary>The board selected when --board is omitted.</summary>
    public const string DefaultBoard = "6502";

    private static readonly string[] Names =
        ["6502", "z80", "68000", "8086", "breadboard6502"];

    /// <summary>The available board names, in catalog order, for --board list + usage text.</summary>
    public static IReadOnlyList<string> AvailableBoards => Names;

    /// <summary>
    /// Build and boot a board by name (case-insensitive). On success returns true with a
    /// <see cref="BootedBoard"/>; on an unknown name returns false with an error message.
    /// The caller resets the machine and wires the UART. <paramref name="tier"/> selects
    /// interpreter (default) or JIT.
    /// </summary>
    public static bool TryBoot(string name, ExecutionTier tier,
                               out BootedBoard? board, out string? error)
    {
        board = null;
        error = null;
        string key = name.Trim().ToLowerInvariant();
        switch (key)
        {
            case "6502":
            case "breadboard6502":
                board = BootBreadboard6502(tier);
                return true;
            case "z80":
                board = BootReferenceSbc(CpuKind.Z80, tier);
                return true;
            case "68000":
                board = BootReferenceSbc(CpuKind.M68000, tier);
                return true;
            case "8086":
                board = BootReferenceSbc(CpuKind.I8086, tier);
                return true;
            default:
                error = $"unknown board '{name}' (available: {string.Join(", ", Names)})";
                return false;
        }
    }

    private static BootedBoard BootBreadboard6502(ExecutionTier tier)
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        BoardSpec spec = Breadboard6502Board.Spec(BoardRoms.Mos6502Demo(), uart, timer);
        Machine machine = BoardMachineFactory.Build(spec, tier);
        return new BootedBoard(machine, uart, Banner.For(spec));
    }

    private static BootedBoard BootReferenceSbc(CpuKind cpu, ExecutionTier tier)
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        byte[] rom = cpu switch
        {
            CpuKind.Z80 => BoardRoms.Z80Boot(),
            CpuKind.M68000 => BoardRoms.M68000Boot(),
            CpuKind.I8086 => BoardRoms.I8086Boot(),
            _ => throw new System.NotSupportedException($"no host boot ROM for {cpu}"),
        };
        BoardSpec spec = ReferenceSbc.Build(cpu, uart, timer, rom);
        Machine machine = BoardMachineFactory.Build(spec, tier);
        BootedBoard board = new(machine, uart, Banner.For(spec));

        // The Z80 boots from RAM at $0000, so poke its program into RAM after the machine is
        // built (the ROM image is a recipe placeholder). The other CPUs boot from ROM directly.
        if (cpu == CpuKind.Z80)
        {
            IAddressSpace space = machine.Space(AddressSpaceKind.Program);
            byte[] program = BoardRoms.Z80BootProgram();
            for (int i = 0; i < program.Length; i++)
                space.Write8((uint)i, program[i]);
        }
        return board;
    }
}
