using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80WzModelTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("wzf")]
        public static class WzfSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"), new("BC", 16, HighHalf: "B", LowHalf: "C"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xC3, "JP",  AddrMode.ExtendedAddress, [JumpAbs()]),
                Insn(0xCD, "CALL", AddrMode.ExtendedAddress, [CallAbs()]),
                Insn(0xC7, "RST", AddrMode.Implied, [Rst()]),
                Insn(0x18, "JR",  AddrMode.RelativeJump, [RelJump()]),
                Insn(0x0A, "LD",  AddrMode.RegisterIndirect, [Load("A")]),   // LD A,(BC)
                Insn(0x3A, "LD",  AddrMode.ExtendedAddress, [Load("A")]),    // LD A,(nn)
                Insn(0x32, "LD",  AddrMode.ExtendedAddress, [Store("A")]),   // LD (nn),A
                Insn(0x02, "LD",  AddrMode.RegisterIndirect, [Store("A")]),  // LD (BC),A
                Insn(0x2A, "LD",  AddrMode.ExtendedAddress, [LoadMem16("HL")]),  // LD HL,(nn)
                Insn(0x22, "LD",  AddrMode.ExtendedAddress, [Store16("HL")]),    // LD (nn),HL
                Insn(0x09, "ADD", AddrMode.Register, [Add16("HL", "BC")]),   // ADD HL,BC
                Insn(0xE3, "EX",  AddrMode.RegisterIndirect, [ExSpHl()]),    // EX (SP),HL
                Insn(0xDB, "IN",  AddrMode.IoPortImmediate, [PortIn("A")]),  // IN A,(n)
                Insn(0xD3, "OUT", AddrMode.IoPortImmediate, [PortOut("A")]), // OUT (n),A
                Insn(0x41, "LD",  AddrMode.Register, [Transfer("C", "B")]),  // LD B,C (Register-class control)
            ];
        }

        public sealed partial class WzfCpu
        {
            private readonly IAddressSpace _bus;
            private readonly IAddressSpace _io;
            public byte Q;
            public WzfCpu(IAddressSpace bus, IAddressSpace io) { _bus = bus; _io = io; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            private byte ReadBus(uint a) { _cycles++; return _bus.Read8(a); }
            private void WriteBus(uint a, byte v) { _cycles++; _bus.Write8(a, v); }
            private byte ReadIo(uint p) { _cycles++; return _io.Read8(p); }
            private void WriteIo(uint p, byte v) { _cycles++; _io.Write8(p, v); }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
        }
        """;

    private static (object Cpu, Type T, IAddressSpace Bus, IAddressSpace Io) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.WzfCpu");
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        var io = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
        io.MapMemory(0x0000, new byte[0x10000], writable: true);
        var cpu = System.Activator.CreateInstance(t, new object[] { bus, io })!;
        return (cpu, t, bus, io);
    }
    private static void Set(object cpu, Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });
    private static ulong Get(object cpu, Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;

    [Fact]
    public void JP_nn_sets_WZ_to_nn()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xC3); bus.Write8(1, 0x34); bus.Write8(2, 0x12);   // JP 0x1234
        Set(cpu, t, "PC", 0); Set(cpu, t, "WZ", 0xFFFF);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x1234u, (uint)Get(cpu, t, "WZ"));   // WZ = nn
        Assert.Equal(0x1234u, (uint)Get(cpu, t, "PC"));
    }

    [Fact]
    public void CALL_nn_sets_WZ_to_nn()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xCD); bus.Write8(1, 0x78); bus.Write8(2, 0x56);   // CALL 0x5678
        Set(cpu, t, "PC", 0); Set(cpu, t, "SP", 0xFFFE); Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x5678u, (uint)Get(cpu, t, "WZ"));   // WZ = nn
    }

    [Fact]
    public void RST_sets_WZ_to_vector()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xC7);                            // RST 00
        Set(cpu, t, "PC", 0); Set(cpu, t, "SP", 0xFFFE); Set(cpu, t, "WZ", 0xABCD);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x0000u, (uint)Get(cpu, t, "WZ"));   // WZ = n = 0
    }

    [Fact]
    public void JR_d_sets_WZ_to_dest()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0x18); bus.Write8(1, 0x05);       // JR +5 → dest = 0x07
        Set(cpu, t, "PC", 0); Set(cpu, t, "WZ", 0xFFFF);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x0007u, (uint)Get(cpu, t, "PC"));
        Assert.Equal(0x0007u, (uint)Get(cpu, t, "WZ"));   // WZ = dest
    }

    [Fact]
    public void LD_A_BC_sets_WZ_to_BC_plus_1()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0x0A);                            // LD A,(BC)
        bus.Write8(0x2000, 0x77);
        Set(cpu, t, "PC", 0); Set(cpu, t, "BC", 0x2000); Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x77, (byte)Get(cpu, t, "A"));
        Assert.Equal(0x2001u, (uint)Get(cpu, t, "WZ"));   // WZ = BC + 1
    }

    [Fact]
    public void LD_A_nn_sets_WZ_to_nn_plus_1()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0x3A); bus.Write8(1, 0x00); bus.Write8(2, 0x40);   // LD A,(0x4000)
        bus.Write8(0x4000, 0x99);
        Set(cpu, t, "PC", 0); Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x99, (byte)Get(cpu, t, "A"));
        Assert.Equal(0x4001u, (uint)Get(cpu, t, "WZ"));   // WZ = nn + 1
    }

    [Fact]
    public void LD_nn_A_sets_WZ_to_A_high_and_nn_plus_1_low()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0x32); bus.Write8(1, 0x85); bus.Write8(2, 0x12);   // LD (0x1285),A
        Set(cpu, t, "PC", 0); Set(cpu, t, "A", 0x97); Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x97, bus.Read8(0x1285));
        // WZ = (A << 8) | ((nn + 1) & 0xFF) = (0x97 << 8) | ((0x1285 + 1) & 0xFF) = 0x9786
        Assert.Equal(0x9786u, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void LD_BC_A_sets_WZ_to_A_high_and_BC_plus_1_low()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0x02);                            // LD (BC),A
        Set(cpu, t, "PC", 0); Set(cpu, t, "A", 0x66); Set(cpu, t, "BC", 0x789F);
        Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x66, bus.Read8(0x789F));
        // WZ = (A << 8) | ((BC + 1) & 0xFF) = (0x66 << 8) | ((0x789F + 1) & 0xFF) = 0x66A0
        Assert.Equal(0x66A0u, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void LD_HL_nn_sets_WZ_to_nn_plus_1()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0x2A); bus.Write8(1, 0x10); bus.Write8(2, 0x50);   // LD HL,(0x5010)
        bus.Write8(0x5010, 0x34); bus.Write8(0x5011, 0x12);
        Set(cpu, t, "PC", 0); Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x1234u, (uint)Get(cpu, t, "HL"));
        Assert.Equal(0x5011u, (uint)Get(cpu, t, "WZ"));   // WZ = nn + 1
    }

    [Fact]
    public void LD_nn_HL_sets_WZ_to_nn_plus_1()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0x22); bus.Write8(1, 0x00); bus.Write8(2, 0x40);   // LD (0x4000),HL
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0xBEEF); Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xEF, bus.Read8(0x4000)); Assert.Equal(0xBE, bus.Read8(0x4001));
        Assert.Equal(0x4001u, (uint)Get(cpu, t, "WZ"));   // WZ = nn + 1
    }

    [Fact]
    public void ADD_HL_BC_sets_WZ_to_preop_HL_plus_1()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0x09);                            // ADD HL,BC
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0xB015); Set(cpu, t, "BC", 0x0001);
        Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xB016u, (uint)Get(cpu, t, "HL"));
        Assert.Equal(0xB016u, (uint)Get(cpu, t, "WZ"));   // WZ = pre-op HL + 1 = 0xB015 + 1
    }

    [Fact]
    public void EX_SP_HL_sets_WZ_to_new_HL()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xE3);                            // EX (SP),HL
        bus.Write8(0xFF00, 0xE4); bus.Write8(0xFF01, 0xE8);   // word at (SP) = 0xE8E4
        Set(cpu, t, "PC", 0); Set(cpu, t, "SP", 0xFF00); Set(cpu, t, "HL", 0x1234);
        Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0xE8E4u, (uint)Get(cpu, t, "HL"));
        Assert.Equal(0xE8E4u, (uint)Get(cpu, t, "WZ"));   // WZ = the new HL (post-exchange)
    }

    [Fact]
    public void IN_A_n_sets_WZ_to_A_high_and_n_plus_1_low()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xDB); bus.Write8(1, 0xF9);       // IN A,(0xF9)
        io.Write8((0xE3 << 8) | 0xF9, 0x55);            // port (A<<8)|n
        Set(cpu, t, "PC", 0); Set(cpu, t, "A", 0xE3); Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x55, (byte)Get(cpu, t, "A"));
        // WZ = (preA << 8) | ((n + 1) & 0xFF) = (0xE3 << 8) | 0xFA = 0xE3FA
        Assert.Equal(0xE3FAu, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void OUT_n_A_sets_WZ_to_A_high_and_n_plus_1_low()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0xD3); bus.Write8(1, 0x9F);       // OUT (0x9F),A
        Set(cpu, t, "PC", 0); Set(cpu, t, "A", 0x66); Set(cpu, t, "WZ", 0x0000);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x66, io.Read8((0x66 << 8) | 0x9F));
        // WZ = (A << 8) | ((n + 1) & 0xFF) = (0x66 << 8) | 0xA0 = 0x66A0
        Assert.Equal(0x66A0u, (uint)Get(cpu, t, "WZ"));
    }

    [Fact]
    public void LD_B_C_leaves_WZ_unchanged_and_Q_zero()
    {
        var (cpu, t, bus, io) = Build();
        bus.Write8(0, 0x41);                            // LD B,C (Register class)
        Set(cpu, t, "PC", 0); Set(cpu, t, "C", 0x5A); Set(cpu, t, "WZ", 0x22B2);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x5A, (byte)Get(cpu, t, "B"));
        Assert.Equal(0x22B2u, (uint)Get(cpu, t, "WZ"));   // WZ UNCHANGED
        Assert.Equal((byte)0, (byte)t.GetField("Q")!.GetValue(cpu)!);   // shared-class Q = 0
    }
}
