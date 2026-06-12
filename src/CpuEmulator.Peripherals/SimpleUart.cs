using System.Collections.Concurrent;
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// 4-register UART with rx-IRQ, partially decoded (offset &amp; 0x03) — mirrors through whatever
/// page span it is mapped over (authentic partial decode per the AddressSpace contract).
/// Ground truth C register map:
///
///   offset 0  DATA    read: dequeue next rx byte (0x00 when empty); recomputes IRQ level
///                     write: transmit via OnTransmit
///   offset 1  STATUS  read: bit0 rx-ready, bit1 tx-ready (always 1); never dequeues
///                     write: ignored
///   offset 2  CTRL    read: bit0 rx-irq-enable; bits 1-7 = 0
///                     write: bit0 stored; other bits ignored; recomputes IRQ level
///   offset 3  —       reserved: 0x00                    write: ignored
///
/// IRQ contract (level, matching 6502 IRQ semantics): the UART's source is asserted while
/// rx-ready &amp;&amp; rx-irq-enable; deasserted the moment the queue drains or the enable clears.
/// Realize claims context.IrqLine.Source() (the PR #8 doc-comment promise, discharged here).
/// Unrealized UARTs never touch a line — FeedInput stays safe.
/// Honest peek: DATA peeks the queue head without dequeuing (0x00 if empty); the rest peek
/// their read values; TryPeek always returns true.
/// AccessWidth is ignored (8-bit device).
/// </summary>
public sealed class SimpleUart : IPeripheral
{
    private readonly ConcurrentQueue<byte> _rx = new();
    private IInterruptLine? _irq;
    private bool _rxIrqEnabled;

    public string Name => "uart";

    /// <summary>Per-byte transmit sink. Null is allowed — transmitted bytes are dropped.</summary>
    public Action<byte>? OnTransmit { get; set; }

    /// <summary>Queue one byte for the guest to read from DATA.</summary>
    public void FeedInput(byte value)
    {
        _rx.Enqueue(value);
        UpdateIrqLevel();
    }

    /// <summary>Claims context.IrqLine.Source() for the rx-IRQ. Unrealized UARTs never
    /// touch a line — FeedInput and CTRL writes are safe without Realize.</summary>
    public void Realize(IMachineContext context) => _irq = context.IrqLine.Source();

    public uint Read(uint offset, AccessWidth width)
    {
        switch (offset & 0x03)
        {
            case 0:
            {
                uint value = _rx.TryDequeue(out byte b) ? b : 0x00u; // DATA: destructive read
                UpdateIrqLevel();
                return value;
            }
            case 1:
                return (uint)((_rx.IsEmpty ? 0x00 : 0x01) | 0x02); // STATUS: peek only
            case 2:
                return _rxIrqEnabled ? 0x01u : 0x00u;               // CTRL: rx-irq-enable bit
            default:
                return 0x00u;                                        // reserved
        }
    }

    /// <summary>Side-effect-free peek: DATA returns the queue head without dequeuing
    /// (0x00 if empty); STATUS and CTRL return their read values; reserved returns 0x00.
    /// Always returns true.</summary>
    public bool TryPeek(uint offset, out byte value)
    {
        value = (offset & 0x03) switch
        {
            0 => _rx.TryPeek(out byte head) ? head : (byte)0x00,
            1 => (byte)((_rx.IsEmpty ? 0x00 : 0x01) | 0x02),
            2 => _rxIrqEnabled ? (byte)0x01 : (byte)0x00,
            _ => 0x00,
        };
        return true;
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        switch (offset & 0x03)
        {
            case 0:
                OnTransmit?.Invoke(unchecked((byte)value)); // DATA: transmit
                break;
            case 2:
                _rxIrqEnabled = (value & 0x01) != 0;       // CTRL: store bit0
                UpdateIrqLevel();
                break;
            // STATUS and reserved writes ignored
        }
    }

    private void UpdateIrqLevel()
    {
        if (_irq is null) return;                           // bare (unrealized) UARTs drive no line
        if (_rxIrqEnabled && !_rx.IsEmpty) _irq.Assert();
        else _irq.Release();
    }
}
