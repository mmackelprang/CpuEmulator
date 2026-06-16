using System.Text.Json;
using CpuEmulator.Tests.TomHarte;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class M68000TomHarteRunnerTests
{
    // MOVE.w D0,D1 (0x3200) at PC 0x1000; D0 = 0x1234. A one-word, register-to-register MOVE: the queue
    // (M4.5d-2a) starts as [0x3200 (operword @ 0x1000), 0xABCD (@ 0x1002)] and, consuming the single operword,
    // advances+refills to the queue END STATE [word@0x1002, word@0x1004] = [0xABCD, 0xBEEF]. final: D1 = 0x1234,
    // PC = 0x1002, final.prefetch = [0xABCD, 0xBEEF]. (0xABCD=43981, 0xBEEF=48879; 0x1234=4660; 0x3200=12800.)
    // RAM holds the post-operword words at 0x1002/0x1004 so the queue's refills read them deterministically.
    private const string MoveCase = """
        [{
          "name": "MOVE.w D0,D1",
          "initial": { "d0":4660,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                       "a0":0,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                       "usp":0,"ssp":0,"sr":0,"pc":4096,
                       "prefetch":[12800,43981],
                       "ram":[[4096,50],[4097,0],[4098,171],[4099,205],[4100,190],[4101,239]] },
          "final":   { "d0":4660,"d1":4660,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                       "a0":0,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                       "usp":0,"ssp":0,"sr":0,"pc":4098,
                       "prefetch":[43981,48879],
                       "ram":[[4096,50],[4097,0],[4098,171],[4099,205],[4100,190],[4101,239]] },
          "length": 4,
          "transactions": [["r",4,6,4100,".w",48879]]
        }]
        """;

    [Fact]
    public void Runs_a_synthetic_MOVE_case_green()
    {
        using var doc = JsonDocument.Parse(MoveCase);
        var c = M68000TomHarteLoader.Parse(doc.RootElement.EnumerateArray().First());
        // M4.5d-2a: the PC/prefetch axis (pcPrefetchAxis: true) — asserts the data result PLUS the queue END
        // STATE (final.pc + both final.prefetch words). This synthetic one-word MOVE has a self-consistent
        // queue: it advances past the operword and refills to [word@final.pc, word@final.pc+2]. (The full
        // per-transaction trace + CycleCount == length is M4.5d-2b; 2a asserts queue state, not cycles.)
        string? result = M68000TomHarteRunner.RunCase(c, pcPrefetchAxis: true);
        Assert.Null(result);   // null = pass (regs + RAM + PC + final.prefetch all matched)
    }
}
