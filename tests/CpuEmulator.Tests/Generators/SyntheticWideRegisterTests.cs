using CpuEmulator.Core;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M4.1 (ADR 0003 Decision 1) — the 32-bit register proof. A GENERATOR fixture (NOT a shipped
/// CPU) declaring a 32-bit register, compiled via GeneratorTestHost and DRIVEN at runtime: a full 32-bit
/// value round-trips through GetRegister/SetRegister. The 6502/Z80 declare only 8/16-bit registers, so
/// none of this perturbs them (byte-identical .g.cs — proven by SyntheticWideRegisterByteIdentityTests +
/// RegeneratedSpecTests).</summary>
public class SyntheticWideRegisterTests
{
    // A minimal synthetic CPU with a 32-bit data register D0, a 32-bit PC, and a 16-bit status. No
    // instructions (the register foundation is what is under test). The partial supplies the bus + hooks
    // the generator requires.
    private const string WideSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("widetest")]
        public static class WideTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("D0", 32),
                new("SR", 16, RegisterRole.Status),
                new("PC", 32, RegisterRole.ProgramCounter),
            ];

            public static readonly InstructionDef[] Instructions = [];
        }

        public sealed partial class WideTestCpu
        {
            private readonly IAddressSpace _bus;
            public WideTestCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            private byte ReadBus(uint addr) { _cycles++; return _bus.Read8(addr); }
            private void WriteBus(uint addr, byte v) { _cycles++; _bus.Write8(addr, v); }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void Spec_with_a_32bit_register_generates_with_no_diagnostics()
    {
        var result = GeneratorTestHost.Run(WideSpec);

        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n",
                result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
        // The 32-bit register's backing field is typed `uint` (Task 3 makes this true).
        Assert.Contains("public uint D0;", result.GeneratedText);
        Assert.Contains("public uint PC;", result.GeneratedText);
        // The 16-bit status stays `ushort` (the existing arm is unchanged).
        Assert.Contains("public ushort SR;", result.GeneratedText);
    }

    private static readonly Lazy<Type> s_cpu =
        new(() => GeneratorTestHost.CompileAndLoadType(WideSpec, "SyntheticCpu.WideTestCpu"));

    private static object NewCpu()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, 24);
        bus.MapMemory(0, new byte[0x1000], writable: true);
        return Activator.CreateInstance(s_cpu.Value, bus)!;
    }

    private static ulong Get(object cpu, string r) =>
        (ulong)s_cpu.Value.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;
    private static void Set(object cpu, string r, ulong v) =>
        s_cpu.Value.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });

    [Fact]
    public void D0_round_trips_a_full_32bit_value()
    {
        var cpu = NewCpu();
        Set(cpu, "D0", 0xDEADBEEFul);
        Assert.Equal(0xDEADBEEFul, Get(cpu, "D0"));   // a ushort field would give 0xBEEF
    }

    [Fact]
    public void PC_round_trips_a_full_32bit_value()
    {
        var cpu = NewCpu();
        Set(cpu, "PC", 0x00FF_FFFEul);                // a 24-bit-ish PC value, > 16 bits
        Assert.Equal(0x00FF_FFFEul, Get(cpu, "PC"));
    }

    [Fact]
    public void SetRegister_truncates_to_the_field_width_for_a_32bit_register()
    {
        var cpu = NewCpu();
        Set(cpu, "D0", 0x1_2345_6789ul);              // 33 bits in; uint field keeps the low 32
        Assert.Equal(0x2345_6789ul, Get(cpu, "D0"));
    }
}
