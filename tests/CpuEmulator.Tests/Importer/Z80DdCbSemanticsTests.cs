using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class Z80DdCbSemanticsTests
{
    [Theory]
    // x=0 rotate/shift: rot[y] (IX+d), copy = reg[z] (z=6 -> "-").
    [InlineData(0x00, "[DdCb(\"RLC\",0,\"B\")]")]    // z=0 -> copy B
    [InlineData(0x06, "[DdCb(\"RLC\",0,\"-\")]")]    // z=6 -> no copy
    [InlineData(0x04, "[DdCb(\"RLC\",0,\"H\")]")]    // z=4 -> copy H (PLAIN H, not IXh — H5)
    [InlineData(0x3E, "[DdCb(\"SRL\",0,\"-\")]")]    // y=7 rot SRL, z=6
    [InlineData(0x38, "[DdCb(\"SRL\",0,\"B\")]")]    // y=7 SRL, z=0 -> copy B
    [InlineData(0x30, "[DdCb(\"SLL\",0,\"B\")]")]    // y=6 -> SLL (undoc), z=0
    // x=1 BIT: bit index = y; NO copy (always "-"); z ignored.
    [InlineData(0x46, "[DdCb(\"BIT\",0,\"-\")]")]
    [InlineData(0x40, "[DdCb(\"BIT\",0,\"-\")]")]    // z=0 -> STILL "-" (BIT never copies)
    [InlineData(0x7E, "[DdCb(\"BIT\",7,\"-\")]")]    // y=7
    // x=2 RES / x=3 SET: bit index = y; copy = reg[z] (z=6 -> "-").
    [InlineData(0x86, "[DdCb(\"RES\",0,\"-\")]")]
    [InlineData(0x80, "[DdCb(\"RES\",0,\"B\")]")]
    [InlineData(0xC6, "[DdCb(\"SET\",0,\"-\")]")]
    [InlineData(0xFF, "[DdCb(\"SET\",7,\"A\")]")]    // y=7, z=7 -> copy A
    public void OpsFor_derives_the_compound_optext(int finalOpcode, string expected)
        => Assert.Equal(expected, Z80DdCbSemantics.OpsFor(finalOpcode));

    [Fact]
    public void OpsFor_is_total_over_all_256_final_opcodes()
    {
        for (int op = 0; op <= 0xFF; op++)
            Assert.NotNull(Z80DdCbSemantics.OpsFor(op));   // no holes (H6)
    }
}
