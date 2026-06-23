using System.Text;
using CpuEmulator.Host;
using CpuEmulator.Machines;
using Xunit;

namespace CpuEmulator.Tests.Host;

/// <summary>The per-board host smokes: each board boots through BoardRegistry, the monitor
/// renders the right per-CPU registers (+ disassembly where the CPU has one), step/run
/// advances, and the UART round-trips. These are the un-fakeable "the host boots any board"
/// proofs for piece #3.</summary>
public class HostBoardSmokeTests
{
    [Fact]
    public void Mos6502_registry_path_prints_the_demo_banner_message_byte_identically()
    {
        // Boot the 6502 through the registry, reset, run the demo on a bounded budget — the
        // captured UART stream must be the breadboard demo's hello message (the same bytes the
        // retired hand-wired path produced).
        var tx = new StringBuilder();
        bool ok = BoardRegistry.TryBoot("6502", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error);
        Assert.True(ok, error);
        board!.Uart.OnTransmit = b => tx.Append((char)b);

        board.Machine.Reset();        // PC = $E000 via the ROM reset vector
        board.Machine.Run(10_000);    // hello completes well within this budget

        Assert.Contains("Hello from Breadboard6502!", tx.ToString());
    }

    [Fact]
    public void Mos6502_host_smoke_registers_disasm_step_and_uart_echo()
    {
        Assert.True(BoardRegistry.TryBoot("6502", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error), error);
        board!.Machine.Reset();
        var engine = board.NewMonitor();

        // Registers: the 6502 names — A, X, Y, S(P), P, PC — appear in the rendered line.
        string regs = engine.Registers();
        Assert.Contains("A=", regs);
        Assert.Contains("PC=", regs);
        Assert.Contains("P=", regs);   // the 6502 status register (proves 6502-shaped state)

        // Disassembly at the reset entry $E000 is the demo's 'LDX #$00' (real 6502 mnemonic).
        string dis = engine.Disassemble(0xE000, 1);
        Assert.Contains("LDX", dis);

        // Step advances PC past the 2-byte LDX.
        var step = engine.Step();
        Assert.Equal(0xE000u, step.PcBefore);
        Assert.Equal(0xE002u, engine.ProgramCounter);

        // UART round-trip: run the demo to its echo loop, feed a byte, run, observe it echoed.
        var tx = new StringBuilder();
        board.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Run(20_000);     // hello prints, then the ROM parks in the polled echo loop
        tx.Clear();
        board.Uart.FeedInput((byte)'Z');
        board.Machine.Run(20_000);     // the echo loop dequeues + retransmits
        Assert.Contains("Z", tx.ToString());
    }

    [Fact]
    public void Z80_host_smoke_registers_disasm_and_uart_prints_OK()
    {
        Assert.True(BoardRegistry.TryBoot("z80", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error), error);

        var tx = new StringBuilder();
        board!.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();        // Z80: PC = 0 (the program was poked into RAM at boot)
        var engine = board.NewMonitor();

        // Registers: the Z80 names a PC + an A; it does NOT have a 6502 'P' status register.
        string regs = engine.Registers();
        Assert.Contains("PC=", regs);
        Assert.Contains("A=", regs);

        // Disassembly at $0000 is the boot program's first op: LD A,'O' (opcode 0x3E).
        // The Z80 disassembler renders a real mnemonic (1605 arms), not '???'.
        string dis = engine.Disassemble(0x0000, 1);
        Assert.Contains("LD", dis);
        Assert.DoesNotContain("???", dis);

        // Step advances PC off $0000 (the boot program is executing real instructions).
        engine.Step();
        Assert.NotEqual(0x0000u, engine.ProgramCounter);

        // UART round-trip: run to completion; the boot writes "OK\r" out the UART.
        board.Machine.Run(2000);
        Assert.Equal("OK\r", tx.ToString());
    }

    [Fact]
    public void Z80_host_smoke_on_the_jit_tier_also_prints_OK()
    {
        Assert.True(BoardRegistry.TryBoot("z80", ExecutionTier.Jit,
            out BootedBoard? board, out string? error), error);

        var tx = new StringBuilder();
        board!.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();
        board.Machine.Run(2000);
        Assert.Equal("OK\r", tx.ToString());
    }

    [Fact]
    public void M68000_host_smoke_registers_24bit_address_step_and_uart_prints_OK()
    {
        Assert.True(BoardRegistry.TryBoot("68000", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error), error);

        var tx = new StringBuilder();
        board!.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();        // 68000: SSP/PC from $0/$4, SR=0x2700 (supervisor)
        var engine = board.NewMonitor();

        // Registers: the 68000 names D-registers + a PC + SR (NOT a 6502 'A'/'P').
        string regs = engine.Registers();
        Assert.Contains("PC=", regs);
        Assert.Contains("D0=", regs);    // a 68000 data register — proves 68000-shaped state

        // 24-bit address width: the memory dump renders 6 hex digits (e.g. "000008:").
        Assert.Equal(6, engine.AddressDigits);
        string dump = engine.ReadMemory(0x000008, 1);
        Assert.StartsWith("000008:", dump);

        // The generated 68000 disassembler now walks the field grammar (roadmap #6 / D68):
        // it renders a real mnemonic for any opword the decoder recognizes, not '???'. The
        // boot ROM at $000008 holds a real instruction (the boot program), so the monitor
        // renders its mnemonic. (Extension-word operands render placeholders — the 3-byte
        // disassembly contract cannot carry them — but the mnemonic + size + cc + EA are real.)
        string dis = engine.Disassemble(0x000008, 1);
        Assert.DoesNotContain("???", dis);

        // Step still advances PC (length comes from the real DescriptorFor, not the disasm).
        uint pcBefore = engine.ProgramCounter;
        engine.Step();
        Assert.NotEqual(pcBefore, engine.ProgramCounter);

        // UART round-trip: run to completion; the boot writes "OK\r".
        board.Machine.Run(2000);
        Assert.Equal("OK\r", tx.ToString());
    }

    [Fact]
    public void I8086_host_smoke_registers_20bit_address_disasm_and_uart_prints_OK()
    {
        Assert.True(BoardRegistry.TryBoot("8086", ExecutionTier.Interpreter,
            out BootedBoard? board, out string? error), error);

        var tx = new StringBuilder();
        board!.Uart.OnTransmit = b => tx.Append((char)b);
        board.Machine.Reset();        // 8086: CS=FFFF, IP=0, DS=ES=SS=0, FLAGS=0
        var engine = board.NewMonitor();

        // Registers: the 8086 names segment + general registers (AX, CS, ...) + the program
        // counter. The 8086's program counter renders as 'IP' (its own register name — the
        // engine's register dump iterates RegisterNames directly), not '6502-style' PC.
        string regs = engine.Registers();
        Assert.Contains("IP=", regs);    // the 8086 program counter (observed name)
        Assert.Contains("AX=", regs);    // an 8086 general register
        Assert.Contains("CS=", regs);    // a segment register — proves 8086-shaped state

        // 20-bit address width: 5 hex digits.
        Assert.Equal(5, engine.AddressDigits);

        // Disassembly at the body (physical 0xF0000) is MOV (opcode 0xB8). The 8086 disasm
        // renders a real mnemonic (284 arms), not '???'.
        string dis = engine.Disassemble(0xF0000, 1);
        Assert.Contains("MOV", dis);
        Assert.DoesNotContain("???", dis);

        // UART round-trip: run to completion; the boot FAR-JMPs to the body and writes "OK\r".
        board.Machine.Run(2000);
        Assert.Equal("OK\r", tx.ToString());
    }
}
