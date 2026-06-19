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
    public Endianness Endianness { get; }

    public AddressSpace(AddressSpaceKind kind, int addressBits, AddressSpaceOptions? options = null,
        Endianness endianness = Endianness.LittleEndian)
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
        // The options carry the per-space byte order (the seam a board recipe declares through
        // MachineBuilder); when options are supplied they are authoritative. The explicit `endianness`
        // parameter remains the order for callers that construct a space directly without options (the
        // 68000 execute/TomHarte harnesses pass endianness: BigEndian and no options).
        Endianness = options is not null ? options.Endianness : endianness;
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

    /// <summary>Re-zero a backing array that is ALREADY mapped, WITHOUT re-allocating the array or rebuilding the
    /// page table — the pooled-test-runner reuse seam (lever 2). Equivalent to allocating a fresh zeroed backing
    /// and re-mapping it, but with zero allocation: the mapping (the PageEntry[] page table) is unchanged, so
    /// every page still points at <paramref name="backing"/>. The caller then re-installs the case's initial RAM
    /// with Write8 exactly as it would on a fresh array. Additive: no existing caller uses it; the production
    /// hot path is untouched.</summary>
    public void ClearMappedBacking(byte[] backing)
    {
        System.ArgumentNullException.ThrowIfNull(backing);
        System.Array.Clear(backing, 0, backing.Length);
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

    /// <summary>Read a 16-bit word — one transaction, composed over <see cref="Read8"/> per
    /// <see cref="Endianness"/> (M4.2, ADR 0003 Decision 2). Big-endian: high byte at <paramref
    /// name="address"/>. Inherits page resolution, MMIO callouts, and wrap-masking from Read8. The M4.2
    /// bus does NOT fault on an odd address (the address-error exception is M4.5 —
    /// see <see cref="BusAlignment.IsMisaligned"/>).</summary>
    public ushort Read16(uint address) =>
        Endianness == Endianness.BigEndian
            ? (ushort)((Read8(address) << 8) | Read8(address + 1))
            : (ushort)(Read8(address) | (Read8(address + 1) << 8));

    /// <summary>Read a 32-bit long — two word transactions, HIGH WORD FIRST under big-endian.</summary>
    public uint Read32(uint address) =>
        Endianness == Endianness.BigEndian
            ? ((uint)Read16(address) << 16) | Read16(address + 2)
            : (uint)Read16(address) | ((uint)Read16(address + 2) << 16);

    /// <summary>Write a 16-bit word — one transaction, composed over <see cref="Write8"/> per
    /// <see cref="Endianness"/>. Big-endian: high byte written first at <paramref name="address"/>.</summary>
    public void Write16(uint address, ushort value)
    {
        if (Endianness == Endianness.BigEndian)
        {
            Write8(address, (byte)(value >> 8));
            Write8(address + 1, (byte)value);
        }
        else
        {
            Write8(address, (byte)value);
            Write8(address + 1, (byte)(value >> 8));
        }
    }

    /// <summary>Write a 32-bit long — two word transactions, HIGH WORD FIRST under big-endian.</summary>
    public void Write32(uint address, uint value)
    {
        if (Endianness == Endianness.BigEndian)
        {
            Write16(address, (ushort)(value >> 16));
            Write16(address + 2, (ushort)value);
        }
        else
        {
            Write16(address, (ushort)value);
            Write16(address + 2, (ushort)(value >> 16));
        }
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
