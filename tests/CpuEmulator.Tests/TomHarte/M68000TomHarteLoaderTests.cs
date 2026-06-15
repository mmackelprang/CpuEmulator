using System.Text.Json;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class M68000TomHarteLoaderTests
{
    // One case in the upstream-pinned shape (Task 1 recon, SingleStepTests/680x0 68000/v1):
    // separate usp/ssp, 16-bit sr, 2-word prefetch (initial + final), ram as [addr, value] pairs,
    // a top-level "length" (total cycles), and transactions as either ["n", cycles] (an idle slot,
    // length 2) or [dir, cycles, fc, addr, sizeTag, value] (a bus access, length 6). Field 2 is the
    // per-slot CYCLE COUNT (confirmed: the case "length" == sum of field 2); size tags are .b/.w only
    // (the 68000 bus is 16-bit, so a .l access decomposes into two .w transactions — no .l at bus level).
    private const string OneCase = """
        [{
          "name": "ADD.w sample",
          "initial": {
            "d0": 1, "d1": 2, "d2": 0, "d3": 0, "d4": 0, "d5": 0, "d6": 0, "d7": 0,
            "a0": 4096, "a1": 0, "a2": 0, "a3": 0, "a4": 0, "a5": 0, "a6": 0,
            "usp": 16384, "ssp": 32768, "sr": 8192, "pc": 1024,
            "prefetch": [53328, 0],
            "ram": [[1024, 208], [1025, 65]]
          },
          "final": {
            "d0": 3, "d1": 2, "d2": 0, "d3": 0, "d4": 0, "d5": 0, "d6": 0, "d7": 0,
            "a0": 4096, "a1": 0, "a2": 0, "a3": 0, "a4": 0, "a5": 0, "a6": 0,
            "usp": 16384, "ssp": 32768, "sr": 8192, "pc": 1028,
            "prefetch": [0, 1],
            "ram": [[1024, 208], [1025, 65]]
          },
          "length": 8,
          "transactions": [
            ["n", 2],
            ["r", 4, 6, 1024, ".w", 53328],
            ["r", 2, 6, 1026, ".w", 1]
          ]
        }]
        """;

    [Fact]
    public void Parses_the_full_68000_case_shape()
    {
        using var doc = JsonDocument.Parse(OneCase);
        var c = M68000TomHarteLoader.Parse(doc.RootElement.EnumerateArray().First());

        Assert.Equal("ADD.w sample", c.Name);
        Assert.Equal(1u, c.Initial.D[0]);
        Assert.Equal(2u, c.Initial.D[1]);
        Assert.Equal(4096u, c.Initial.A[0]);
        Assert.Equal(16384u, c.Initial.Usp);
        Assert.Equal(32768u, c.Initial.Ssp);
        Assert.Equal((ushort)8192, c.Initial.Sr);
        Assert.Equal(1024u, c.Initial.Pc);
        Assert.Equal((ushort)53328, c.Initial.Prefetch[0]);
        Assert.Equal((ushort)0, c.Initial.Prefetch[1]);
        Assert.Equal(2, c.Initial.Ram.Length);
        Assert.Equal(1024u, c.Initial.Ram[0].Address);
        Assert.Equal((byte)208, c.Initial.Ram[0].Value);

        // Final prefetch is DIFFERENT from initial (the load-bearing new dimension; asserted in M4.5).
        Assert.Equal((ushort)0, c.Final.Prefetch[0]);
        Assert.Equal((ushort)1, c.Final.Prefetch[1]);
        Assert.Equal(3u, c.Final.D[0]);
        Assert.Equal(1028u, c.Final.Pc);

        // The top-level "length" (total instruction cycles) is carried.
        Assert.Equal(8, c.Length);

        // Three transactions: one idle ("n") slot then two word reads.
        Assert.Equal(3, c.Transactions.Length);

        var idle = c.Transactions[0];
        Assert.True(idle.IsIdle);
        Assert.False(idle.IsRead);
        Assert.Equal(2, idle.Cycles);   // the idle slot's cycle count (Field 2)

        var t1 = c.Transactions[1];
        Assert.False(t1.IsIdle);
        Assert.True(t1.IsRead);
        Assert.Equal(1024u, t1.Address);
        Assert.Equal(".w", t1.SizeTag);
        Assert.Equal(53328u, t1.Value);
        Assert.Equal(4, t1.Cycles);     // the bus slot's cycle count (Field 2)
        Assert.Equal(6, t1.FunctionCode);
    }
}
