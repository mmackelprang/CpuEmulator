using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;

namespace CpuEmulator.Host;

/// <summary>
/// The breadboard's 8 KiB ROM image ($E000–$FFFF), assembled AT STARTUP by the generated
/// single-instruction assembler (artifact 5 eating its own dogfood): a hello-print loop,
/// then a polled echo loop. Only data is poked — message bytes, NUL, vectors.
/// Assembly happens in a SCRATCH space mapping the SAME byte[] writable at the SAME
/// addresses: the live breadboard maps this image read-only, and TryAssembleAt over
/// non-strict ROM is a silent no-op (the recorded a-over-ROM behavior).
/// </summary>
public static class DemoRom
{
    public const ushort Entry = 0xE000;
    public const ushort MessageAddress = 0xE01E;
    public const string Message = "Hello from Breadboard6502!\r\n";

    public static byte[] Build()
    {
        var image = new byte[0x2000];
        var scratch = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        scratch.MapMemory(0xE000, image, writable: true);
        var cpu = new Mos6502Cpu(scratch);
        var assembler = new MonitorEngine(cpu, scratch, cpu);

        uint pc = Entry;
        foreach (string line in new[]
        {
            "LDX #$00",      // E000  entry: reset vector lands here
            "LDA $E01E,X",   // E002  next message byte
            "BEQ $E00E",     // E005  NUL terminator -> echo loop
            "STA $D000",     // E007  UART DATA: transmit
            "INX",           // E00A
            "JMP $E002",     // E00B  print loop
            "LDA $D001",     // E00E  echo: poll STATUS
            "AND #$01",      // E011  rx-ready?
            "BEQ $E00E",     // E013  not ready -> poll
            "LDA $D000",     // E015  DATA: dequeue rx
            "STA $D000",     // E018  DATA: echo it back
            "JMP $E00E",     // E01B  echo forever
        })
        {
            if (!assembler.TryAssembleAt(pc, line, out byte[] bytes, out string? error))
                throw new EmulationException(
                    $"demo ROM assembly failed at ${pc:X4} '{line}': {error}");
            pc += (uint)bytes.Length;
        }
        if (pc != MessageAddress)
            throw new EmulationException(
                $"demo ROM layout drifted: cursor ${pc:X4}, expected ${MessageAddress:X4}.");

        foreach (char c in Message)
            scratch.Write8(pc++, (byte)c);
        scratch.Write8(pc, 0x00);                              // terminator
        scratch.Write8(0xFFFA, 0x00); scratch.Write8(0xFFFB, 0xE0); // NMI    -> entry
        scratch.Write8(0xFFFC, 0x00); scratch.Write8(0xFFFD, 0xE0); // RESET  -> entry
        scratch.Write8(0xFFFE, 0x00); scratch.Write8(0xFFFF, 0xE0); // IRQ/BRK -> entry
        return image;
    }
}
