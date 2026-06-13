using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>
/// The packaging-law build check (spec §3 "load-bearing packaging rule"). The AOT-clean runtime-graph
/// assemblies — Core, Cpus.Mos6502, Peripherals, Monitor, Host — must NEVER reference
/// CpuEmulator.Jit (the only assembly that uses Reflection.Emit, the only non-AOT runtime-graph
/// member) NOR CpuEmulator.Benchmarks (the dev-tool bench core, which references Jit + is never in any
/// shipped graph). This pins the rule by reflecting over each assembly's referenced assemblies; it
/// needs no NativeAOT host.
///
/// M2-ii Task 10: the actual NativeAOT publish of the Host SUCCEEDS once PublishAot is scoped to the
/// Host csproj (not passed as a global -p:PublishAot=true, which propagates to the netstandard2.0
/// CpuEmulator.Generators analyzer + fails NETSDK1207). The recorded command + result is in
/// docs/user-guide/jit.md; this reference-graph test is the enforced-by-construction backstop.
/// </summary>
public class AotCleanlinessTests
{
    [Theory]
    [InlineData("CpuEmulator.Core")]
    [InlineData("CpuEmulator.Cpus.Mos6502")]
    [InlineData("CpuEmulator.Peripherals")]
    [InlineData("CpuEmulator.Monitor")]
    [InlineData("CpuEmulator.Host")]
    public void Aot_clean_assemblies_do_not_reference_the_JIT(string assemblyName)
    {
        var asm = System.Reflection.Assembly.Load(assemblyName);
        Assert.DoesNotContain(asm.GetReferencedAssemblies(),
            a => a.Name == "CpuEmulator.Jit");
    }

    [Theory]
    [InlineData("CpuEmulator.Core")]
    [InlineData("CpuEmulator.Cpus.Mos6502")]
    [InlineData("CpuEmulator.Peripherals")]
    [InlineData("CpuEmulator.Monitor")]
    [InlineData("CpuEmulator.Host")]
    public void Aot_clean_assemblies_do_not_reference_the_bench_tool(string assemblyName)
    {
        var asm = System.Reflection.Assembly.Load(assemblyName);
        Assert.DoesNotContain(asm.GetReferencedAssemblies(),
            a => a.Name == "CpuEmulator.Benchmarks");
    }
}
