using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdMiscTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edmisc")]
        public static class EdMiscSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("I", 8), new("R", 8),
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xED)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xED, 0x47, "LD", AddrMode.Implied, [EdLdIaRa("I_A")]),
                Insn(0xED, 0x57, "LD", AddrMode.Implied, [EdLdIaRa("A_I")]),
                Insn(0xED, 0x67, "RRD", AddrMode.RegisterIndirect, [EdRrdRld(false)]),
                Insn(0xED, 0x6F, "RLD", AddrMode.RegisterIndirect, [EdRrdRld(true)]),
            ];
        }

        public sealed partial class EdMiscCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            private bool _iff1, _iff2;
            public bool Iff1 { get => _iff1; set => _iff1 = value; }
            public bool Iff2 { get => _iff2; set => _iff2 = value; }
            public EdMiscCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            private byte ReadBus(uint a) { _cycles++; return _bus.Read8(a); }
            private void WriteBus(uint a, byte v) { _cycles++; _bus.Write8(a, v); }
            private byte ReadIo(uint p) => 0;
            private void WriteIo(uint p, byte v) { }
            private void HandleUndefinedOpcode(byte op) { }
        }
        """;

    private static (object Cpu, Type T, IAddressSpace Mem) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EdMiscCpu");
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
    public void LD_A_I_sets_PV_from_IFF2()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x57);                 // LD A,I
        Set(cpu, t, "PC", 0); Set(cpu, t, "I", 0x42); Set(cpu, t, "F", 0x01); // C preserved
        t.GetProperty("Iff2")!.SetValue(cpu, true);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x42, (byte)Get(cpu, t, "A"));
        byte f = (byte)Get(cpu, t, "F");
        Assert.Equal(0x04, f & 0x04);                 // P/V = IFF2 = 1
        Assert.Equal(0x01, f & 0x01);                 // C preserved
        Assert.Equal(0x00, f & 0x40);                 // Z = 0 (value 0x42)
    }

    [Fact]
    public void LD_A_I_clears_PV_when_IFF2_clear()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x57);
        Set(cpu, t, "PC", 0); Set(cpu, t, "I", 0x42); Set(cpu, t, "F", 0x00);
        t.GetProperty("Iff2")!.SetValue(cpu, false);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x00, (byte)Get(cpu, t, "F") & 0x04);   // P/V = 0
    }

    [Fact]
    public void LD_I_A_copies_A_to_I_no_flags()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x47);                 // LD I,A
        Set(cpu, t, "PC", 0); Set(cpu, t, "A", 0x99); Set(cpu, t, "F", 0x5A);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x99, (byte)Get(cpu, t, "I"));
        Assert.Equal(0x5A, (byte)Get(cpu, t, "F"));   // F unchanged (LD I,A sets no flags)
    }

    [Fact]
    public void RRD_rotates_nibbles_and_sets_WZ()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x67);                 // RRD
        mem.Write8(0x4000, 0x34);                                 // (HL)
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "A", 0x12);
        t.GetMethod("Step")!.Invoke(cpu, null);
        // RRD: A = 0x12, (HL) = 0x34 -> A = 0x14, (HL) = 0x23
        Assert.Equal(0x14, (byte)Get(cpu, t, "A"));
        Assert.Equal(0x23, mem.Read8(0x4000));
        Assert.Equal(0x4001u, (uint)Get(cpu, t, "WZ"));   // WZ = HL + 1
    }

    [Fact]
    public void RLD_rotates_nibbles_other_direction()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x6F);                 // RLD
        mem.Write8(0x4000, 0x34);                                 // (HL)
        Set(cpu, t, "PC", 0); Set(cpu, t, "HL", 0x4000); Set(cpu, t, "A", 0x12);
        t.GetMethod("Step")!.Invoke(cpu, null);
        // RLD: (HL) = ((0x34 << 4) | 0x2) & 0xFF = 0x42 ; A = (0x12 & 0xF0) | (0x34 >> 4) = 0x13
        Assert.Equal(0x13, (byte)Get(cpu, t, "A"));
        Assert.Equal(0x42, mem.Read8(0x4000));
        Assert.Equal(0x4001u, (uint)Get(cpu, t, "WZ"));   // WZ = HL + 1
    }
}
