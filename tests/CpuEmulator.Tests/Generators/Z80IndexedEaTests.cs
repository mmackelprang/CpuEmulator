using System.Text;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>
/// M3.4e-1a — the reusable (IX+d)/(IY+d) effective-address emit helper. <c>EmitZ80IndexedEa</c> is a
/// pure emitter helper (the EmitWz/Z80IndirectPair shape): it appends ONE C# statement computing the
/// SIGNED effective address into a known local (__ea), taking the index-register NAME and the
/// displacement-byte EXPRESSION as parameters so every indexed emit arm (M3.4e-2/3) calls it
/// uniformly. e-1a ships the helper and proves it synthetically; no live opcode calls it yet.
///
/// Reached directly because CpuEmulator.Generators grants InternalsVisibleTo("CpuEmulator.Tests")
/// (src/CpuEmulator.Generators/AssemblyInfo.cs) and the test project references the generator as a
/// normal library, so the internal helper on the internal CpuEmitter type is callable.
/// </summary>
public class Z80IndexedEaTests
{
    [Fact]
    public void EmitZ80IndexedEa_emits_signed_effective_address_into_a_local()
    {
        var sb = new StringBuilder();
        // (lo) is the decode walk's first operand byte (the DD/FD-core displacement); e-1b/e-2 pass
        // the right source for the compound form.
        CpuEmulator.Generators.CpuEmitter.EmitZ80IndexedEa(sb, "IX", "lo");
        string s = sb.ToString();
        Assert.Contains("ushort __ea", s);
        Assert.Contains("(sbyte)", s);    // SIGNED displacement — the (IX-128..+127) range
        Assert.Contains("IX", s);
        Assert.Contains("lo", s);
    }

    [Fact]
    public void EmitZ80IndexedEa_threads_the_index_register_and_displacement_expression()
    {
        var sb = new StringBuilder();
        CpuEmulator.Generators.CpuEmitter.EmitZ80IndexedEa(sb, "IY", "disp");
        string s = sb.ToString();
        Assert.Contains("IY", s);
        Assert.Contains("disp", s);
        Assert.Contains("(sbyte)", s);
        Assert.Contains("unchecked", s);   // 16-bit wraparound on the EA add
    }
}
