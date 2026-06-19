using CpuEmulator.Core;
using CpuEmulator.Host;
using CpuEmulator.Machines;

namespace CpuEmulator.Tests.Host;

/// <summary>The breadboard6502 memory-map facts, now proven through the host's registry boot
/// path (BoardRegistry.TryBoot("6502", ...)) rather than the retired hand-wired Breadboard6502
/// class. Same board-spec (Breadboard6502Board.Spec + DemoRom), same assertions: RAM r/w, the
/// UART STATUS/DATA mirrors, the open-bus hole, the timer page + mirrors, and ROM write-protect.</summary>
public class Breadboard6502Tests
{
    private static BootedBoard Boot()
    {
        Assert.True(BoardRegistry.TryBoot("6502", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error), error);
        return board!;
    }

    [Fact]
    public void Ram_read_write_at_zero_page()
    {
        BootedBoard board = Boot();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        space.Write8(0x0000, 0xAB);

        Assert.Equal(0xAB, space.Read8(0x0000));
    }

    [Fact]
    public void Ram_read_write_at_cfff()
    {
        BootedBoard board = Boot();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        space.Write8(0xCFFF, 0xCD);

        Assert.Equal(0xCD, space.Read8(0xCFFF));
    }

    [Fact]
    public void Uart_status_at_D001_reads_0x02_when_empty()
    {
        BootedBoard board = Boot();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        uint status = space.Read8(0xD001);

        Assert.Equal(0x02u, status);
    }

    [Fact]
    public void Mirror_feed_input_readable_at_D004()
    {
        BootedBoard board = Boot();
        board.Uart.FeedInput(0x42);
        var space = board.Machine.Space(AddressSpaceKind.Program);

        // offset 4 & 0x03 == 0 → DATA (mirrored)
        uint value = space.Read8(0xD004);

        Assert.Equal(0x42u, value);
    }

    [Fact]
    public void Rom_at_E000_ignores_writes()
    {
        BootedBoard board = Boot();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        byte originalByte = space.Read8(0xE000); // LDX opcode = 0xA2
        space.Write8(0xE000, 0xFF);
        byte afterWrite = space.Read8(0xE000);

        Assert.Equal(originalByte, afterWrite);
    }

    [Fact]
    public void Open_bus_D200_reads_0xFF()
    {
        // Relocated from $D100 (authorized change #6): the timer now owns $D100;
        // the first open-bus page is $D200.
        BootedBoard board = Boot();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        uint value = space.Read8(0xD200);

        Assert.Equal(0xFFu, value);
    }

    [Fact]
    public void Assemble_over_rom_lands_nothing_the_documented_behavior()
    {
        // PR #7 review observation, now live: TryAssembleAt over the non-strict ROM
        // window reports success but the bytes do not land — the echo disassembles the
        // ROM's real content. Pinned as documented behavior; the Peek API exists now
        // (PR #11), so verify-after-write is feasible — recorded monitor-v3 backlog
        // (bypassing ROM write-protect from the monitor is a feature decision, not a
        // transparency fix). See README known behaviors.
        BootedBoard board = Boot();
        var engine = board.NewMonitor();

        Assert.True(engine.TryAssembleAt(0xE000, "NOP", out _, out _));
        Assert.Equal(0xA2, board.Machine.Space(AddressSpaceKind.Program).Read8(0xE000));
        // ^ still LDX #$00 — the demo ROM's first byte, not 0xEA
    }

    [Fact]
    public void Timer_ctrl_at_D100_reads_zero_at_boot()
    {
        BootedBoard board = Boot();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        Assert.Equal(0x00u, space.Read8(0xD100)); // CTRL: all bits clear at boot
    }

    [Fact]
    public void Timer_mirrors_at_D104()
    {
        BootedBoard board = Boot();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        space.Write8(0xD101, 0x42); // PERIODL via the canonical address

        Assert.Equal(0x42u, space.Read8(0xD105)); // offset 5 & 0x03 == 1 == PERIODL (mirror)
        Assert.Equal(space.Read8(0xD100), space.Read8(0xD104)); // CTRL mirrors too
    }
}
