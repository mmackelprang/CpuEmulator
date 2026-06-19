using CpuEmulator.Host;
using CpuEmulator.Machines;

namespace CpuEmulator.Tests.Host;

public class DemoRomTests
{
    private static BootedBoard Boot()
    {
        Assert.True(BoardRegistry.TryBoot("6502", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error), error);
        return board!;
    }

    [Fact]
    public void Listing_disassembles_back_verbatim()
    {
        BootedBoard board = Boot();
        var engine = board.NewMonitor();

        string expected = string.Join(Environment.NewLine, new[]
        {
            "E000: A2 00     LDX #$00",
            "E002: BD 1E E0  LDA $E01E,X",
            "E005: F0 07     BEQ *+7",
            "E007: 8D 00 D0  STA $D000",
            "E00A: E8        INX",
            "E00B: 4C 02 E0  JMP $E002",
            "E00E: AD 01 D0  LDA $D001",
            "E011: 29 01     AND #$01",
            "E013: F0 F9     BEQ *-7",
            "E015: AD 00 D0  LDA $D000",
            "E018: 8D 00 D0  STA $D000",
            "E01B: 4C 0E E0  JMP $E00E",
        });

        string actual = engine.Disassemble(0xE000, 12);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Message_bytes_and_terminator_land()
    {
        byte[] image = DemoRom.Build();

        Assert.Equal((byte)'H', image[0x1E]);

        byte[] messageBytes = System.Text.Encoding.ASCII.GetBytes(DemoRom.Message);
        for (int i = 0; i < messageBytes.Length; i++)
            Assert.Equal(messageBytes[i], image[0x1E + i]);

        Assert.Equal(0x00, image[0x3A]);
    }

    [Fact]
    public void Vectors_point_at_the_entry()
    {
        byte[] image = DemoRom.Build();

        Assert.Equal(0x00, image[0x1FFA]); // NMI lo
        Assert.Equal(0xE0, image[0x1FFB]); // NMI hi
        Assert.Equal(0x00, image[0x1FFC]); // RESET lo
        Assert.Equal(0xE0, image[0x1FFD]); // RESET hi
        Assert.Equal(0x00, image[0x1FFE]); // IRQ/BRK lo
        Assert.Equal(0xE0, image[0x1FFF]); // IRQ/BRK hi
    }

    [Fact]
    public void Reset_boots_to_the_entry()
    {
        BootedBoard board = Boot();
        board.Machine.Reset();

        Assert.Equal(0xE000u, board.NewMonitor().ProgramCounter);
    }

    [Fact]
    public void Hello_arrives_on_the_uart_after_a_bounded_run()
    {
        BootedBoard board = Boot();
        var collected = new System.Text.StringBuilder();
        board.Uart.OnTransmit = b => collected.Append((char)b);
        board.Machine.Reset();
        board.Machine.Run(1000);

        Assert.Equal(DemoRom.Message, collected.ToString());
    }
}
