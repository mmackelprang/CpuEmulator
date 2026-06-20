using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// The SP0 demo disk controller: a minimal memory-mapped register file over an
/// <see cref="IBlockDevice"/> (a <see cref="DiskImage"/> in the demo). It is the simplest
/// controller that lets the demo ROM "read sector N, then read a byte out". Real controllers
/// (SIO/810, µPD765) replace it in SP1+.
/// <list type="bullet">
///   <item>offset 0 LBA — read/write: the target sector (one byte; resets the DATA pointer on write).</item>
///   <item>offset 1 CMD/STATUS — write 0x01 = read sector LBA into the buffer; write 0x02 = write the
///         buffer to sector LBA (no-op if the block device is read-only). Read = STATUS: bit0 ready
///         (always 1 — ops complete synchronously), bit1 = error (last op was out of range / read-only).
///         A STATUS read also resets the DATA pointer to 0.</item>
///   <item>offset 2 DATA — read/write: an auto-incrementing window into the 256-byte buffer.</item>
/// </list>
/// Polled (no IRQ); <see cref="Realize"/> is a no-op. AccessWidth is ignored (8-bit device).
/// </summary>
public sealed class DemoDisk : IPeripheral
{
    private readonly IBlockDevice _block;
    private readonly byte[] _buffer;
    private long _lba;
    private int _dataPtr;
    private bool _error;

    public string Name => "disk";

    public DemoDisk(IBlockDevice block)
    {
        ArgumentNullException.ThrowIfNull(block);
        _block = block;
        _buffer = new byte[block.SectorSize];
    }

    public void Realize(IMachineContext context) { /* polled device — nothing to wire */ }

    public uint Read(uint offset, AccessWidth width)
    {
        switch (offset % 3)
        {
            case 0:
                return (uint)(_lba & 0xFF);                    // LBA
            case 1:
                _dataPtr = 0;                                  // STATUS read rewinds DATA
                return 0x01u | (_error ? 0x02u : 0x00u);       // bit0 ready, bit1 error
            default:
                byte b = _buffer[_dataPtr];                    // DATA: read + advance
                _dataPtr = (_dataPtr + 1) % _buffer.Length;
                return b;
        }
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        switch (offset % 3)
        {
            case 0:
                _lba = value & 0xFF;                           // LBA (resets DATA window)
                _dataPtr = 0;
                break;
            case 1:
                Execute(unchecked((byte)value));               // CMD
                break;
            default:
                _buffer[_dataPtr] = unchecked((byte)value);    // DATA: store + advance
                _dataPtr = (_dataPtr + 1) % _buffer.Length;
                break;
        }
    }

    private void Execute(byte command)
    {
        _error = false;
        _dataPtr = 0;
        try
        {
            switch (command)
            {
                case 0x01:
                    _block.ReadSector(_lba, _buffer);
                    break;
                case 0x02:
                    _block.WriteSector(_lba, _buffer);
                    break;
                // other commands are no-ops
            }
        }
        catch (ArgumentOutOfRangeException)
        {
            _error = true;                                     // surface as STATUS, never throw to the guest
        }
        catch (InvalidOperationException)
        {
            _error = true;                                     // read-only write attempt
        }
    }
}
