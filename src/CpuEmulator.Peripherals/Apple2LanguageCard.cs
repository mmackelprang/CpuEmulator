using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ Language Card (ADR 0014 Decision 4): a code mapper over $C080-$C08F that
/// run-time bank-switches $D000-$FFFF between the system ROM and 16 KiB of card RAM by calling the
/// shipped IAddressSpace.Remap (PR-A) — the FIRST real consumer of that bank-switch primitive. The ][+
/// layout (research §7): $D000-$DFFF (4 KiB) has two RAM banks (bank 1 / bank 2); $E000-$FFFF (8 KiB)
/// is a single shared RAM region. Write-enabling LC RAM requires TWO CONSECUTIVE READS of an odd $C08x
/// (the 74LS175 pre-write count flip-flop) — a single read does not arm it. The 74LS175 holds TWO
/// independent latches (MAME ramcard16k do_io / Sather ch.5, ADR 0018-C): the pre-write COUNT and the
/// write-enable LATCH are separate. Once two odd reads set write-enable, it PERSISTS across bank-selects,
/// RAM writes, and odd-address writes — only an EVEN-address access clears it (an odd-address write
/// clears just the count). The card is delegated
/// $C08x by the IOU (which owns the $C000 page); it captures the program bus in Realize and remaps on
/// each access. Remap fires PR-A's JIT invalidation listener, so a remapped CODE page runs the new
/// bank (the LC commonly runs DOS/ProDOS/CP/M out of the banked RAM).</summary>
public sealed class Apple2LanguageCard : IPeripheral
{
    private const uint DBank = 0xD000;   // the banked 4 KiB region
    private const uint EShared = 0xE000; // the shared 8 KiB region
    private const int DBankLen = 0x1000; // 4 KiB
    private const int ESharedLen = 0x2000; // 8 KiB

    // The 16 KiB of card RAM as three index-0-based arrays (Remap backing must start at index 0).
    private readonly byte[] _bankD1 = new byte[DBankLen];
    private readonly byte[] _bankD2 = new byte[DBankLen];
    private readonly byte[] _sharedE = new byte[ESharedLen];

    // The system-ROM slices the card remaps $D000/$E000 back to (index-0-based copies of the image).
    private readonly byte[] _romD;   // $D000-$DFFF slice (4 KiB)
    private readonly byte[] _romE;   // $E000-$FFFF slice (8 KiB)

    private IAddressSpace _bus = default!;  // the live program bus, captured in Realize

    // Decoded LC state (power-on = read-ROM, write-protected, bank 1).
    private bool _readRam;        // false => read ROM, true => read LC RAM
    private bool _writeEnabled;   // LC RAM writable latch (set by two consecutive odd-$C08x reads;
                                  // cleared ONLY by an even-$C08x access -- survives odd-address writes)
    private int _bank = 1;        // 1 or 2 (the $D000 bank)
    private int _armCount;        // consecutive-qualifying-read counter (0,1 -> 2 arms write)

    /// <summary>Test-only: total $C08x accesses seen (proves the IOU delegate seam).</summary>
    public long AccessCount { get; private set; }

    /// <summary>Test-only (ADR 0018-C / V80-4 discriminator): count of nonzero bytes in LC $D000 bank 2.
    /// apl2cpm3's ?ldccp `LDIR` copies the CP/M-3 CCP into bank 2; under the old single-latch model the
    /// odd-address bank-2-select write cleared write-enable and the copy was dropped (bank 2 all zeros).
    /// With the two-latch fix the copy lands (the live trace saw 3026/4096 nonzero). Internal seam,
    /// no production caller.</summary>
    internal int Bank2NonZeroCountForTest()
    {
        int count = 0;
        foreach (byte b in _bankD2)
            if (b != 0) count++;
        return count;
    }

