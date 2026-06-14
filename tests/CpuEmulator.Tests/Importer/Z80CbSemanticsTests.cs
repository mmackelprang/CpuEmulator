using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class Z80CbSemanticsTests
{
    private static string Z80DatasetPath => DataPath.Get("z80-opcodes.json");

    [Theory]
    [InlineData(0x00, "[CbRotate(\"RLC\",\"B\")]")]   // x=0 y=0 z=0
    [InlineData(0x06, "[CbRotate(\"RLC\",\"(HL)\")]")] // x=0 y=0 z=6
    [InlineData(0x30, "[CbRotate(\"SLL\",\"B\")]")]    // x=0 y=6 z=0 (undocumented)
    [InlineData(0x3F, "[CbRotate(\"SRL\",\"A\")]")]    // x=0 y=7 z=7
    [InlineData(0x40, "[CbBit(\"BIT\",0,\"B\")]")]     // x=1 y=0 z=0
    [InlineData(0x7E, "[CbBit(\"BIT\",7,\"(HL)\")]")]  // x=1 y=7 z=6
    [InlineData(0x86, "[CbBit(\"RES\",0,\"(HL)\")]")]  // x=2 y=0 z=6
    [InlineData(0xFF, "[CbBit(\"SET\",7,\"A\")]")]     // x=3 y=7 z=7
    public void OpsFor_maps_octal_fields(int opcode, string expected)
    {
        Assert.Equal(expected, Z80CbSemantics.OpsFor(opcode));
    }

    [Fact]
    public void Dataset_has_all_256_CB_rows_including_SLL()
    {
        var dataset = OpcodeDataset.Load(Z80DatasetPath);
        var cb = dataset.Where(r => r.Prefix == "0xCB").ToList();
        Assert.Equal(256, cb.Count);
        // The 8 SLL rows (0x30..0x37) must now exist.
        Assert.Equal(8, cb.Count(r => System.Convert.ToInt32(r.Opcode, 16) is >= 0x30 and <= 0x37));
    }
}
