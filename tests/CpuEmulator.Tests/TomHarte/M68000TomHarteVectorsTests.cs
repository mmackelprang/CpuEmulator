using CpuEmulator.Tests.TomHarte;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class M68000TomHarteVectorsTests
{
    // These tests exercise the PURE path resolver with explicit temp roots and never touch the
    // process-global CPUEMULATOR_TESTVECTORS — mutating it would race the vector-gated theories
    // (e.g. Z80JitTomHarteTests) that read it from parallel xUnit threads.

    [Fact]
    public void Resolver_returns_null_when_the_680x0_directory_is_absent()
    {
        string empty = Path.Combine(Path.GetTempPath(), $"no-vectors-{Guid.NewGuid():N}");
        Assert.Null(M68000TomHarteVectors.ResolveVectorDirectory(empty));
    }

    [Fact]
    public void Resolver_finds_a_present_680x0_directory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"vectors-{Guid.NewGuid():N}");
        string dir = Path.Combine(root, "680x0", "v1");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Equal(dir, M68000TomHarteVectors.ResolveVectorDirectory(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
