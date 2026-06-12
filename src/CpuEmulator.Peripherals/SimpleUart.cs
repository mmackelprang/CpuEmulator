using System.Collections.Concurrent;
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// Minimal polled UART: a 4-register window, partially decoded (offset &amp; 0x03), so the
/// device mirrors through whatever page span it is mapped over — authentic partial decode
/// (sub-page decode is the peripheral's job per the AddressSpace contract).
///
///   offset 0  DATA    read: dequeue next rx byte (0x00 when empty)    write: transmit
///   offset 1  STATUS  read: bit0 rx-ready, bit1 tx-ready (always 1)   write: ignored
///   offset 2/3        reserved: read 0x00                             write: ignored
///
/// STATUS reads never dequeue (peek semantics). DATA reads are destructive by hardware
/// nature: a monitor 'm' dump over DATA dequeues rx — documented known monitor-over-MMIO
/// behavior (the Peek API is monitor-v2 backlog). Byte sinks: FeedInput(byte) enqueues rx;
/// OnTransmit fires per transmitted byte. Polled I/O only in M1 — rx-IRQ joins the timer
/// milestone (recorded), so Realize claims nothing. AccessWidth is ignored (8-bit device).
/// </summary>
public sealed class SimpleUart : IPeripheral
{
    private readonly ConcurrentQueue<byte> _rx = new();

    public string Name => "uart";

    /// <summary>Per-byte transmit sink. Null is allowed — transmitted bytes are dropped.</summary>
    public Action<byte>? OnTransmit { get; set; }

    /// <summary>Queue one byte for the guest to read from DATA.</summary>
    public void FeedInput(byte value) => _rx.Enqueue(value);

    /// <summary>Polled I/O: no IRQ claims, no scheduled events (rx-IRQ is recorded for the
    /// timer milestone — it will claim context.IrqLine here when it lands).</summary>
    public void Realize(IMachineContext context) { }

    public uint Read(uint offset, AccessWidth width) => (offset & 0x03) switch
    {
        0 => _rx.TryDequeue(out byte value) ? value : 0x00u,  // DATA: destructive read
        1 => (uint)((_rx.IsEmpty ? 0x00 : 0x01) | 0x02),      // STATUS: peek only
        _ => 0x00u,                                            // reserved
    };

    public void Write(uint offset, AccessWidth width, uint value)
    {
        if ((offset & 0x03) == 0)
            OnTransmit?.Invoke(unchecked((byte)value));        // DATA: transmit
        // STATUS/reserved writes ignored
    }

    /// <summary>Side-effect-free peek: DATA returns the queue head without dequeuing
    /// (0x00 if empty); STATUS returns the same ready bits as Read; others return 0x00.</summary>
    public bool TryPeek(uint offset, out byte value)
    {
        value = (offset & 0x03) switch
        {
            0 => _rx.TryPeek(out byte head) ? head : (byte)0x00,
            1 => (byte)((_rx.IsEmpty ? 0x00 : 0x01) | 0x02),
            _ => 0x00,
        };
        return true;
    }
}
