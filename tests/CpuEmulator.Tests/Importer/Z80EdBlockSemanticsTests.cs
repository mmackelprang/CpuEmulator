using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class Z80EdBlockSemanticsTests
{
    [Theory]
    [InlineData(0xA0, "LDI")]  [InlineData(0xA1, "CPI")]  [InlineData(0xA2, "INI")]  [InlineData(0xA3, "OUTI")]
    [InlineData(0xA8, "LDD")]  [InlineData(0xA9, "CPD")]  [InlineData(0xAA, "IND")]  [InlineData(0xAB, "OUTD")]
    [InlineData(0xB0, "LDIR")] [InlineData(0xB1, "CPIR")] [InlineData(0xB2, "INIR")] [InlineData(0xB3, "OTIR")]
    [InlineData(0xB8, "LDDR")] [InlineData(0xB9, "CPDR")] [InlineData(0xBA, "INDR")] [InlineData(0xBB, "OTDR")]
    public void Block_opcodes_map_to_EdBlock(int opcode, string mnemonic)
    {
        Assert.Equal($"[EdBlock(\"{mnemonic}\")]", Z80EdSemantics.OpsFor(opcode));
    }

    [Theory]
    [InlineData(0x80)]   // ED plane but not core, not block
    [InlineData(0x9F)]
    [InlineData(0xBC)]   // just past the block range
    [InlineData(0xFF)]
    public void NonBlock_nonCore_ED_returns_null(int opcode)
    {
        Assert.Null(Z80EdSemantics.OpsFor(opcode));
    }
}
