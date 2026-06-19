using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;
using Xunit;

namespace CpuEmulator.Tests.Monitor;

/// <summary>The monitor engine's a-command absolute-target resolution must respect the address
/// space width, not assume 16 bits. We can't easily exercise a 24-bit branch with the 6502
/// assembler, so we test the boundary directly: in a 16-bit space a 4-hex-digit '$hhhh'
/// absolute target still resolves (the prior behavior is preserved), and a too-wide token is
/// rejected. The 24/20-bit acceptance is exercised end-to-end by the 68000/8086 host smokes.</summary>
public class MonitorWideAddressTests
{
    private static MonitorEngine New16BitEngine(out AddressSpace space)
    {
        space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        var image = new byte[0x10000];
        space.MapMemory(0x0000, image, writable: true);
        var cpu = new Mos6502Cpu(space);
        return new MonitorEngine(cpu, space, cpu);
    }

    [Fact]
    public void Branch_with_a_4_digit_absolute_target_still_resolves_in_a_16_bit_space()
    {
        MonitorEngine engine = New16BitEngine(out _);
        // 'BNE $0205' at $0200: the table rejects absolute on a branch, so the engine resolves
        // it to a relative offset (target - (addr + len)). offset = 0x0205 - 0x0202 = 3.
        bool ok = engine.TryAssembleAt(0x0200, "BNE $0205", out byte[] bytes, out string? error);
        Assert.True(ok, error);
        Assert.Equal(2, bytes.Length);     // 6502 relative branch is 2 bytes
        Assert.Equal(0xD0, bytes[0]);      // BNE opcode
        Assert.Equal(0x03, bytes[1]);      // +3
    }

    [Fact]
    public void Branch_with_a_5_digit_target_in_a_16_bit_space_does_not_resolve()
    {
        MonitorEngine engine = New16BitEngine(out _);
        // '$01205' is wider than the 16-bit space's 4 digits — the engine must NOT treat it as
        // an absolute target (it would have, naively, before this was width-aware? No — the old
        // code keyed on length==5 i.e. '$hhhh'. The new code keys on _addressDigits, so a
        // 5-digit token in a 4-digit space is not an absolute target → assembly fails cleanly.)
        bool ok = engine.TryAssembleAt(0x0200, "BNE $01205", out _, out string? error);
        Assert.False(ok);
        Assert.NotNull(error);
    }
}
