using System.Reflection;
using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdBlockLoadTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edbl")]
        public static class EdblSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"), new("BC", 16, HighHalf: "B", LowHalf: "C"),
                new("DE", 16, HighHalf: "D", LowHalf: "E"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xED)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xED, 0xA0, "LDI",  AddrMode.Implied, [EdBlock("LDI")]),
                Insn(0xED, 0xA8, "LDD",  AddrMode.Implied, [EdBlock("LDD")]),
                Insn(0xED, 0xB0, "LDIR", AddrMode.Implied, [EdBlock("LDIR")]),
            ];
        }

        public sealed partial class EdblCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public EdblCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            private byte ReadBus(uint a) => _bus.Read8(a);
            private void WriteBus(uint a, byte v) => _bus.Write8(a, v);
            private void HandleUndefinedOpcode(byte op) { }
        }
        """;

    private static (object Cpu, Type T, IAddressSpace Bus) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EdblCpu");
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = System.Activator.CreateInstance(t, new object[] { bus })!;
        return (cpu, t, bus);
    }
    private static void Set(object cpu, Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });
    private static ulong Get(object cpu, Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;

    [Fact]
    public void LDI_transfers_byte_adjusts_pointers_sets_flags_WZ_unchanged()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA0);     // LDI
        bus.Write8(0x4000, 0x37);                     // (HL) source byte
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "DE", 0x5000);
        Set(cpu, t, "BC", 0x0003); Set(cpu, t, "A", 0x01); Set(cpu, t, "WZ", 0xABCD);
        Set(cpu, t, "F", 0xFF);                        // S/Z/C should survive; H/N/PV recomputed
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x37, bus.Read8(0x5000));         // (DE) <- (HL)
        Assert.Equal(0x4001u, (uint)Get(cpu, t, "HL"));// HL+1
        Assert.Equal(0x5001u, (uint)Get(cpu, t, "DE"));// DE+1
        Assert.Equal(0x0002u, (uint)Get(cpu, t, "BC"));// BC-1
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x00, f & 0x10);                  // H = 0
        Assert.Equal(0x00, f & 0x02);                  // N = 0
        Assert.Equal(0x04, f & 0x04);                  // P/V = (BC-1 != 0) = 1
        Assert.Equal(0x80, f & 0x80);                  // S preserved (was set)
        Assert.Equal(0x01, f & 0x01);                  // C preserved
        // X/Y from (A + transferredByte) = (1 + 0x37) = 0x38: bit3(X)=1, bit1(Y)=0.
        Assert.Equal(0x08, f & 0x08);                  // X (F3) = bit3 of (A+n) = 1
        Assert.Equal(0x00, f & 0x20);                  // Y (F5) = bit1 of (A+n) = 0
        Assert.Equal(0xABCDu, (uint)Get(cpu, t, "WZ"));// WZ UNCHANGED
        Assert.Equal(f, Q(cpu, t));                    // Q = F (block ops always write F)
    }

    private static byte Q(object cpu, Type t) =>
        (byte)t.GetField("Q")!.GetValue(cpu)!;

    [Fact]
    public void LDD_decrements_pointers()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA8);     // LDD
        bus.Write8(0x4000, 0x42);
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "DE", 0x5000);
        Set(cpu, t, "BC", 0x0002);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x42, bus.Read8(0x5000));         // (DE) <- (HL)
        Assert.Equal(0x3FFFu, (uint)Get(cpu, t, "HL"));// HL-1
        Assert.Equal(0x4FFFu, (uint)Get(cpu, t, "DE"));// DE-1
        Assert.Equal(0x0001u, (uint)Get(cpu, t, "BC"));// BC-1
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x04, f & 0x04);                  // P/V = (BC-1 != 0) = 1
    }

    [Fact]
    public void LDI_clears_PV_when_BC_exhausted()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0, 0xED); bus.Write8(1, 0xA0);
        bus.Write8(0x4000, 0x00);
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "DE", 0x5000);
        Set(cpu, t, "BC", 0x0001);                     // BC-1 = 0 -> P/V = 0
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x0000u, (uint)Get(cpu, t, "BC"));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x00, f & 0x04);                  // P/V = 0
    }

    [Fact]
    public void LDIR_rewinds_PC_when_BC_not_exhausted()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0x100, 0xED); bus.Write8(0x101, 0xB0);   // LDIR at 0x100
        bus.Write8(0x4000, 0x99);
        Set(cpu, t, "PC", 0x100); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "DE", 0x5000);
        Set(cpu, t, "BC", 0x0003);                          // BC-1 = 2 != 0 -> repeat
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x100u, (uint)Get(cpu, t, "PC"));      // PC rewound to the instruction
        Assert.Equal(0x101u, (uint)Get(cpu, t, "WZ"));      // WZ = instruction-PC + 1
    }

    [Fact]
    public void LDIR_advances_PC_on_final_iteration()
    {
        var (cpu, t, bus) = Build();
        bus.Write8(0x100, 0xED); bus.Write8(0x101, 0xB0);
        bus.Write8(0x4000, 0x99);
        Set(cpu, t, "PC", 0x100); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "DE", 0x5000);
        Set(cpu, t, "BC", 0x0001);                          // BC-1 = 0 -> final, no repeat
        Set(cpu, t, "WZ", 0xCAFE);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x102u, (uint)Get(cpu, t, "PC"));      // PC advanced past the 2-byte instruction
        Assert.Equal(0xCAFEu, (uint)Get(cpu, t, "WZ"));     // WZ unchanged on the final (non-repeat) iteration
    }
}
