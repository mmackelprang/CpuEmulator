using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;

namespace CpuEmulator.Tests.Monitor;

/// <summary>
/// Tests for MonitorEngine memory/file/registers/disassembly API (Task 4).
/// Fixture: Mos6502Cpu over 64 KiB RAM, engine new MonitorEngine(cpu, space, cpu).
/// </summary>
public class MonitorEngineTests
{
    private static (MonitorEngine Engine, Mos6502Cpu Cpu, IAddressSpace Space) NewMachine()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = new Mos6502Cpu(space);
        cpu.SetRegister("PC", 0x0200);
        var engine = new MonitorEngine(cpu, space, cpu);
        return (engine, cpu, space);
    }

    // ── Ctor null-arg checks ─────────────────────────────────────────────────

    [Fact]
    public void Ctor_throws_on_null_cpu()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = new Mos6502Cpu(space);
        Assert.Throws<ArgumentNullException>(() => new MonitorEngine(null!, space, cpu));
    }

    [Fact]
    public void Ctor_throws_on_null_memory()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = new Mos6502Cpu(space);
        Assert.Throws<ArgumentNullException>(() => new MonitorEngine(cpu, null!, cpu));
    }

    [Fact]
    public void Ctor_throws_on_null_support()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = new Mos6502Cpu(space);
        Assert.Throws<ArgumentNullException>(() => new MonitorEngine(cpu, space, null!));
    }

    // ── LoadBytes / SaveBytes ────────────────────────────────────────────────

    [Fact]
    public void LoadBytes_lands_bytes_at_address()
    {
        var (engine, _, _) = NewMachine();
        engine.LoadBytes(0x0200, new byte[] { 0xA9, 0x42 });
        byte[] result = engine.SaveBytes(0x0200, 2);
        Assert.Equal(new byte[] { 0xA9, 0x42 }, result);
    }

    [Fact]
    public void LoadBytes_wraps_past_address_boundary()
    {
        var (engine, _, space) = NewMachine();
        engine.LoadBytes(0xFFFF, new byte[] { 0x01, 0x02 });
        Assert.Equal(0x01, space.Read8(0xFFFF));
        Assert.Equal(0x02, space.Read8(0x0000)); // wrapped
    }

    [Fact]
    public void SaveBytes_wraps_past_address_boundary()
    {
        var (engine, _, space) = NewMachine();
        space.Write8(0xFFFF, 0xAA);
        space.Write8(0x0000, 0xBB);
        byte[] result = engine.SaveBytes(0xFFFF, 2);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, result);
    }

    // ── LoadFile / SaveFile ──────────────────────────────────────────────────

    [Fact]
    public void LoadFile_SaveFile_roundtrip()
    {
        var (engine, _, _) = NewMachine();
        byte[] original = new byte[] { 0x01, 0x02, 0x03, 0x04, 0xAB, 0xCD };
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, original);
            int loaded = engine.LoadFile(0x0300, path);
            Assert.Equal(original.Length, loaded);

            string savePath = Path.GetTempFileName();
            try
            {
                engine.SaveFile(0x0300, original.Length, savePath);
                byte[] saved = File.ReadAllBytes(savePath);
                Assert.Equal(original, saved);
            }
            finally
            {
                File.Delete(savePath);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── ReadMemory / WriteMemory (hex dump format) ───────────────────────────

    [Fact]
    public void ReadMemory_formats_partial_line_with_padding_and_ascii()
    {
        var (engine, _, _) = NewMachine();
        engine.WriteMemory(0x0300, new byte[] { 0x05, 0x00 });
        string dump = engine.ReadMemory(0x0300, 2);
        // Ground truth D, pinned VERBATIM: {addr:X4}: {hex,-47} |{ascii}| — a partial
        // line space-pads the hex field to the full 47 chars.
        Assert.Equal("0300: " + "05 00".PadRight(47) + " |..|", dump);
    }

    [Fact]
    public void ReadMemory_formats_full_16byte_line()
    {
        var (engine, _, _) = NewMachine();
        byte[] data = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        engine.WriteMemory(0x0000, data);
        string dump = engine.ReadMemory(0x0000, 16);
        // Should have exactly one line starting with 0000:
        Assert.StartsWith("0000: ", dump);
        Assert.DoesNotContain(Environment.NewLine, dump); // single line
        // 16 bytes * 3 chars each - 1 trailing space = 47 chars: "00 01 02 ... 0F"
        Assert.Contains("00 01 02 03 04 05 06 07 08 09 0A 0B 0C 0D 0E 0F", dump);
    }

    [Fact]
    public void ReadMemory_formats_printable_ascii_and_dots()
    {
        var (engine, _, _) = NewMachine();
        engine.WriteMemory(0x0200, new byte[] { 0x41 }); // 'A'
        string dump = engine.ReadMemory(0x0200, 1);
        Assert.Contains("|A|", dump);
    }

    [Fact]
    public void ReadMemory_non_printable_becomes_dot()
    {
        var (engine, _, _) = NewMachine();
        engine.WriteMemory(0x0200, new byte[] { 0x01 }); // non-printable
        string dump = engine.ReadMemory(0x0200, 1);
        Assert.Contains("|.|", dump);
    }

    [Fact]
    public void WriteMemory_mutates_memory()
    {
        var (engine, _, space) = NewMachine();
        engine.WriteMemory(0x0300, new byte[] { 0x05, 0x00 });
        Assert.Equal(0x05, space.Read8(0x0300));
        Assert.Equal(0x00, space.Read8(0x0301));
    }

    // ── Disassemble ──────────────────────────────────────────────────────────

    [Fact]
    public void Disassemble_formats_three_instructions_correctly()
    {
        var (engine, _, _) = NewMachine();
        // A9 42 EA 4C 00 02
        engine.LoadBytes(0x0200, new byte[] { 0xA9, 0x42, 0xEA, 0x4C, 0x00, 0x02 });
        string result = engine.Disassemble(0x0200, 3);

        string[] lines = result.Split(Environment.NewLine);
        Assert.Equal(3, lines.Length);
        Assert.Equal("0200: A9 42     LDA #$42", lines[0]);
        Assert.Equal("0202: EA        NOP", lines[1]);
        Assert.Equal("0203: 4C 00 02  JMP $0200", lines[2]);
    }

    [Fact]
    public void Disassemble_undefined_opcode_renders_question_marks_and_advances_1()
    {
        var (engine, _, _) = NewMachine();
        engine.LoadBytes(0x0200, new byte[] { 0xFF, 0xEA }); // 0xFF = undefined
        string result = engine.Disassemble(0x0200, 2);

        string[] lines = result.Split(Environment.NewLine);
        Assert.Equal(2, lines.Length);
        Assert.Equal("0200: FF        ???", lines[0]);
        Assert.Equal("0201: EA        NOP", lines[1]);
    }

    // ── Registers / SetRegister ──────────────────────────────────────────────

    [Fact]
    public void Registers_returns_formatted_line()
    {
        var (engine, cpu, _) = NewMachine();
        cpu.A = 0x00; cpu.X = 0x05; cpu.Y = 0x00;
        cpu.S = 0xFD; cpu.P = 0xB0; cpu.PC = 0x0202;
        // Consume some cycles via a NOP step so CYC > 0
        engine.LoadBytes(0x0202, new byte[] { 0xEA });
        // Just check format of registers line without stepping
        string regs = engine.Registers();
        Assert.StartsWith("A=00 X=05 Y=00 S=FD P=B0 PC=0202 CYC=", regs);
    }

    [Fact]
    public void SetRegister_PC_is_reflected_in_Registers()
    {
        var (engine, _, _) = NewMachine();
        engine.SetRegister("PC", 0x0400);
        string regs = engine.Registers();
        Assert.Contains("PC=0400", regs);
    }

    // ── CPU-agnostic PC / address-width surface (review follow-up) ───────────
    // The REPL must never reach for "PC" or :X4 by name — these two members are
    // the engine's IMonitorSupport-routed accessors for them.

    [Fact]
    public void ProgramCounter_round_trips_through_the_PC_role_register()
    {
        var (engine, cpu, _) = NewMachine();
        engine.ProgramCounter = 0x0400;
        Assert.Equal(0x0400u, engine.ProgramCounter);
        Assert.Equal(0x0400, cpu.PC);
    }

    [Fact]
    public void ProgramCounter_set_masks_to_the_address_space()
    {
        var (engine, _, _) = NewMachine();
        engine.ProgramCounter = 0x1_0001; // 17 bits — wraps to 0x0001 on a 16-bit bus
        Assert.Equal(0x0001u, engine.ProgramCounter);
    }

    [Fact]
    public void AddressDigits_is_four_for_a_16_bit_space()
    {
        var (engine, _, _) = NewMachine();
        Assert.Equal(4, engine.AddressDigits);
    }
}
