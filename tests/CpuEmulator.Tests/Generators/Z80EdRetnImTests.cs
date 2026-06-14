using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EdRetnImTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("edretnim")]
        public static class EdRetnImSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
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
                Insn(0xED, 0x45, "RETN", AddrMode.Implied, [EdRetn(false)]),
                Insn(0xED, 0x4D, "RETI", AddrMode.Implied, [EdRetn(true)]),
                Insn(0xED, 0x46, "IM",  AddrMode.Implied, [EdIm(0)]),
                Insn(0xED, 0x56, "IM",  AddrMode.Implied, [EdIm(1)]),
                Insn(0xED, 0x5E, "IM",  AddrMode.Implied, [EdIm(2)]),
            ];
        }

        public sealed partial class EdRetnImCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            private bool _iff1, _iff2;
            public bool Iff1 { get => _iff1; set => _iff1 = value; }
            public bool Iff2 { get => _iff2; set => _iff2 = value; }
            public int Im;
            public EdRetnImCpu(IAddressSpace bus) { _bus = bus; }
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
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EdRetnImCpu");
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
    public void RETN_pops_PC_copies_IFF2_into_IFF1_sets_WZ()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x45);                 // RETN
        mem.Write8(0xFFFE, 0x34); mem.Write8(0xFFFF, 0x12);       // return addr 0x1234 on stack
        Set(cpu, t, "PC", 0); Set(cpu, t, "SP", 0xFFFE);
        t.GetProperty("Iff1")!.SetValue(cpu, true);
        t.GetProperty("Iff2")!.SetValue(cpu, false);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x1234u, (uint)Get(cpu, t, "PC"));
        Assert.False((bool)t.GetProperty("Iff1")!.GetValue(cpu)!);   // IFF1 = IFF2 = false
        Assert.Equal(0x1234u, (uint)Get(cpu, t, "WZ"));              // WZ = popped PC
    }

    [Fact]
    public void RETI_also_copies_IFF2_into_IFF1()
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, 0x4D);                 // RETI
        mem.Write8(0xFFFE, 0x78); mem.Write8(0xFFFF, 0x56);
        Set(cpu, t, "PC", 0); Set(cpu, t, "SP", 0xFFFE);
        t.GetProperty("Iff1")!.SetValue(cpu, false);
        t.GetProperty("Iff2")!.SetValue(cpu, true);
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(0x5678u, (uint)Get(cpu, t, "PC"));
        Assert.True((bool)t.GetProperty("Iff1")!.GetValue(cpu)!);    // IFF1 = IFF2 = true
    }

    [Theory]
    [InlineData(0x46, 0)]
    [InlineData(0x56, 1)]
    [InlineData(0x5E, 2)]
    public void IM_sets_the_interrupt_mode(int op2, int mode)
    {
        var (cpu, t, mem) = Build();
        mem.Write8(0, 0xED); mem.Write8(1, (byte)op2);
        Set(cpu, t, "PC", 0);
        t.GetField("Im")!.SetValue(cpu, 7);          // seed a stale mode
        t.GetMethod("Step")!.Invoke(cpu, null);
        Assert.Equal(mode, (int)t.GetField("Im")!.GetValue(cpu)!);
    }
}
