using System.Collections.Generic;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class TomHarteParseCacheTests
{
    [Fact]
    public void Second_request_at_same_or_smaller_size_does_not_reparse()
    {
        int parses = 0;
        var cache = new TomHarteParseCache<int>();
        List<int> Parse(int max) { parses++; var l = new List<int>(); for (int i = 0; i < max; i++) l.Add(i); return l; }

        var a = cache.Get("k", 100, Parse);
        var b = cache.Get("k", 100, Parse);
        var c = cache.Get("k", 50, Parse);   // smaller — served from the cached 100

        Assert.Equal(1, parses);             // parsed ONCE
        Assert.Equal(100, a.Count);
        Assert.Same(a, b);                   // same backing list returned
        Assert.Equal(100, c.Count);          // smaller request still returns the wider cached list (caller caps)
    }

    [Fact]
    public void Larger_request_reparses_and_upgrades_the_high_water_mark()
    {
        int parses = 0;
        var cache = new TomHarteParseCache<int>();
        List<int> Parse(int max) { parses++; var l = new List<int>(); for (int i = 0; i < max; i++) l.Add(i); return l; }

        cache.Get("k", 100, Parse);
        var big = cache.Get("k", 500, Parse); // larger — re-parses to 500
        cache.Get("k", 200, Parse);           // now served from the cached 500

        Assert.Equal(2, parses);              // 100 then 500; the 200 is a hit
        Assert.Equal(500, big.Count);
    }
}
