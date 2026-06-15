using System.Text.Json;
using CpuEmulator.Tests.TomHarte;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

public class M68000TomHarteRunnerScaffoldTests
{
    private const string OneCase = """
        [{
          "name": "scaffold case",
          "initial": { "d0":1,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                       "a0":4096,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                       "usp":16384,"ssp":32768,"sr":8192,"pc":1024,
                       "prefetch":[53328,0],"ram":[[1024,208]] },
          "final":   { "d0":1,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                       "a0":4096,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                       "usp":16384,"ssp":32768,"sr":8192,"pc":1024,
                       "prefetch":[53328,0],"ram":[[1024,208]] },
          "length": 0,
          "transactions": []
        }]
        """;

    [Fact]
    public void Scaffold_sets_full_state_and_reports_not_yet_executed()
    {
        using var doc = JsonDocument.Parse(OneCase);
        var c = M68000TomHarteLoader.Parse(doc.RootElement.EnumerateArray().First());
        // M4.4b: the scaffold sets the full state without throwing and returns the not-yet-executed sentinel
        // (no op bodies -> no Step -> no assertion; M4.5 replaces the sentinel with the real Step + diff).
        string result = M68000TomHarteRunner.RunCase(c);
        Assert.Equal(M68000TomHarteRunner.NotYetExecuted, result);
    }

    [Fact]
    public void Scaffold_sets_a_supervisor_state_whose_a7_aliases_ssp()
    {
        // A supervisor case (S-bit set in sr) exercises the A7-banking set path: SSP is the active A7.
        const string supervisorCase = """
            [{
              "name": "supervisor scaffold",
              "initial": { "d0":0,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                           "a0":0,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                           "usp":248155182,"ssp":2048,"sr":9993,"pc":3072,
                           "prefetch":[53555,15214],"ram":[[3072,0],[3073,0]] },
              "final":   { "d0":0,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                           "a0":0,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                           "usp":248155182,"ssp":2048,"sr":9993,"pc":3072,
                           "prefetch":[53555,15214],"ram":[[3072,0],[3073,0]] },
              "length": 0,
              "transactions": []
            }]
            """;
        using var doc = JsonDocument.Parse(supervisorCase);
        var c = M68000TomHarteLoader.Parse(doc.RootElement.EnumerateArray().First());
        // The state-set path must not throw on the full 32-bit usp/ssp + the supervisor sr.
        Assert.Equal(M68000TomHarteRunner.NotYetExecuted, M68000TomHarteRunner.RunCase(c));
    }
}
