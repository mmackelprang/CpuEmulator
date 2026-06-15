using System.Text.Json;
using CpuEmulator.Tests.TomHarte;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class M68000TomHarteRunnerTests
{
    // MOVE.w D0,D1 (0x3200) at PC 0x1000; D0 = 0x1234. The only transaction is the operword fetch (one .w
    // read at 0x1000 worth 4 cycles). final: D1 = 0x1234, PC = 0x1002, CCR (Z clear, N clear). length = 4.
    private const string MoveCase = """
        [{
          "name": "MOVE.w D0,D1",
          "initial": { "d0":4660,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                       "a0":0,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                       "usp":0,"ssp":0,"sr":0,"pc":4096,
                       "prefetch":[12800,0],"ram":[[4096,50],[4097,0]] },
          "final":   { "d0":4660,"d1":4660,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                       "a0":0,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                       "usp":0,"ssp":0,"sr":0,"pc":4098,
                       "prefetch":[0,0],"ram":[[4096,50],[4097,0]] },
          "length": 4,
          "transactions": [["r",4,6,4096,".w",12800]]
        }]
        """;

    [Fact]
    public void Runs_a_synthetic_MOVE_case_green()
    {
        using var doc = JsonDocument.Parse(MoveCase);
        var c = M68000TomHarteLoader.Parse(doc.RootElement.EnumerateArray().First());
        // Full-axis (timingAxis: true) — this synthetic case has a self-consistent prefetch/PC/trace/cycle, so
        // it exercises BOTH the M4.5a data axis and the M4.5d timing axis the USP families also satisfy.
        string? result = M68000TomHarteRunner.RunCase(c, timingAxis: true);
        Assert.Null(result);   // null = pass (regs + RAM + PC + trace + cycles all matched)
    }
}
