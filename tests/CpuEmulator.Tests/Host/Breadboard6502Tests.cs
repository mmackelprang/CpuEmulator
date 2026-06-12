using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Host;
using CpuEmulator.Monitor;

namespace CpuEmulator.Tests.Host;

public class Breadboard6502Tests
{
    [Fact]
    public void Ram_read_write_at_zero_page()
    {
        var board = new Breadboard6502();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        space.Write8(0x0000, 0xAB);

        Assert.Equal(0xAB, space.Read8(0x0000));
    }

    [Fact]
    public void Ram_read_write_at_cfff()
    {
        var board = new Breadboard6502();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        space.Write8(0xCFFF, 0xCD);

        Assert.Equal(0xCD, space.Read8(0xCFFF));
    }

    [Fact]
    public void Uart_status_at_D001_reads_0x02_when_empty()
    {
        var board = new Breadboard6502();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        uint status = space.Read8(0xD001);

        Assert.Equal(0x02u, status);
    }

    [Fact]
    public void Mirror_feed_input_readable_at_D004()
    {
        var board = new Breadboard6502();
        board.Uart.FeedInput(0x42);
        var space = board.Machine.Space(AddressSpaceKind.Program);

        // offset 4 & 0x03 == 0 → DATA (mirrored)
        uint value = space.Read8(0xD004);

        Assert.Equal(0x42u, value);
    }

    [Fact]
    public void Rom_at_E000_ignores_writes()
    {
        var board = new Breadboard6502();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        byte originalByte = space.Read8(0xE000); // LDX opcode = 0xA2
        space.Write8(0xE000, 0xFF);
        byte afterWrite = space.Read8(0xE000);

        Assert.Equal(originalByte, afterWrite);
    }

    [Fact]
    public void Open_bus_D100_reads_0xFF()
    {
        var board = new Breadboard6502();
        var space = board.Machine.Space(AddressSpaceKind.Program);

        uint value = space.Read8(0xD100);

        Assert.Equal(0xFFu, value);
    }

    [Fact]
    public void Assemble_over_rom_lands_nothing_the_documented_behavior()
    {
        // PR #7 review observation, now live: TryAssembleAt over the non-strict ROM
        // window reports success but the bytes do not land — the echo disassembles the
        // ROM's real content. Pinned as documented M1 behavior; the verify-after-write
        // fix is rejected until the Peek API exists (a read-back over MMIO is itself a
        // destructive read). See README known behaviors.
        var board = new Breadboard6502();
        var engine = board.NewMonitor();

        Assert.True(engine.TryAssembleAt(0xE000, "NOP", out _, out _));
        Assert.Equal(0xA2, board.Machine.Space(AddressSpaceKind.Program).Read8(0xE000));
        // ^ still LDX #$00 — the demo ROM's first byte, not 0xEA
    }
}
