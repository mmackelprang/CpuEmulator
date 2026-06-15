using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M3.5-1 (Task 6a) — proves the generator routes the <c>Ei</c> micro-op body through the
/// partial hook <c>OnInterruptEnable()</c> (structured CPUs only) rather than writing <c>_iff1</c>
/// directly, so the hand-written partial can own the Z80 EI one-instruction-delay latch. A synthetic
/// structured spec (it declares a DecodeStructure, so the <c>partial void OnInterruptEnable()</c>
/// declaration is emitted) with a single <c>EI</c> row + a partial that records the hook call drives
/// the generated body at runtime and asserts the hook fired. The 6502 (no DecodeStructure, no EI row)
/// never declares the partial nor emits the call, so its <c>.g.cs</c> is byte-identical.</summary>
public class Z80EiDelayTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("eid")]
        public static class EidSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status),
                new("SP", 16, RegisterRole.StackPointer),
                new("PC", 16, RegisterRole.ProgramCounter),
                new("WZ", 16),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xFB, "EI", AddrMode.Implied, [Ei()]),
            ];
        }

        public sealed partial class EidCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public bool _iff1, _iff2;
            public bool HookCalled;
            public EidCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            partial void OnInterruptEnable() { HookCalled = true; }
            private byte ReadBus(uint a) { _cycles++; return _bus.Read8(a); }
            private void WriteBus(uint a, byte v) { _cycles++; _bus.Write8(a, v); }
            private void HandleUndefinedOpcode(byte op) { }
        }
        """;

    [Fact]
    public void EI_body_routes_through_the_OnInterruptEnable_hook()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EidCpu");
        var program = new CpuEmulator.Core.AddressSpace(CpuEmulator.Core.AddressSpaceKind.Program, 16);
        program.MapMemory(0, new byte[0x10000], writable: true);
        program.Write8(0, 0xFB);   // EI
        var cpu = System.Activator.CreateInstance(t, new object[] { program })!;
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { "PC", 0UL });
        t.GetMethod("Step")!.Invoke(cpu, null);
        // The generated Ei body called OnInterruptEnable() instead of setting _iff1 directly.
        Assert.True((bool)t.GetField("HookCalled")!.GetValue(cpu)!);
    }
}