    /// <param name="systemRom">The same 12 KiB $D000-$FFFF image the board maps as ROM; the card
    /// slices it for the read-ROM remaps.</param>
    public Apple2LanguageCard(byte[] systemRom)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        if (systemRom.Length != 0x3000)
            throw new ArgumentException("system ROM must be 12 KiB ($D000-$FFFF).", nameof(systemRom));
        _romD = systemRom[0x0000..0x1000];   // $D000-$DFFF
        _romE = systemRom[0x1000..0x3000];   // $E000-$FFFF
    }

    public string Name => "apple2lc";

    public void Realize(IMachineContext context)
    {
        _bus = context.Space(AddressSpaceKind.Program); // the live bus we Remap (the SpectrumUla precedent)
        // Power-on state = read-ROM: the board already mapped $D000-$FFFF to ROM, so no remap needed yet.
    }

    // The card maps no page of its own (the IOU owns $C000); these are unreachable.
    public uint Read(uint offset, AccessWidth width) => 0x00;
    public void Write(uint offset, AccessWidth width, uint value) { }

    /// <summary>Called by the IOU for every $C080-$C08F access (offset 0x80-0x8F, isRead = it was a
    /// Read). Decodes the bank / read-source / write-enable per the standard LC truth table (Sather),
    /// then re-points $D000/$E000 via the shipped Remap.</summary>
    public byte Access(byte offset, bool isRead)
    {
        AccessCount++;
        int o = offset & 0x0F;   // $C080-$C08F low nibble

        // Bank select: the $C088 line (bit 3) picks the $D000 bank. Polarity is pinned by the Task-2
        // gate: $C083 (o=3, bit 3 clear) selects bank 1; $C08B (o=$B, bit 3 set) selects bank 2.
        _bank = (o & 0x08) == 0 ? 1 : 2;   // bit 3 clear => bank 1, set => bank 2 (gated by Task 2)

        // Read source: read RAM when (o & 0x03) is 0 or 3; read ROM when it is 1 or 2.
        int sel = o & 0x03;
        _readRam = sel is 0 or 3;

        // Pre-write flip-flop -- the real 74LS175 has TWO independent latches (MAME ramcard16k do_io /
        // Sather ch.5), NOT one. The pre-write COUNT and the write-enable LATCH are separate:
        //   EVEN access       -> clear the COUNT and clear write-enable (the only thing that disables writes)
        //   odd-address WRITE  -> clear the COUNT ONLY (write-enable, if already set, SURVIVES)
        //   odd-address READ   -> 1st read arms the count; 2nd consecutive read sets write-enable
        // The load-bearing correction (ADR 0018-C): an odd-address WRITE (e.g. a $C08B bank-2 select,
        // STA $C08B / LD ($C08B),A) must NOT clear write-enable -- only an EVEN access does. The old
        // single-flag model wrongly write-protected LC bank 2 on the bank-select write, dropping
        // apl2cpm3's ?ldccp CCP `LDIR` copy. Two odd reads still enable; even access still disables.
        if ((o & 0x01) == 0)
        {
            _armCount = 0;
            _writeEnabled = false;            // EVEN access: clear both latches
        }
        else if (!isRead)
        {
            _armCount = 0;                    // odd-address WRITE: clear the COUNT only (write-enable survives)
        }
        else
        {
            if (_armCount < 2) _armCount++;   // odd-address READ: arm the count
            if (_armCount >= 2) _writeEnabled = true;   // 2nd consecutive odd read enables writes
        }

        ApplyMapping();
        return 0x00;   // floating bus on a soft-switch read (the side effect is the remap)
    }

    /// <summary>Re-point $D000 (the active bank) + $E000 (shared) at ROM or RAM per the decoded state,
    /// via the shipped IAddressSpace.Remap (PR-A). Read-ROM -> map the ROM slices read-only. Read-RAM ->
    /// map the RAM arrays with the decoded writability. (The ][+ "read ROM / write RAM" split collapses
    /// to a single backing per page on the shipped single-backing page table — PR-E maps the READ source;
    /// the write-enable rides the same backing's Writable flag, so read-RAM+write-enabled is the writable
    /// case DOS/ProDOS/CP/M use. Read-ROM is read-only; a separate write-through-to-RAM-while-reading-ROM
    /// page is out of scope and not needed for the target software — noted for the JIT-tier follow-on.)</summary>
    private void ApplyMapping()
    {
        if (_readRam)
        {
            _bus.Remap(DBank, _bank == 1 ? _bankD1 : _bankD2, writable: _writeEnabled);
            _bus.Remap(EShared, _sharedE, writable: _writeEnabled);
        }
        else
        {
            _bus.Remap(DBank, _romD, writable: false);     // read system ROM at $D000
            _bus.Remap(EShared, _romE, writable: false);   // read system ROM at $E000
        }
    }
}
