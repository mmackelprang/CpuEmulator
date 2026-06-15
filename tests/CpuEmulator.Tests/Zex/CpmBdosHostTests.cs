using Xunit;

namespace CpuEmulator.Tests.Zex;

/// <summary>
/// Proves the CP/M BDOS host (the loader + the Step loop + the function-2 console-out intercept +
/// the warm-boot termination) WITHOUT any fetched ZEX binary — using a tiny hand-assembled .com
/// embedded here. This keeps the host fully testable when the ZEX binaries are absent (the fetch-
/// resilience requirement): the real ZEXDOC/ZEXALL runs (ZexallTests) are skip-gated on the fetch,
/// but the host MECHANISM is gated here on every CI run.
/// </summary>
public class CpmBdosHostTests
{
    [Fact]
    public void Host_prints_via_BDOS_function_2_and_terminates_on_warm_boot()
    {
        // A CP/M .com loaded at 0x0100. Prints 'H' then 'i' via BDOS fn 2, then JP 0x0000 (warm boot).
        //   0100: 1E 48     LD E,0x48 ('H')
        //   0102: 0E 02     LD C,0x02 (BDOS fn 2 = console out)
        //   0104: CD 05 00  CALL 0x0005 (BDOS)
        //   0107: 1E 69     LD E,0x69 ('i')
        //   0109: CD 05 00  CALL 0x0005
        //   010C: C3 00 00  JP 0x0000 (warm boot → terminate)
        byte[] com =
        {
            0x1E, 0x48, 0x0E, 0x02, 0xCD, 0x05, 0x00,
            0x1E, 0x69, 0xCD, 0x05, 0x00,
            0xC3, 0x00, 0x00,
        };

        var host = new CpmBdosHost(com);
        string transcript = host.Run(cycleBudget: 1_000_000);

        Assert.Equal("Hi", transcript);
        Assert.True(host.Terminated, "the program should reach warm boot (PC == 0x0000)");
    }
}
