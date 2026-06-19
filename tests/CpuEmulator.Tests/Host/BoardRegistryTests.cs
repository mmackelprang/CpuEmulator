using CpuEmulator.Host;
using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Host;

public class BoardRegistryTests
{
    [Fact]
    public void Available_boards_lists_all_five_in_catalog_order()
    {
        Assert.Equal(
            new[] { "6502", "z80", "68000", "8086", "breadboard6502" },
            BoardRegistry.AvailableBoards);
    }

    [Fact]
    public void Default_board_is_6502()
    {
        Assert.Equal("6502", BoardRegistry.DefaultBoard);
    }

    [Theory]
    [InlineData("6502")]
    [InlineData("Z80")]          // case-insensitive
    [InlineData("68000")]
    [InlineData("8086")]
    [InlineData("breadboard6502")]
    public void TryBoot_builds_a_machine_for_each_known_name(string name)
    {
        bool ok = BoardRegistry.TryBoot(name, ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error);

        Assert.True(ok, error);
        Assert.NotNull(board);
        Assert.NotNull(board!.Machine);
        Assert.NotNull(board.Uart);
        Assert.False(string.IsNullOrWhiteSpace(board.Banner));
    }

    [Fact]
    public void TryBoot_rejects_an_unknown_name_with_a_clean_error()
    {
        bool ok = BoardRegistry.TryBoot("6809", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error);

        Assert.False(ok);
        Assert.Null(board);
        Assert.Contains("unknown board '6809'", error);
    }

    [Fact]
    public void TryBoot_on_the_jit_tier_also_builds()
    {
        bool ok = BoardRegistry.TryBoot("z80", ExecutionTier.Jit,
            out BootedBoard? board, out string? error);
        Assert.True(ok, error);
        Assert.NotNull(board);
    }
}
