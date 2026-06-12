using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;
using CpuEmulator.Tests.Klaus;

namespace CpuEmulator.Tests.Monitor;

/// <summary>
/// Monitor-driven UAT sessions (Task 7): the monitor is the acceptance surface (spec §8).
/// Each session drives the REPL exactly as a user at the §9-item-7 host console would —
/// assemble through 'a', run through 'g', inspect through 'r'/'m', disassemble back
/// through 'd' — and asserts on the verbatim transcript.
/// </summary>
[Trait("Category", "UAT")]
public class MonitorUatTests
{
    private static (MonitorEngine Engine, Mos6502Cpu Cpu) NewMachine()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = new Mos6502Cpu(space);
        cpu.S = 0xFD;
        return (new MonitorEngine(cpu, space, cpu), cpu);
    }

    private static string RunSession(MonitorEngine engine, string session)
    {
        var output = new StringWriter();
        new MonitorRepl(engine, new StringReader(session), output).Run();
        return output.ToString();
    }

    [Fact]
    public void Countdown_session_assemble_run_inspect_disassemble()
    {
        // Assembled live through the monitor (branch target resolved by address):
        // 0200 LDX #$05 / 0202 STX $0300 / 0205 DEX / 0206 BNE $0205 (D0 FD) /
        // 0208 STX $0301 / 020B JMP $020B (self-trap). Cycles: 2+4+5*2+4*3+2+4 = 34.
        var (engine, _) = NewMachine();
        string text = RunSession(engine, """
            a $0200 LDX #$05
            a STX $0300
            a DEX
            a BNE $0205
            a STX $0301
            a JMP $020B
            g $0200 until $020B 10000
            r
            m $0300 2
            d $0200 6
            q
            """);

        Assert.Contains("0206: D0 FD     BNE *-3", text);        // branch resolved + echoed
        Assert.Contains("target $020B reached after 34 cycles", text); // cycle-exact run
        Assert.Contains("X=00", text);                            // countdown completed
        Assert.Contains("0300: 05 00", text);                     // both stores landed
        Assert.Contains("0200: A2 05     LDX #$05", text);        // disassembles back
        Assert.Contains("020B: 4C 0B 02  JMP $020B", text);
        Assert.DoesNotContain("? ", text);                        // no command errored
    }

    /// <summary>
    /// Klaus-via-monitor smoke: load the 64 KiB functional-test image through 'l', set PC
    /// through 'g $0400' (the leading-address form routes through engine.ProgramCounter),
    /// and run a bounded 1M-cycle slice toward the success trap ($3469). A passing full run
    /// needs ~96M cycles (3b-ii actual: 96,241,367), two orders of magnitude past the slice
    /// — so a healthy CPU exhausts the budget without trapping; any trap inside the slice
    /// is a real failure.
    /// </summary>
    [KlausFact]
    public void Klaus_via_monitor_loads_and_runs_a_bounded_slice_without_trapping()
    {
        var (engine, _) = NewMachine();
        string text = RunSession(engine, $"""
            l 0000 {KlausVectors.TryGetBinaryPath()!}
            g $0400 until $3469 1000000
            q
            """);

        Assert.Contains("loaded $10000 bytes at $0000", text);
        Assert.Contains("budget exhausted", text);
        Assert.DoesNotContain("trapped", text);
    }
}
