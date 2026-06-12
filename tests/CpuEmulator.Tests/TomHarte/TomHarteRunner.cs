using System.Text;
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Tests.Mos6502;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Executes TomHarte SingleStepTests cases against a fresh CPU with a full 64 KiB RAM and
/// a tracing bus, then diffs the result against the expected final state and cycle log.
/// </summary>
internal static class TomHarteRunner
{
    /// <summary>
    /// Executes one vector case against a fresh CPU + full-64KiB RAM with a tracing bus.
    /// Returns null on pass, or a multi-line failure report with the disassembled instruction
    /// and a cycle-by-cycle expected/actual table.
    /// </summary>
    public static string? RunCase(TomHarteCase testCase)
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        foreach (var entry in testCase.Initial.Ram)
            inner.Write8(entry.Address, entry.Value);

        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", testCase.Initial.Pc);
        cpu.SetRegister("S", testCase.Initial.S);
        cpu.SetRegister("A", testCase.Initial.A);
        cpu.SetRegister("X", testCase.Initial.X);
        cpu.SetRegister("Y", testCase.Initial.Y);
        cpu.SetRegister("P", testCase.Initial.P);

        cpu.Step();

        var problems = new List<string>();
        CheckRegister(problems, cpu, "PC", testCase.Final.Pc);
        CheckRegister(problems, cpu, "S",  testCase.Final.S);
        CheckRegister(problems, cpu, "A",  testCase.Final.A);
        CheckRegister(problems, cpu, "X",  testCase.Final.X);
        CheckRegister(problems, cpu, "Y",  testCase.Final.Y);
        CheckRegister(problems, cpu, "P",  testCase.Final.P);

        foreach (var entry in testCase.Final.Ram)
        {
            byte actual = inner.Read8(entry.Address);
            if (actual != entry.Value)
                problems.Add($"RAM[{entry.Address:X4}]: expected {entry.Value:X2}, got {actual:X2}");
        }

        if (cpu.CycleCount != testCase.Cycles.Length)
            problems.Add($"cycle count: expected {testCase.Cycles.Length}, got {cpu.CycleCount}");
        for (int i = 0; i < Math.Min(bus.Trace.Count, testCase.Cycles.Length); i++)
        {
            var expected = testCase.Cycles[i];
            var actual   = bus.Trace[i];
            if (actual.Address != expected.Address || actual.Value != expected.Value
                || actual.IsRead != expected.IsRead)
            {
                problems.Add($"bus trace diverges at cycle {i + 1} (table below)");
                break;
            }
        }

        return problems.Count == 0 ? null : Format(testCase, bus, problems);
    }

    private static void CheckRegister(
        List<string> problems, Mos6502Cpu cpu, string name, ulong expected)
    {
        ulong actual = cpu.GetRegister(name);
        if (actual != expected)
            problems.Add($"{name}: expected {expected:X2}, got {actual:X2}");
    }

    private static string Format(
        TomHarteCase testCase, TracingAddressSpace bus, List<string> problems)
    {
        byte ByteAt(ushort address) =>
            testCase.Initial.Ram.FirstOrDefault(r => r.Address == address)?.Value ?? 0;

        ushort pc = testCase.Initial.Pc;
        string disassembly = Mos6502Cpu.Disassemble(
            ByteAt(pc), ByteAt((ushort)(pc + 1)), ByteAt((ushort)(pc + 2)));

        var sb = new StringBuilder();
        sb.AppendLine($"case '{testCase.Name}' — {disassembly}");
        foreach (string problem in problems)
            sb.AppendLine($"  {problem}");
        sb.AppendLine($"  {"cycle",5}  {"expected",-14}  actual");
        for (int i = 0; i < Math.Max(testCase.Cycles.Length, bus.Trace.Count); i++)
        {
            string expected = i < testCase.Cycles.Length ? testCase.Cycles[i].ToString() : "—";
            string actual   = i < bus.Trace.Count        ? bus.Trace[i].ToString()        : "—";
            sb.AppendLine($"  {i + 1,5}  {expected,-14}  {actual}");
        }
        return sb.ToString();
    }
}
