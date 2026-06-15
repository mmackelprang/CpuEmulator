using CpuEmulator.Core.Jit;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68000MoveDecodeTests
{
    [Fact]
    public void Move_l_d16An_to_d16An_reads_both_displacement_words()
    {
        // MOVE.l (d16,A0),(d16,A1):
        //   dest reg = A1 (001), dest mode = d16(An) (101), src mode = d16(An) (101), src reg = A0 (000).
        //   operword bits: 00 | size .l (10 @ 13-12) | destReg 001 (11-9) | destMode 101 (8-6) | srcMode 101 (5-3) | srcReg 000 (2-0)
        //   = 0010 1011 0110 1000 = 0x2B68.  Then src disp word 0x0004, dest disp word 0x0008.
        var buf = new byte[] { 0x2B, 0x68, 0x00, 0x04, 0x00, 0x08, 0, 0 };
        var stream = new BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        DecodeResult r = M68000Cpu.Decode(stream);
        Assert.NotEqual(0xFFFFFFFFu, r.OperationKey);          // MOVE matched
        Assert.Equal(6, r.Length);                             // operword + src disp + dest disp = 3 words = 6 bytes
        Assert.Equal(2, r.ExtensionWords.Count);               // BOTH displacement words captured
        Assert.Equal((ushort)0x0004, r.ExtensionWords[0]);     // source displacement
        Assert.Equal((ushort)0x0008, r.ExtensionWords[1]);     // dest displacement (appended after source)
    }

    [Fact]
    public void Move_w_register_to_register_has_no_extension_words()
    {
        // MOVE.w D0,D1 = 0x3200 — no EA extension words either side.
        var buf = new byte[] { 0x32, 0x00, 0, 0 };
        var stream = new BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        DecodeResult r = M68000Cpu.Decode(stream);
        Assert.Equal(2, r.Length);
        Assert.Equal(0, r.ExtensionWords.Count);
    }
}
