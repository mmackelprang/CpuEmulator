using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class TomHarteSamplingTests
{
    [Fact]
    public void Default_is_100_when_no_env_set()
    {
        Assert.Equal(100, TomHarteSampling.ResolveSampleSize(uat: null, sample: null));
    }

    [Fact]
    public void Uat_full_is_unbounded()
    {
        Assert.Equal(int.MaxValue, TomHarteSampling.ResolveSampleSize(uat: "full", sample: null));
        Assert.Equal(int.MaxValue, TomHarteSampling.ResolveSampleSize(uat: "full", sample: "200"));  // full wins
    }

    [Fact]
    public void Explicit_sample_overrides_default()
    {
        Assert.Equal(200, TomHarteSampling.ResolveSampleSize(uat: null, sample: "200"));
    }

    [Fact]
    public void Non_positive_or_garbage_sample_falls_back_to_default()
    {
        Assert.Equal(100, TomHarteSampling.ResolveSampleSize(uat: null, sample: "0"));
        Assert.Equal(100, TomHarteSampling.ResolveSampleSize(uat: null, sample: "xyz"));
    }
}
