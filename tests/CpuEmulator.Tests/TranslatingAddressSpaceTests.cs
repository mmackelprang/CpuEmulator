using CpuEmulator.Core;

namespace CpuEmulator.Tests;

public class TranslatingAddressSpaceTests
{
    // A fixed test translation: add $1000 (wrapping at 16 bits). Enough to prove the wrapper
    // routes every access through ToPhysical; PR-J ships the real 6-branch SoftCard table.
    private sealed class Add0x1000 : IAddressTranslation
    {
        public uint ToPhysical(uint logical) => (logical + 0x1000) & 0xFFFF;
    }

    private static (TranslatingAddressSpace view, AddressSpace inner) Build()
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true); // 64 KiB RAM
        var view = new TranslatingAddressSpace(inner, new Add0x1000());
        return (view, inner);
    }

    [Fact]
    public void Read8_translates_logical_to_physical()
    {
        var (view, inner) = Build();
        inner.Write8(0x1000, 0x42);          // physical $1000
        Assert.Equal(0x42, view.Read8(0x0000)); // logical $0000 -> physical $1000
    }

    [Fact]
    public void Write8_translates_logical_to_physical()
    {
        var (view, inner) = Build();
        view.Write8(0x0000, 0x37);           // logical $0000 -> physical $1000
        Assert.Equal(0x37, inner.Read8(0x1000));
    }

    [Fact]
    public void TryPeek8_translates_and_is_side_effect_free()
    {
        var (view, inner) = Build();
        inner.Write8(0x1000, 0x5A);
        bool ok = view.TryPeek8(0x0000, out byte v);
        Assert.True(ok);
        Assert.Equal(0x5A, v);
    }

    [Fact]
    public void Metadata_mirrors_the_inner_space()
    {
        var (view, inner) = Build();
        Assert.Equal(inner.Kind, view.Kind);
        Assert.Equal(inner.AddressBits, view.AddressBits);
        Assert.Equal(inner.Endianness, view.Endianness);
    }

    [Fact]
    public void Mapping_on_the_wrapper_is_unsupported()
    {
        var (view, _) = Build();
        Assert.Throws<NotSupportedException>(() => view.MapMemory(0, new byte[0x100], true));
    }

    [Fact]
    public void Mapping_a_peripheral_on_the_wrapper_is_unsupported()
    {
        var (view, _) = Build();
        Assert.Throws<NotSupportedException>(() => view.MapPeripheral(0, 0x100, new NullPeripheral()));
    }

    private sealed class NullPeripheral : IPeripheral
    {
        public string Name => "null";
        public void Realize(IMachineContext context) { }
        public uint Read(uint offset, AccessWidth width) => 0;
        public void Write(uint offset, AccessWidth width, uint value) { }
    }
}
