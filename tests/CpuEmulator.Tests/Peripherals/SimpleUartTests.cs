using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Peripherals;

public class SimpleUartTests
{
    // ── Facts: transmit ───────────────────────────────────────────────────────

    [Fact]
    public void Data_write_invokes_OnTransmit_with_the_byte()
    {
        var uart = new SimpleUart();
        var received = new List<byte>();
        uart.OnTransmit = b => received.Add(b);

        uart.Write(0, AccessWidth.Byte, 0x41);

        Assert.Equal([0x41], received);
    }

    [Fact]
    public void Write_without_sink_is_dropped_no_throw()
    {
        var uart = new SimpleUart();
        // OnTransmit is null — must not throw
        uart.Write(0, AccessWidth.Byte, 0x41);
    }

    [Fact]
    public void Write_masks_value_to_a_byte()
    {
        var uart = new SimpleUart();
        byte? got = null;
        uart.OnTransmit = b => got = b;

        uart.Write(0, AccessWidth.Byte, 0x1FF);

        Assert.Equal((byte)0xFF, got);
    }

    // ── Facts: receive ────────────────────────────────────────────────────────

    [Fact]
    public void Data_reads_dequeue_FIFO_in_order()
    {
        var uart = new SimpleUart();
        uart.FeedInput((byte)'A');
        uart.FeedInput((byte)'B');

        uint first = uart.Read(0, AccessWidth.Byte);
        uint second = uart.Read(0, AccessWidth.Byte);

        Assert.Equal('A', first);
        Assert.Equal('B', second);
    }

    [Fact]
    public void Empty_data_read_returns_0x00()
    {
        var uart = new SimpleUart();

        uint value = uart.Read(0, AccessWidth.Byte);

        Assert.Equal(0x00u, value);
    }

    // ── Facts: STATUS register ────────────────────────────────────────────────

    [Fact]
    public void Status_is_0x03_with_rx_queued_and_0x02_when_empty()
    {
        var uart = new SimpleUart();

        uint emptyStatus = uart.Read(1, AccessWidth.Byte);
        Assert.Equal(0x02u, emptyStatus); // tx-ready only

        uart.FeedInput(0x41);
        uint readyStatus = uart.Read(1, AccessWidth.Byte);
        Assert.Equal(0x03u, readyStatus); // rx-ready | tx-ready
    }

    [Fact]
    public void Status_read_never_dequeues()
    {
        var uart = new SimpleUart();
        uart.FeedInput(0x41);

        // Read STATUS twice — DATA should still yield the byte
        _ = uart.Read(1, AccessWidth.Byte);
        _ = uart.Read(1, AccessWidth.Byte);

        uint data = uart.Read(0, AccessWidth.Byte);
        Assert.Equal(0x41u, data);
    }

    // ── Theories: reserved offsets ────────────────────────────────────────────

    [Theory]
    [InlineData(2u)]
    [InlineData(3u)]
    public void Reserved_offsets_read_0x00(uint offset)
    {
        var uart = new SimpleUart();
        Assert.Equal(0x00u, uart.Read(offset, AccessWidth.Byte));
    }

    // ── Theories: non-DATA writes are ignored ─────────────────────────────────

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    public void Non_DATA_writes_are_ignored(uint offset)
    {
        var uart = new SimpleUart();
        byte? transmitted = null;
        uart.OnTransmit = b => transmitted = b;

        uart.Write(offset, AccessWidth.Byte, 0xFF);

        Assert.Null(transmitted);
        // STATUS still reflects empty (tx-ready only)
        Assert.Equal(0x02u, uart.Read(1, AccessWidth.Byte));
    }

    // ── Theories: register mirroring through the page ─────────────────────────

    [Theory]
    [InlineData(4u,  true)]   // offset 4 == offset 0 (DATA: dequeues)
    [InlineData(5u,  false)]  // offset 5 == offset 1 (STATUS: does not dequeue)
    [InlineData(0xFCu, true)] // offset 0xFC == offset 0 (DATA: dequeues)
    [InlineData(0xFDu, false)]// offset 0xFD == offset 1 (STATUS: does not dequeue)
    public void Registers_mirror_through_the_page(uint offset, bool shouldDequeue)
    {
        var uart = new SimpleUart();
        uart.FeedInput(0x41);

        _ = uart.Read(offset, AccessWidth.Byte);

        // If offset maps to DATA (& 0x03 == 0) it should have dequeued
        uint remaining = uart.Read(0, AccessWidth.Byte);
        if (shouldDequeue)
            Assert.Equal(0x00u, remaining); // was dequeued by mirrored read
        else
            Assert.Equal(0x41u, remaining); // STATUS read — still there
    }

    // ── Realize claims nothing and machine composition works ──────────────────

