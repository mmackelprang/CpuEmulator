using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68000StepDispatchTests
{
    private static (M68000Cpu Cpu, AddressSpace Bus) Build(params (uint Addr, byte Val)[] mem)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 24,
            endianness: Endianness.BigEndian);
        bus.MapMemory(0x000000, new byte[0x10000], writable: true);
        foreach (var (a, v) in mem) bus.Write8(a, v);
        return (new M68000Cpu(bus), bus);
    }

    [Fact]
    public void Step_does_not_throw_NotSupported_anymore()
    {
        // 0x4100 at PC 0x1000 — matches no field op (the illegal path): routes to Undefined, advances PC by
        // the operword (length 2), no throw. (A non-MOVE family in M4.5a routes to Undefined the same way;
        // an UNMATCHED word decodes to length 2 cleanly — a matched EA-less op like NOP extracts a spurious
        // EA field from its low 6 bits and so the M4.3a decoder over-reports its length, which is a separate
        // pre-existing decode characteristic, not this slice's concern.)
        var (cpu, _) = Build((0x1000, 0x41), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        var ex = Record.Exception(() => cpu.Step());
        Assert.Null(ex);                                    // the M4.5 throw is gone
        Assert.Equal(0x1002u, (uint)cpu.GetRegister("PC")); // advanced past the operword (length 2)
    }

    [Fact(Skip = "MOVE body lands in Task 4")]
    public void Step_reaches_the_move_body_for_a_MOVE_operword()
    {
        // MOVE.l D0,D1 = 0x2200 (0010 dest-reg=001 dest-mode=000 src-mode=000 src-reg=000; size .l via Move enc).
        // After Step, D1 == D0 (the MOVE body ran). D0 set to a sentinel.
        var (cpu, _) = Build((0x1000, 0x22), (0x1001, 0x00));
        cpu.SetRegister("PC", 0x1000);
        cpu.SetRegister("D0", 0xCAFEF00D);
        cpu.Step();
        Assert.Equal(0xCAFEF00Du, (uint)cpu.GetRegister("D1"));  // MOVE executed (body from Task 4)
    }
}
