using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class Z80EdSemanticsTests
{
    private static string Z80DatasetPath => DataPath.Get("z80-opcodes.json");

    [Theory]
    [InlineData(0x40, "[EdIn(\"B\")]")]
    [InlineData(0x70, "[EdIn(\"none\")]")]
    [InlineData(0x41, "[EdOut(\"B\")]")]
    [InlineData(0x71, "[EdOut(\"zero\")]")]
    [InlineData(0x42, "[EdAdcSbc16(\"SBC\",\"BC\")]")]
    [InlineData(0x4A, "[EdAdcSbc16(\"ADC\",\"BC\")]")]
    [InlineData(0x43, "[EdLdNnRp(\"STORE\",\"BC\")]")]
    [InlineData(0x4B, "[EdLdNnRp(\"LOAD\",\"BC\")]")]
    [InlineData(0x44, "[EdNeg()]")]
    [InlineData(0x4C, "[EdNeg()]")]
    [InlineData(0x45, "[EdRetn(false)]")]
    [InlineData(0x4D, "[EdRetn(true)]")]
    [InlineData(0x46, "[EdIm(0)]")]
    [InlineData(0x56, "[EdIm(1)]")]
    [InlineData(0x5E, "[EdIm(2)]")]
    [InlineData(0x47, "[EdLdIaRa(\"I_A\")]")]
    [InlineData(0x57, "[EdLdIaRa(\"A_I\")]")]
    [InlineData(0x67, "[EdRrdRld(false)]")]
    [InlineData(0x6F, "[EdRrdRld(true)]")]
    [InlineData(0x77, "[EdNop()]")]
    public void OpsFor_maps_octal_fields(int opcode, string expected)
    {
        Assert.Equal(expected, Z80EdSemantics.OpsFor(opcode));
    }

    [Fact]
    public void OpsFor_returns_null_outside_the_core()
    {
        Assert.Null(Z80EdSemantics.OpsFor(0xA0));   // LDI — block op, out of scope
        Assert.Null(Z80EdSemantics.OpsFor(0xB0));   // LDIR
    }

    [Fact]
    public void Dataset_has_all_64_ED_core_rows()
    {
        var dataset = OpcodeDataset.Load(Z80DatasetPath);
        var core = dataset.Where(r => r.Prefix == "0xED"
            && System.Convert.ToInt32(r.Opcode, 16) is >= 0x40 and <= 0x7F).ToList();
        Assert.Equal(64, core.Count);
    }
}
