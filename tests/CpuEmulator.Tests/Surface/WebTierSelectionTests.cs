using CpuEmulator.Machines;
using CpuEmulator.Surface.Web;
using Xunit;

namespace CpuEmulator.Tests.Surface;

/// <summary>The asset-free proof that the web surface honours the selected execution tier (feat/web-jit-tier):
/// the <c>--tier</c> / <c>?tier=</c> value parsed in <see cref="Program.Main"/> reaches the surface factories
/// and on to <see cref="BoardMachineFactory.Build"/>, so a JIT-selected machine is jitted and an
/// interpreter-selected one is not. These build via the factories DIRECTLY (no server, no cached assets), so
/// they run in CI without staging any ROM/disk. <see cref="DemoSession"/> + the surfaces are internal to the
/// Web assembly, reached here via InternalsVisibleTo("CpuEmulator.Tests").</summary>
public class WebTierSelectionTests
{
    [Fact]
    public void TryParseTier_maps_the_friendly_values_case_insensitively()
    {
        // interpreter / interp -> Interpreter; jit -> Jit; unknown -> false. The query/flag path relies on this.
        Assert.True(DemoSession.TryParseTier("interpreter", out var t1) && t1 == ExecutionTier.Interpreter);
        Assert.True(DemoSession.TryParseTier("interp", out var t2) && t2 == ExecutionTier.Interpreter);
        Assert.True(DemoSession.TryParseTier("jit", out var t3) && t3 == ExecutionTier.Jit);

        // Case-insensitive.
        Assert.True(DemoSession.TryParseTier("JIT", out var up) && up == ExecutionTier.Jit);
        Assert.True(DemoSession.TryParseTier("Interpreter", out var mix) && mix == ExecutionTier.Interpreter);

        // Unknown -> false.
        Assert.False(DemoSession.TryParseTier("turbo", out _));
        Assert.False(DemoSession.TryParseTier("", out _));
    }

    [Fact]
    public void SelectedTier_defaults_to_interpreter()
    {
        // The server-wide default before any --tier flag is parsed: the interpreter (AOT-clean, the safe floor).
        Assert.Equal(ExecutionTier.Interpreter, DemoSession.SelectedTier);
    }

    [Fact]
    public void DemoBoard_surface_is_jitted_on_the_jit_tier()
    {
        // The demo board needs no assets, so it proves the tier reaches BoardMachineFactory.Build end-to-end:
        // a JIT-selected demo machine reports IsJitted; an interpreter-selected one does not.
        Assert.True(DemoBoardSurface.Create(f => { }, ExecutionTier.Jit).Machine.IsJitted);
        Assert.False(DemoBoardSurface.Create(f => { }, ExecutionTier.Interpreter).Machine.IsJitted);
    }

    [Fact]
    public void DemoBoard_surface_defaults_to_the_interpreter()
    {
        // The default-arg overload (no tier) is interpreter — the safe floor that matches SelectedTier's default.
        Assert.False(DemoBoardSurface.Create(f => { }).Machine.IsJitted);
    }

    [Fact]
    public void Spectrum_surface_honours_the_tier_with_a_synthetic_rom()
    {
        // A synthetic 16 KiB ROM (all zeros) is a valid SHAPE for SpectrumBoard.Spec — it only length-checks the
        // ROM (must be exactly $4000 bytes), never its content — so this builds without booting. We assert ONLY
        // IsJitted, proving the tier threads through SpectrumSurface.Create -> SpectrumMachine.Build.
        byte[] rom = new byte[0x4000];   // 16 KiB, all zeros — a build-only ROM (not a bootable image)
        Assert.True(SpectrumSurface.Create(rom, f => { }, a => { }, ExecutionTier.Jit).Machine.IsJitted);
        Assert.False(SpectrumSurface.Create(rom, f => { }, a => { }, ExecutionTier.Interpreter).Machine.IsJitted);
    }
}
