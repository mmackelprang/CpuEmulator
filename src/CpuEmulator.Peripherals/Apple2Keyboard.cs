using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>The Apple ][+ keyboard (ADR 0014 Decision 3): a host-facing IKeyboardSink that translates
/// portable KeyEvents into the ][+'s uppercase-only 7-bit code set (Apple2KeyMap) and latches them into
/// the shared Apple2VideoState the IOU reads at $C000 (bit 7 = strobe). It owns no bus page — the IOU
/// owns $C000; this chip is an IPeripheral only to receive Realize (it needs no tick). The ][+ latch
/// has no "release": a key-up is a no-op (the latch holds the last key until the guest reads $C010,
/// which the IOU handles). One shared Apple2VideoState, no duplication.</summary>
public sealed class Apple2Keyboard : IPeripheral, IKeyboardSink
{
    private readonly Apple2VideoState _state;

    /// <param name="state">The shared latch/mode state the IOU also holds (ADR 0014 Decision 3).</param>
    public Apple2Keyboard(Apple2VideoState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
    }

    public string Name => "apple2keyboard";

    public void Realize(IMachineContext context) { /* no bus page, no tick, no IRQ */ }

    // The chip maps no page; these are unreachable but must satisfy IPeripheral.
    public uint Read(uint offset, AccessWidth width) => 0x00;
    public void Write(uint offset, AccessWidth width, uint value) { }

    // ── IKeyboardSink: translate + latch on key-down; key-up is a no-op (the ][+ latch holds). ──
    public void PostKey(in KeyEvent e)
    {
        if (e.Action != KeyAction.Down)
            return; // the ][+ keyboard latch has no release event
        if (Apple2KeyMap.TryMap(e.Key, e.Char, out byte code))
            _state.LatchKey(code);  // sets the 7-bit code + raises the strobe (bit 7)
        // an unmapped key is silently ignored (the IKeyboardSink unknown-key contract)
    }
}