    [Fact]
    public void Realize_claims_nothing_and_machine_composition_works()
    {
        var uart = new SimpleUart();
        uart.FeedInput(0xAB);

        var machine = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
            .WithPeripheral(AddressSpaceKind.Program, 0xD000, 0x0100, uart)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();

        // Realize ran (no throw), and the peripheral is reachable through the machine space
        uint value = machine.Space(AddressSpaceKind.Program).Read8(0xD000);
        Assert.Equal(0xABu, value);
    }

    // ── Perturbation pins ─────────────────────────────────────────────────────
    //
    // These two tests document known, deliberate behavior: because monitor hex dumps
    // (and any bus read) go through the live address bus, reading the DATA register at
    // offset 0 is a destructive rx read. This is hardware-faithful (real UARTs work the
    // same way). A Peek API that avoids dequeuing on inspection is a monitor-v2 backlog
    // item. These tests make the behavior deliberate, not accidental.

    [Fact]
    public void Bus_read_over_DATA_dequeues_rx_the_hardware_truth()
    {
        // Hardware truth: a bus read at the DATA offset (& 0x03 == 0) is a destructive rx
        // dequeue — real UARTs work the same way. The monitor's display path (m/d/s) no
        // longer takes this path (it peeks instead), but live bus reads still dequeue.
        var uart = new SimpleUart();
        uart.FeedInput((byte)'A');
        uart.FeedInput((byte)'B');

        var machine = Machine.Create("test")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
            .WithPeripheral(AddressSpaceKind.Program, 0xD000, 0x10000 - 0xD000, uart)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();

        var space = machine.Space(AddressSpaceKind.Program);

        // First live bus read at $D000 (offset 0 → DATA): consumes 'A'
        uint first = space.Read8(0xD000);
        Assert.Equal((uint)'A', first);
        // STATUS bit0 still set — 'B' is still queued
        Assert.Equal(0x03u, space.Read8(0xD001));

        // Second live bus read at $D000: consumes 'B'
        uint second = space.Read8(0xD000);
        Assert.Equal((uint)'B', second);
        // STATUS bit0 now clear — queue drained
        Assert.Equal(0x02u, space.Read8(0xD001));
    }

    [Fact]
    public void Monitor_m_command_over_DATA_peeks_without_dequeuing()
    {
        // PR #8 perturbation pin, flipped to guard the Peek fix: the monitor 'm' command
        // now uses TryPeek (side-effect-free). A dump over DATA shows the head byte without
        // dequeuing it. Both bytes remain in the queue after the monitor read.
        var uart = new SimpleUart();
        uart.FeedInput((byte)'A');
        uart.FeedInput((byte)'B');

        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0xD000], writable: true);
        space.MapPeripheral(0xD000, 0x3000, uart);

        var cpu = new Mos6502Cpu(space);
        cpu.SetRegister("PC", 0x0200);
        cpu.S = 0xFD;

        var engine = new MonitorEngine(cpu, space, cpu);

        // 'm d000 1' dumps 1 byte starting at $D000 — now a side-effect-free peek
        var output = new StringWriter();
        new MonitorRepl(engine, new StringReader("m d000 1\nq"), output).Run();
        string text = output.ToString();

        // Output should show 'A' (0x41) at D000
        Assert.Contains("D000:", text);
        Assert.Contains("41", text);

        // BOTH bytes remain queued — the monitor peek did not dequeue 'A'
        Assert.Equal((uint)'A', uart.Read(0, AccessWidth.Byte));
        Assert.Equal((uint)'B', uart.Read(0, AccessWidth.Byte));
    }

    // ── Honest peek (v1 registers) ────────────────────────────────────────────

    [Fact]
    public void TryPeek_DATA_returns_head_without_dequeuing()
    {
        var uart = new SimpleUart();
        uart.FeedInput((byte)'A');
        uart.FeedInput((byte)'B');

        bool ok1 = uart.TryPeek(0, out byte v1);
        bool ok2 = uart.TryPeek(0, out byte v2);

        Assert.True(ok1);
        Assert.True(ok2);
        Assert.Equal((byte)'A', v1);
        Assert.Equal((byte)'A', v2); // head, not dequeued

        // Live reads still yield A then B
        Assert.Equal((uint)'A', uart.Read(0, AccessWidth.Byte));
        Assert.Equal((uint)'B', uart.Read(0, AccessWidth.Byte));
    }

    [Fact]
    public void TryPeek_DATA_empty_returns_zero()
    {
        var uart = new SimpleUart();
        bool ok = uart.TryPeek(0, out byte value);
        Assert.True(ok);
        Assert.Equal(0x00, value);
    }

    [Fact]
    public void TryPeek_STATUS_matches_read()
    {
        var uart = new SimpleUart();
        uart.FeedInput(0x41);

        uint readStatus = uart.Read(1, AccessWidth.Byte);
        uart.TryPeek(1, out byte peekStatus);

        Assert.Equal((byte)readStatus, peekStatus);
    }
}
