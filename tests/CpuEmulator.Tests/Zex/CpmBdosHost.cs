using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;

namespace CpuEmulator.Tests.Zex;

/// <summary>
/// A MINIMAL in-test CP/M-80 host for the ZEXDOC/ZEXALL exercisers (M3.5-2, decision D6 — a test
/// fixture, NOT a real CpuEmulator.Hosts.CpmZ80 machine). It loads a .com image at 0x0100, seeds the
/// CP/M Page Zero (warm-boot terminator at 0x0000, the BDOS entry the program CALLs at 0x0005),
/// runs the Z80 interpreter one Step at a time, and intercepts a CALL to the BDOS (PC == 0x0005)
/// to service console output (function 2 = char in E; function 9 = $-terminated string at DE),
/// capturing the console transcript. It terminates when the program warm-boots (PC reaches 0x0000)
/// or the cycle budget is exhausted (a hang guard). ZEX is pure-computation: it asserts no interrupt
/// line and never HALTs, so every Step is a plain fetch-execute.
/// </summary>
public sealed class CpmBdosHost
{
    private const ushort Tpa = 0x0100;       // .com load + entry
    private const ushort BdosEntry = 0x0005; // the CALL target the intercept fires on
    private const ushort WarmBoot = 0x0000;  // termination sentinel
    private const byte StringTerminator = (byte)'$';

    private readonly Z80Cpu _cpu;
    private readonly AddressSpace _mem;
    private readonly StringBuilder _console = new();

    public bool Terminated { get; private set; }

    /// <summary>The Z80 T-state count consumed so far (the budget counter) — exposed for diagnostics
    /// and for pinning the passing ZEX cycle counts in the close-state record.</summary>
    public long CycleCount => _cpu.CycleCount;

    public CpmBdosHost(byte[] com)
    {
        ArgumentNullException.ThrowIfNull(com);
        _mem = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        _mem.MapMemory(0x0000, new byte[0x10000], writable: true);
        // Load the .com into the TPA.
        for (int i = 0; i < com.Length; i++)
            _mem.Write8((uint)(Tpa + i), com[i]);
        // Seed Page Zero: a warm-boot sentinel byte at 0x0000 (PC reaching here terminates) and a RET
        // at 0x0005 as a harmless real target (the intercept fires BEFORE executing it, so this is just
        // belt-and-suspenders for any path that does not hit the intercept).
        _mem.Write8(0x0000, 0x76);  // HALT byte at 0 is never executed (we terminate on PC==0 first)
        _mem.Write8(BdosEntry, 0xC9); // RET

        var io = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
        _cpu = new Z80Cpu(_mem, io);
        _cpu.SetRegister("PC", Tpa);
        _cpu.SetRegister("SP", 0xFFFE); // a sane CP/M-ish stack; ZEX sets up its own early
    }

    /// <summary>Run to warm boot or budget exhaustion. Returns the captured console transcript.</summary>
    public string Run(long cycleBudget)
    {
        while (_cpu.CycleCount < cycleBudget)
        {
            ushort pc = (ushort)_cpu.GetRegister("PC");
            if (pc == WarmBoot) { Terminated = true; break; }
            if (pc == BdosEntry) { ServiceBdos(); continue; }
            _cpu.Step();
        }
        return _console.ToString();
    }

    /// <summary>Service a BDOS call host-side: do the console effect, then RET (pop the return
    /// address the CALL pushed). Only functions 2 (console out) + 9 (print $-string) are implemented;
    /// any other function code is a silent RET (ZEX uses only 2 + 9).</summary>
    private void ServiceBdos()
    {
        byte fn = (byte)_cpu.GetRegister("C");
        switch (fn)
        {
            case 2: // console out: char in E
                _console.Append((char)(byte)_cpu.GetRegister("E"));
                break;
            case 9: // print $-terminated string at DE
            {
                ushort addr = (ushort)_cpu.GetRegister("DE");
                for (int guard = 0; guard < 0x10000; guard++)
                {
                    byte b = _mem.Read8(addr);
                    if (b == StringTerminator) break;
                    _console.Append((char)b);
                    addr = (ushort)(addr + 1);
                }
                break;
            }
            default:
                break; // unimplemented BDOS function — silent RET (ZEX never hits this)
        }
        ReturnFromBdos();
    }

    /// <summary>Host-side RET: pop the 16-bit return address off the Z80 stack and set PC.</summary>
    private void ReturnFromBdos()
    {
        ushort sp = (ushort)_cpu.GetRegister("SP");
        byte lo = _mem.Read8(sp);
        byte hi = _mem.Read8((ushort)(sp + 1));
        _cpu.SetRegister("SP", (ushort)(sp + 2));
        _cpu.SetRegister("PC", (ushort)((hi << 8) | lo));
    }
}
