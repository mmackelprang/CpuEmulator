using Xunit;

namespace CpuEmulator.Tests.Generators;

public class M68kExtensionWordValueTests
{
    // Reuse the M4.3a single-op ADD field grammar (mask 0xF100/match 0xD000, size 7-6, EA 5-0).
    private const string Source = M68kEaTestSpecs.AddGrammarCpu;   // shared synthetic spec (see note)

    [Fact]
    public void Abs_w_surfaces_its_one_extension_word()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        // ADD.w <abs.w> : operword 0xD0 78 (size .w=01, EA mode 7 reg 0 = abs.w), ext word 0x1234.
        var buf = new byte[] { 0xD0, 0x78, 0x12, 0x34 };
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        dynamic r = t.GetMethod("Decode")!.Invoke(null, new object[] { stream })!;
        Assert.Equal(4, (int)r.Length);                       // operword + 1 ext word
        Assert.Equal(1, (int)r.ExtensionWords.Count);
        Assert.Equal(0x1234u, (uint)r.ExtensionWords[0]);     // the abs.w extension word, big-endian
    }

    [Fact]
    public void Abs_l_surfaces_two_extension_words()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.FgwCpu");
        // ADD.w <abs.l> : operword 0xD0 79 (EA mode 7 reg 1 = abs.l), ext words 0x1234 0x5678.
        var buf = new byte[] { 0xD0, 0x79, 0x12, 0x34, 0x56, 0x78 };
        var stream = new CpuEmulator.Core.Jit.BufferFetchStream(buf, unitBytes: 2, bigEndian: true);
        dynamic r = t.GetMethod("Decode")!.Invoke(null, new object[] { stream })!;
        Assert.Equal(6, (int)r.Length);
        Assert.Equal(2, (int)r.ExtensionWords.Count);
        Assert.Equal(0x1234u, (uint)r.ExtensionWords[0]);
        Assert.Equal(0x5678u, (uint)r.ExtensionWords[1]);
    }
}
