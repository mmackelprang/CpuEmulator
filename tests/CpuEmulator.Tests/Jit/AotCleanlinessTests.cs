using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>
/// Task 8: the packaging-law build check (spec §3 "load-bearing packaging rule"). The AOT-clean
/// assemblies — Core, Cpus.Mos6502, Peripherals, Monitor, Host — must NEVER reference
/// CpuEmulator.Jit (the only assembly that uses Reflection.Emit and is therefore the only non-AOT
/// member of the build graph). This pins the rule by reflecting over each assembly's referenced
/// assemblies; it needs no NativeAOT host. The actual PublishAot smoke (a NativeAOT publish of the
/// Host succeeding because the graph excludes Jit) is a manual/CI step recorded in the closeout.
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
}
