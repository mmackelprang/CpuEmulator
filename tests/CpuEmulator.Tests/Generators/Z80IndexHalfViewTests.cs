using CpuEmulator.Core;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M3.4e-1a — the IXh/IXl/IYh/IYl half-view shape (the D2 storage inversion, RECON-FINDING A1).
/// Declaring the 8-bit halves makes them the only STORAGE and turns IX/IY into computed pair-VIEWS
/// over them — the inverse of today's bare-16-bit IX/IY. This proves the shape on the EXISTING
/// pair-view machinery (CpuEmitter.cs:61-69) BEFORE the real z80-semantics.json regen (Task 4): the
/// halves must be declared FIRST (the H/L-before-HL convention the emitter relies on), and IX must
/// round-trip bidirectionally through GetRegister/SetRegister. Green on arrival means the real Task 4
/// regen's storage inversion is sound; the runner (which only ever names "IX"/"IY") sees no change.
///
/// Uses the degenerate (no-DecodeStructure) register-file fixture shape from RegisterPairAliasingTests
/// — a pure register-file proof needs no structured decoder, and a declared prefix with no prefixed
/// Insn row would (correctly) trip CPUGEN012. The real spec (Task 4) carries the prefixed rows.
/// </summary>
public class Z80IndexHalfViewTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ixh")]
        public static class IxhSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status),
                new("IXh", 8), new("IXl", 8),   // halves FIRST (the H/L-before-HL convention, A1)
                new("IYh", 8), new("IYl", 8),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("IX", 16, HighHalf: "IXh", LowHalf: "IXl"),   // IX is now a VIEW (storage = halves)
                new("IY", 16, HighHalf: "IYh", LowHalf: "IYl"),
            ];
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0x00, "NOP", AddrMode.Implied, []),
            ];
        }

        public sealed partial class IxhCpu
        {
            private readonly IAddressSpace _bus;
            public IxhCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            private byte ReadBus(uint a) { _cycles++; return _bus.Read8(a); }
            private void WriteBus(uint a, byte v) { _cycles++; _bus.Write8(a, v); }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    private static dynamic NewCpu()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.IxhCpu");
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);
        return System.Activator.CreateInstance(t, (IAddressSpace)bus)!;
    }

    [Fact]
    public void IX_and_IY_emit_as_view_properties_over_their_halves()
    {
        var result = GeneratorTestHost.Run(Source);
        Assert.Empty(result.AllErrors);
        // Computed PROPERTIES over the 8-bit halves — NOT "public ushort IX;" fields (storage moved).
        Assert.Contains(
            "public ushort IX { get => (ushort)((IXh << 8) | IXl); set { IXh = (byte)(value >> 8); IXl = (byte)value; } }",
            result.GeneratedText);
        Assert.Contains(
            "public ushort IY { get => (ushort)((IYh << 8) | IYl); set { IYh = (byte)(value >> 8); IYl = (byte)value; } }",
            result.GeneratedText);
        Assert.DoesNotContain("public ushort IX;", result.GeneratedText);
        Assert.DoesNotContain("public ushort IY;", result.GeneratedText);
    }

    [Fact]
    public void Writing_IX_reflects_into_its_halves()
    {
        var cpu = NewCpu();
        cpu.SetRegister("IX", (ulong)0xABCD);
        Assert.Equal((ulong)0xAB, cpu.GetRegister("IXh"));   // high half
        Assert.Equal((ulong)0xCD, cpu.GetRegister("IXl"));   // low half
    }

    [Fact]
    public void Writing_a_half_reflects_into_IX()
    {
        var cpu = NewCpu();
        cpu.SetRegister("IX", (ulong)0x0000);
        cpu.SetRegister("IXh", (ulong)0x12);
        cpu.SetRegister("IXl", (ulong)0x34);
        Assert.Equal((ulong)0x1234, cpu.GetRegister("IX"));   // the pair view reflects both halves
    }

    [Fact]
    public void IY_round_trips_through_its_halves_both_ways()
    {
        var cpu = NewCpu();
        cpu.SetRegister("IY", (ulong)0xBEEF);
        Assert.Equal((ulong)0xBE, cpu.GetRegister("IYh"));
        Assert.Equal((ulong)0xEF, cpu.GetRegister("IYl"));
        cpu.SetRegister("IYh", (ulong)0xC0);
        cpu.SetRegister("IYl", (ulong)0xDE);
        Assert.Equal((ulong)0xC0DE, cpu.GetRegister("IY"));
    }
}
