namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ video screen-address math (research §4, verified bijective). Hi-res uses the
/// VERIFIED two-level interleave; the swapped-stride variant is REFUTED (it collides everywhere except
/// y=0/64/191) and must never be used. Text/lo-res use the GBASCALC row bases. Pure functions — the
/// un-fakeable address gate exercises them directly, and Apple2Video composes them in RenderInto.</summary>
public static class Apple2HiResAddress
{
    /// <summary>Hi-res scanline (y in 0..191) -> the base address of that row's 40 bytes.
    /// addr(y) = 0x2000 + (y/64)*0x28 + (y%8)*0x400 + ((y/8)&7)*0x80   (page 1; +0x2000 for page 2).
    /// Landmarks: y=0->$2000, y=1->$2400, y=8->$2080, y=64->$2028, y=191->$3FD0.</summary>
    public static uint RowBase(int y, bool page2)
    {
        uint baseAddr = (uint)(0x2000
            + (y / 64) * 0x28
            + (y % 8) * 0x400
            + ((y / 8) & 7) * 0x80);
        return page2 ? baseAddr + 0x2000 : baseAddr;
    }

    /// <summary>Text/lo-res row (r in 0..23) -> the base address of that row's 40 bytes (GBASCALC).
    /// base(r) = 0x400 + (r%8)*0x80 + (r/8)*0x28   (page 1; +0x400 for page 2).
    /// Landmarks: r=0->$400, r=1->$480, r=8->$428, r=16->$450, r=23->$7D0.</summary>
    public static uint TextRowBase(int r, bool page2)
    {
        uint baseAddr = (uint)(0x400 + (r % 8) * 0x80 + (r / 8) * 0x28);
        return page2 ? baseAddr + 0x400 : baseAddr;
    }
}
