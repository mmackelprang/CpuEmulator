using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;

namespace CpuEmulator.Tests.Monitor;

/// <summary>
/// Tests for MonitorRepl command grammar and verbatim output contracts (Task 6).
/// Each test drives new MonitorRepl(engine, new StringReader(input), output).Run()
/// and asserts on output.ToString().
/// </summary>
public class MonitorReplTests
{
    private static (MonitorEngine Engine, Mos6502Cpu Cpu, IAddressSpace Space) NewMachine()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);

        // IRQ vector → $8000
        space.Write8(0xFFFE, 0x00);
        space.Write8(0xFFFF, 0x80);
        space.Write8(0x8000, 0xEA);

        var cpu = new Mos6502Cpu(space);
        cpu.SetRegister("PC", 0x0200);
        cpu.S = 0xFD;
        var engine = new MonitorEngine(cpu, space, cpu);
        return (engine, cpu, space);
    }

    private static string RunSession(MonitorEngine engine, string input)
    {
        var output = new StringWriter();
        new MonitorRepl(engine, new StringReader(input), output).Run();
        return output.ToString();
    }

    // ── m command: dump ───────────────────────────────────────────────────────

    [Fact]
    public void M_dump_formats_two_bytes()
    {
        var (engine, _, space) = NewMachine();
        space.Write8(0x0300, 0x05);
        space.Write8(0x0301, 0x41);
        string text = RunSession(engine, "m 0300 2\nq");
        Assert.Contains("0300:", text);
        Assert.Contains("05 41", text);
    }

    [Fact]
    public void M_write_stores_bytes_and_echoes_dump()
    {
        var (engine, _, space) = NewMachine();
        string text = RunSession(engine, "m 0300: 05 41\nq");
        Assert.Contains("0300:", text);
        Assert.Contains("05 41", text);
        Assert.Equal(0x05, space.Read8(0x0300));
        Assert.Equal(0x41, space.Read8(0x0301));
    }

    [Fact]
    public void M_write_ascii_A_appears_in_echo()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "m 0300: 41\nq");
        Assert.Contains("|A|", text);
    }

    [Fact]
    public void M_write_bad_byte_token_prints_error()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "m 0300: zz\nq");
        Assert.Contains("? bad byte 'zz'", text);
    }

    // ── d command: disassemble ────────────────────────────────────────────────

    [Fact]
    public void D_disassembles_two_instructions()
    {
        var (engine, _, space) = NewMachine();
        space.Write8(0x0200, 0xA9);
        space.Write8(0x0201, 0x42);
        space.Write8(0x0202, 0xEA);
        string text = RunSession(engine, "d 0200 2\nq");
        Assert.Contains("0200: A9 42     LDA #$42", text);
        Assert.Contains("0202: EA        NOP", text);
    }

    [Fact]
    public void D_default_count_is_8()
    {
        var (engine, _, space) = NewMachine();
        // Fill 8 NOPs
        for (uint i = 0; i < 8; i++) space.Write8(0x0200 + i, 0xEA);
        string text = RunSession(engine, "d 0200\nq");
        // Should have 8 lines with 0200..0207
        Assert.Contains("0207:", text);
    }

    // ── a command: assemble ───────────────────────────────────────────────────

    [Fact]
    public void A_with_address_assembles_and_echoes_disassembly_line()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "a $0200 LDA #$42\nq");
        Assert.Contains("0200: A9 42     LDA #$42", text);
        Assert.DoesNotContain("?", text.Replace("?", string.Empty.PadLeft(0)) + "SENTINEL");
        // More precisely: no line starting with "? "
        foreach (string line in text.Split('\n'))
            Assert.DoesNotMatch(@"^\? ", line.TrimEnd());
    }

    [Fact]
    public void A_cursor_form_continues_from_prior_address()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "a $0200 LDA #$42\na NOP\nq");
        Assert.Contains("0200: A9 42     LDA #$42", text);
        Assert.Contains("0202: EA        NOP", text);
    }

    [Fact]
    public void A_cursor_form_without_prior_address_prints_error()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "a NOP\nq");
        Assert.Contains("? no assembly address", text);
    }

    [Fact]
    public void A_unknown_mnemonic_prints_error()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "a $0200 FROB #$12\nq");
        Assert.Contains("? ", text);
        Assert.Contains("unknown mnemonic", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── r command: registers ──────────────────────────────────────────────────

    [Fact]
    public void R_prints_registers_line()
    {
        var (engine, cpu, _) = NewMachine();
        cpu.SetRegister("PC", 0x0202);
        string text = RunSession(engine, "r\nq");
        Assert.Contains("PC=0202", text);
        Assert.Contains("CYC=", text);
    }

    [Fact]
    public void R_set_register_updates_and_prints()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "r PC=$0400\nq");
        Assert.Contains("PC=0400", text);
    }

    [Fact]
    public void R_unknown_register_prints_error()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "r BOGUS=1\nq");
        Assert.Contains("? ", text);
        Assert.Contains("BOGUS", text);
    }

    // ── s command: step ───────────────────────────────────────────────────────

    [Fact]
    public void S_prints_two_line_step_report()
    {
        var (engine, cpu, space) = NewMachine();
        space.Write8(0x0200, 0xEA); // NOP
        cpu.SetRegister("PC", 0x0200);
        string text = RunSession(engine, "s\nq");
        // First line: "{pc}: {disassembly}"
        Assert.Contains("0200: NOP", text);
        // Second line: registers
        Assert.Contains("PC=0201", text);
    }

    [Fact]
    public void S_N_steps_prints_multiple_reports()
    {
        var (engine, cpu, space) = NewMachine();
        space.Write8(0x0200, 0xEA);
        space.Write8(0x0201, 0xEA);
        space.Write8(0x0202, 0xEA);
        cpu.SetRegister("PC", 0x0200);
        string text = RunSession(engine, "s 3\nq");
        Assert.Contains("0200: NOP", text);
        Assert.Contains("0201: NOP", text);
        Assert.Contains("0202: NOP", text);
    }

    [Fact]
    public void S_interrupt_pending_prints_interrupt_serviced()
    {
        var (engine, cpu, space) = NewMachine();
        space.Write8(0x0200, 0xEA);
        cpu.SetRegister("PC", 0x0200);
        cpu.P = 0x20; // I clear
        cpu.SetIrqLine(true);
        string text = RunSession(engine, "s\nq");
        Assert.Contains("(interrupt serviced)", text);
    }

    // ── g command: run ────────────────────────────────────────────────────────

    [Fact]
    public void G_with_until_target_prints_target_reached_stop_line()
    {
        var (engine, cpu, space) = NewMachine();
        space.Write8(0x0200, 0xEA); // NOP
        space.Write8(0x0201, 0x4C); // JMP $0203
        space.Write8(0x0202, 0x03);
        space.Write8(0x0203, 0x02);
        cpu.SetRegister("PC", 0x0200);
        string text = RunSession(engine, "g $0200 until $0201 10000\nq");
        Assert.Contains("target $0201 reached after", text);
        Assert.Contains("cycles", text);
    }

    [Fact]
    public void G_trap_prints_trapped_stop_line()
    {
        var (engine, cpu, space) = NewMachine();
        // JMP $0200 — self-trap
        space.Write8(0x0200, 0x4C);
        space.Write8(0x0201, 0x00);
        space.Write8(0x0202, 0x02);
        cpu.SetRegister("PC", 0x0200);
        string text = RunSession(engine, "g $0200 until $FFFF 100000\nq");
        Assert.Contains("trapped at $0200 after", text);
    }

    [Fact]
    public void G_budget_exhausted_prints_budget_stop_line()
    {
        var (engine, cpu, space) = NewMachine();
        // NOP slide
        for (uint i = 0; i < 50; i++) space.Write8(0x0200 + i, 0xEA);
        cpu.SetRegister("PC", 0x0200);
        string text = RunSession(engine, "g $0200 until $FFFF 10\nq");
        Assert.Contains("budget exhausted at $", text);
        Assert.Contains("after", text);
    }

    [Fact]
    public void G_bare_runs_with_default_budget_and_prints_stop_line()
    {
        var (engine, cpu, space) = NewMachine();
        // JMP self — trap
        space.Write8(0x0200, 0x4C);
        space.Write8(0x0201, 0x00);
        space.Write8(0x0202, 0x02);
        cpu.SetRegister("PC", 0x0200);
        // Bare g without until — no target, just budget
        string text = RunSession(engine, "g\nq");
        // Should print a stop line (budget exhausted or trapped)
        Assert.True(
            text.Contains("budget exhausted at") || text.Contains("trapped at"),
            $"Expected stop line, got: {text}");
    }

    // ── l / w commands: load / save ───────────────────────────────────────────

    [Fact]
    public void L_W_load_save_file_roundtrip()
    {
        var (engine, _, _) = NewMachine();
        byte[] original = new byte[] { 0xA9, 0x42, 0xEA };
        string loadPath = Path.GetTempFileName();
        string savePath = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(loadPath, original);
            string text = RunSession(engine,
                $"l 0300 {loadPath}\nw 0300 3 {savePath}\nq");
            Assert.Contains("loaded $3 bytes at $0300", text);
            Assert.Contains("wrote $3 bytes from $0300", text);
            Assert.Equal(original, File.ReadAllBytes(savePath));
        }
        finally
        {
            File.Delete(loadPath);
            File.Delete(savePath);
        }
    }

    [Fact]
    public void L_missing_file_prints_error_and_repl_survives()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "l 0300 /nonexistent/path/file.bin\nq");
        Assert.Contains("? ", text);
        // REPL continues — q exits cleanly
    }

    // ── ? / unknown / blank / q ───────────────────────────────────────────────

    [Fact]
    public void Help_command_prints_command_table()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "?\nq");
        // The table should mention the commands
        Assert.Contains("m ADDR", text);
        Assert.Contains("d ADDR", text);
        Assert.Contains("a $ADDR", text);
        Assert.Contains("r", text);
        Assert.Contains("s [N]", text);
        Assert.Contains("g [", text);
        Assert.Contains("l ADDR", text);
        Assert.Contains("w ADDR", text);
        Assert.Contains("q", text);
    }

    [Fact]
    public void Unknown_command_prints_error_with_help_hint()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "x\nq");
        Assert.Contains("? unknown command 'x'", text);
        Assert.Contains("type ? for help", text);
    }

    [Fact]
    public void Blank_lines_are_ignored()
    {
        var (engine, _, _) = NewMachine();
        string text = RunSession(engine, "\n  \n\nq");
        // Should produce empty output (no ? errors)
        Assert.DoesNotContain("?", text);
    }

    [Fact]
    public void Q_quits()
    {
        var (engine, _, _) = NewMachine();
        // Commands after q should not run
        string text = RunSession(engine, "q\nr");
        Assert.DoesNotContain("CYC=", text);
    }

    [Fact]
    public void Eof_quits()
    {
        var (engine, _, _) = NewMachine();
        // StringReader with no q — EOF should quit cleanly
        string text = RunSession(engine, "r");
        Assert.Contains("CYC=", text); // r ran
        // No crash — clean exit
    }
}
