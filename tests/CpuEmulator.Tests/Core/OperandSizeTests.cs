using CpuEmulator.Core.Specification;
using Xunit;

namespace CpuEmulator.Tests.Core;

/// <summary>M4.1 (ADR 0003 Decision 1, Decision D3) — the OperandSize axis exists as a Core type. The
/// 68000's .b/.w/.l size suffix is a property of the (instruction × micro-op), not of the register
/// declaration. M4.1 declares the type and stakes the name; threading it onto the size-bearing ops
/// (Move68kOp/Alu68kOp/AluAddr68kOp) is deferred to the first ALU-family PR (M4.5a), when real encodings
/// settle the operand-model shape (ADR 0003 §4 item 2).</summary>
public class OperandSizeTests
{
    [Fact]
    public void OperandSize_has_byte_word_long_members()
    {
        Assert.Equal(0, (int)OperandSize.Byte);
        Assert.Equal(1, (int)OperandSize.Word);
        Assert.Equal(2, (int)OperandSize.Long);
        Assert.Equal(3, System.Enum.GetValues<OperandSize>().Length);
    }
}
