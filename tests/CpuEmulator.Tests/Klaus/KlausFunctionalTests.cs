using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.Klaus;

public class KlausFunctionalTests(ITestOutputHelper output)
{
    /// <summary>PC of the success trap (`jmp *`) in the standard pre-assembled
    /// 6502_functional_test.bin (default build) — verified against the listing shipped
    /// beside the binary (bin_files/6502_functional_test.lst, label `success`).</summary>
    private const ushort SuccessTrap = 0x3469;

    private const ushort StartAddress = 0x0400;
    private const long CycleBudget = 500_000_000; // a passing run needs ~96M cycles

    [KlausFact]
    public void Functional_test_runs_to_the_success_trap()
    {
        byte[] image = File.ReadAllBytes(KlausVectors.TryGetBinaryPath()!);
        Assert.Equal(0x10000, image.Length); // full 64 KiB image, loaded at $0000

        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, image, writable: true); // the test self-modifies RAM
        var cpu = new Mos6502Cpu(space);
        cpu.PC = StartAddress;
        cpu.S = 0xFD;  // power-up-ish; the test initializes its own stack anyway
        cpu.P = 0x34;

        while (cpu.CycleCount < CycleBudget)
        {
            ushort before = cpu.PC;
            cpu.Step();
            if (cpu.PC == before) // trap idiom: jmp * / branch-to-self parks PC
            {
                if (cpu.PC == SuccessTrap)
                {
                    output.WriteLine($"success trap reached after {cpu.CycleCount} cycles");
                    return;
                }
                Assert.Fail(TrapReport(cpu, space));
            }
        }
        Assert.Fail($"cycle budget ({CycleBudget}) exhausted without trapping — " +
                    $"PC=0x{cpu.PC:X4} after {cpu.CycleCount} cycles");
    }

    private static string TrapReport(Mos6502Cpu cpu, AddressSpace space)
    {
        ushort pc = cpu.PC;
        string disassembly = Mos6502Cpu.Disassemble(
            space.Read8(pc), space.Read8((uint)((pc + 1) & 0xFFFF)), space.Read8((uint)((pc + 2) & 0xFFFF)));
        return $"trapped at 0x{pc:X4} ({disassembly}) after {cpu.CycleCount} cycles — " +
               $"test byte $0200=0x{space.Read8(0x0200):X2}, " +
               $"A=0x{cpu.A:X2} X=0x{cpu.X:X2} Y=0x{cpu.Y:X2} S=0x{cpu.S:X2} P=0x{cpu.P:X2}";
    }
}
