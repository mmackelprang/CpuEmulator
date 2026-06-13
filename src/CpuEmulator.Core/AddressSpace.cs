namespace CpuEmulator.Core;

/// <summary>
/// Page-table-backed address space. 256-byte pages: each page resolves to backing
/// memory (fast path) or a peripheral handler (MMIO slow path). Mapping granularity
/// is one page; sub-page decode is the peripheral's job (authentic partial decode).
/// </summary>
public sealed class AddressSpace : IAddressSpace
{
    public const int PageSize = 256;
    private const int PageShift = 8;
    private const uint PageMask = PageSize - 1;

    private struct PageEntry
    {
        public byte[]? Backing;      // non-null => memory fast path
        public int BackingOffset;    // index into Backing of this page's first byte
        public bool Writable;
        public IPeripheral? Handler; // non-null => MMIO slow path
        public uint HandlerBase;     // absolute address of the handler's mapping start
    }

    private readonly PageEntry[] _pages;
    private readonly AddressSpaceOptions _options;

    public AddressSpaceKind Kind { get; }
    public int AddressBits { get; }
    public uint AddressMask { get; }

    public AddressSpace(AddressSpaceKind kind, int addressBits, AddressSpaceOptions? options = null)
    {
        // 32-bit spaces need a two-level table (a flat one would be ~16M entries);
        // out of scope until a 32-bit CPU exists.
        if (addressBits is < 8 or > 24)
            throw new MachineConfigurationException(
                $"addressBits must be between 8 and 24, got {addressBits}.");

        Kind = kind;
        AddressBits = addressBits;
        AddressMask = (1u << addressBits) - 1;
        _options = options ?? new AddressSpaceOptions();
        _pages = new PageEntry[(1 << addressBits) >> PageShift];
    }

    public void MapMemory(uint start, byte[] backing, bool writable)
    {
        ArgumentNullException.ThrowIfNull(backing);
        ValidateRange(start, (uint)backing.Length);
        int firstPage = (int)(start >> PageShift);
        int pageCount = backing.Length >> PageShift;
        EnsureRangeUnmapped(firstPage, pageCount, start);
        for (int i = 0; i < pageCount; i++)
        {
            ref PageEntry page = ref _pages[firstPage + i];
            page.Backing = backing;
            page.BackingOffset = i << PageShift;
            page.Writable = writable;
        }
    }

    public void MapPeripheral(uint start, uint length, IPeripheral peripheral)
    {
        ArgumentNullException.ThrowIfNull(peripheral);
        ValidateRange(start, length);
        int firstPage = (int)(start >> PageShift);
        int pageCount = (int)(length >> PageShift);
        EnsureRangeUnmapped(firstPage, pageCount, start);
        for (int i = 0; i < pageCount; i++)
        {
            ref PageEntry page = ref _pages[firstPage + i];
            page.Handler = peripheral;
            page.HandlerBase = start;
        }
    }

    public byte Read8(uint address)
    {
        address &= AddressMask;
        ref PageEntry page = ref _pages[address >> PageShift];
        if (page.Backing is not null)
            return page.Backing[page.BackingOffset + (int)(address & PageMask)];
        if (page.Handler is not null)
            return (byte)page.Handler.Read(address - page.HandlerBase, AccessWidth.Byte);
        if (_options.Strict)
            throw new StrictBusViolationException($"Read from unmapped address 0x{address:X4}.");
        return _options.OpenBusValue;
    }

    public bool TryPeek8(uint address, out byte value)
    {
        address &= AddressMask;
        ref PageEntry page = ref _pages[address >> PageShift];
        if (page.Backing is not null)
        {
            value = page.Backing[page.BackingOffset + (int)(address & PageMask)];
            return true;
        }
        if (page.Handler is not null)
            return page.Handler.TryPeek(address - page.HandlerBase, out value);
        value = _options.OpenBusValue; // peeking unmapped space is side-effect-free by
        return true;                   // definition — no strict-mode throw (debugger view)
    }

    public void Write8(uint address, byte value)
    {
        address &= AddressMask;
        ref PageEntry page = ref _pages[address >> PageShift];
        if (page.Backing is not null)
        {
            if (page.Writable)
                page.Backing[page.BackingOffset + (int)(address & PageMask)] = value;
            else if (_options.Strict)
                throw new StrictBusViolationException($"Write to read-only memory at 0x{address:X4}.");
            return; // ROM write silently ignored (authentic bus behavior)
        }
        if (page.Handler is not null)
        {
            page.Handler.Write(address - page.HandlerBase, AccessWidth.Byte, value);
            return;
        }
        if (_options.Strict)
            throw new StrictBusViolationException($"Write to unmapped address 0x{address:X4}.");
        // unmapped write silently ignored
    }

    /// <summary>JIT fastmem view (internal — CpuEmulator.Jit only). For a page-aligned address,
    /// reports the backing array + the index of this page's first byte + writability when the page
    /// is RAM/ROM (the fastmem fast path). Returns false for peripheral and unmapped pages (the
    /// MMIO/open-bus slow path — the JIT emits a bus callout for those). A peek-equivalent view:
    /// it never reads or writes, never throws, has no strict-mode behavior — it describes the page.</summary>
    internal bool TryGetDirectAccess(uint pageStart, out byte[] backing, out int pageOffset, out bool writable)
    {
        ref readonly PageEntry page = ref _pages[(pageStart & AddressMask) >> PageShift];
        if (page.Backing is not null)
        {
            backing = page.Backing;
            pageOffset = page.BackingOffset;   // index of this page's first byte within Backing
            writable = page.Writable;
            return true;
        }
        backing = System.Array.Empty<byte>();
        pageOffset = 0;
        writable = false;
        return false;                          // peripheral or unmapped -> MMIO slow path
    }

    /// <summary>Page count (= 1 &lt;&lt; (AddressBits - 8)); the JIT sizes its fastmem + dirty arrays.</summary>
    internal int PageCount => _pages.Length;

    private void ValidateRange(uint start, uint length)
    {
        if (length == 0 || (length & PageMask) != 0)
            throw new MachineConfigurationException(
                $"Mapping length 0x{length:X} is not a positive multiple of the {PageSize}-byte page size.");
        if ((start & PageMask) != 0)
            throw new MachineConfigurationException(
                $"Mapping start 0x{start:X} is not {PageSize}-byte page aligned.");
        if (start > AddressMask || length - 1 > AddressMask - start)
            throw new MachineConfigurationException(
                $"Mapping 0x{start:X}..0x{(ulong)start + length - 1:X} exceeds the {AddressBits}-bit address space.");
    }

    private void EnsureRangeUnmapped(int firstPage, int pageCount, uint start)
    {
        for (int i = 0; i < pageCount; i++)
        {
            ref readonly PageEntry page = ref _pages[firstPage + i];
            if (page.Backing is not null || page.Handler is not null)
                throw new MachineConfigurationException(
                    $"Page at 0x{start + (uint)(i << PageShift):X} is already mapped; overlapping mappings are not allowed.");
        }
    }
}
