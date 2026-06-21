using CpuEmulator.Core;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Apple2;

public class SoftCardControlPortTests
{
    // A minimal ICoprocessorControl spy: records the last SetCoprocessorActive value + the call count.
    private sealed class ControlSpy : IMachineContext, ICoprocessorControl
    {
        public bool? LastActive { get; private set; }
        public int Calls { get; private set; }
        public void SetCoprocessorActive(bool active) { LastActive = active; Calls++; }

        // IMachineContext members are unused by the control port's Realize (it only needs the cast).
        public IScheduler Scheduler => throw new NotSupportedException();
        public IAddressSpace Space(AddressSpaceKind kind) => throw new NotSupportedException();
        public IInterruptLine IrqLine => throw new NotSupportedException();
        public IInterruptLine NmiLine => throw new NotSupportedException();
    }

    [Fact]
    public void A_write_flips_the_active_cpu_via_the_coprocessor_control()
    {
        var spy = new ControlSpy();
        var port = new SoftCardControlPort();
        port.Realize(spy);

        port.Write(0x00, AccessWidth.Byte, 0x00);   // first $CN00 write: hand off to the coprocessor
        Assert.Equal(true, spy.LastActive);
        Assert.Equal(1, spy.Calls);

        port.Write(0x00, AccessWidth.Byte, 0x00);   // the matching write: hand back to the primary
        Assert.Equal(false, spy.LastActive);
        Assert.Equal(2, spy.Calls);
    }

    [Fact]
    public void TryPeek_is_side_effect_free_and_does_not_switch_cpus()
    {
        var spy = new ControlSpy();
        var port = new SoftCardControlPort();
        port.Realize(spy);

        bool ok = port.TryPeek(0x00, out byte v);
        Assert.True(ok);
        Assert.Equal(0x00, v);            // open-bus, side-effect-free
        Assert.Equal(0, spy.Calls);       // a debugger peek did NOT toggle the active CPU
    }

    [Fact]
    public void On_a_non_coprocessor_context_the_port_is_inert()
    {
        var port = new SoftCardControlPort();
        // Realize with a context that is NOT an ICoprocessorControl: the cast fails, _ctl stays null.
        port.Realize(new PlainContext());
        port.Write(0x00, AccessWidth.Byte, 0x00);   // must not throw (degrades gracefully)
    }

    private sealed class PlainContext : IMachineContext
    {
        public IScheduler Scheduler => throw new NotSupportedException();
        public IAddressSpace Space(AddressSpaceKind kind) => throw new NotSupportedException();
        public IInterruptLine IrqLine => throw new NotSupportedException();
        public IInterruptLine NmiLine => throw new NotSupportedException();
    }
}
