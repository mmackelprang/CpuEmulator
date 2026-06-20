using System.Collections.Concurrent;
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// The SP0 demo keyboard: a UART-rx-shaped 2-register device (mirrors <see cref="SimpleUart"/>'s
/// receive half). The host PUSHES normalized key events via <see cref="IKeyboardSink.PostKey"/>;
/// the guest READS them memory-mapped (<see cref="IPeripheral"/>):
/// <list type="bullet">
///   <item>offset 0 DATA — read: dequeue the next key byte (0x00 when empty); recomputes IRQ.</item>
///   <item>offset 1 STATUS — read: bit0 = key-ready; never dequeues.</item>
/// </list>
/// Only printable DOWN events with a resolved <see cref="KeyEvent.Char"/> enqueue a byte; key-ups
/// and char-less events are no-ops. <see cref="Realize"/> claims <c>context.IrqLine.Source()</c>
/// and the source is asserted while the queue is non-empty (level-IRQ, matching the UART rx path).
/// AccessWidth is ignored (8-bit device).
/// <para>
/// Threading: <see cref="PostKey"/> only enqueues (safe from any thread via the
/// <see cref="ConcurrentQueue{T}"/>); the IRQ line is recomputed exclusively on the
/// guest-execution (pump) thread during register reads (STATUS poll or DATA dequeue), so the
/// non-thread-safe <see cref="IInterruptLine"/> is never touched off-thread. A guest that polls
/// STATUS (the demo) or reads DATA always observes an up-to-date IRQ line.
/// </para>
/// </summary>
public sealed class DemoKeyboard : IPeripheral, IKeyboardSink
{
    private readonly ConcurrentQueue<byte> _keys = new();
    private IInterruptLine? _irq;

    public string Name => "keyboard";

    public void Realize(IMachineContext context) => _irq = context.IrqLine.Source();

    public void PostKey(in KeyEvent e)
    {
        if (e.Action != KeyAction.Down || e.Char is not char c)
            return;                          // ignore key-ups and char-less events
        _keys.Enqueue(unchecked((byte)c));   // enqueue only: the IRQ line is recomputed on the
                                             // pump thread during Read (STATUS poll / DATA dequeue)
    }

    public uint Read(uint offset, AccessWidth width)
    {
        switch (offset & 0x01)
        {
            case 0:
            {
                uint value = _keys.TryDequeue(out byte b) ? b : 0x00u; // DATA: destructive read
                UpdateIrqLevel();
                return value;
            }
            default:
                UpdateIrqLevel();                                      // STATUS poll re-asserts/releases
                return _keys.IsEmpty ? 0x00u : 0x01u;                  // STATUS: key-ready
        }
    }

    public bool TryPeek(uint offset, out byte value)
    {
        value = (offset & 0x01) == 0
            ? (_keys.TryPeek(out byte head) ? head : (byte)0x00)
            : (byte)(_keys.IsEmpty ? 0x00 : 0x01);
        return true;
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        // The demo keyboard is read-only to the guest; writes are ignored.
    }

    private void UpdateIrqLevel()
    {
        if (_irq is null) return;            // bare (unrealized) keyboards drive no line
        if (!_keys.IsEmpty) _irq.Assert();
        else _irq.Release();
    }
}
