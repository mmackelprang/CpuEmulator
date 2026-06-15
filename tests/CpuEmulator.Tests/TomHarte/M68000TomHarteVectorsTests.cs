using CpuEmulator.Tests.TomHarte;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class M68000TomHarteVectorsTests
{
    [Fact]
    public void Resolver_returns_null_when_the_680x0_directory_is_absent()
    {
        string prev = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS") ?? "";
        try
        {
            // Point the cache at an empty temp dir -> no 680x0/v1 -> null.
            string empty = Path.Combine(Path.GetTempPath(), $"no-vectors-{Guid.NewGuid():N}");
            Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", empty);
            Assert.Null(M68000TomHarteVectors.TryGetVectorDirectory());
        }
        finally { Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", prev.Length == 0 ? null : prev); }
    }

    [Fact]
    public void Resolver_finds_a_present_680x0_directory()
    {
        string prev = Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS") ?? "";
        try
        {
            string root = Path.Combine(Path.GetTempPath(), $"vectors-{Guid.NewGuid():N}");
            string dir = Path.Combine(root, "680x0", "v1");
            Directory.CreateDirectory(dir);
            Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", root);
            Assert.Equal(dir, M68000TomHarteVectors.TryGetVectorDirectory());
            Directory.Delete(root, recursive: true);
        }
        finally { Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", prev.Length == 0 ? null : prev); }
    }
}
