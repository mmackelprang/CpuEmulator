# Building Machines

A machine is composed from address spaces, memory, peripherals, and a CPU using the `MachineBuilder` fluent API. Construction is two-phase — all bus mappings exist before any peripheral's `Realize` runs — ensuring peripherals can safely read the bus during initialization.

Source: `src/CpuEmulator.Core/Machine.cs`, `src/CpuEmulator.Core/MachineBuilder.cs`

---

## MachineBuilder API

All builder methods return `this` for chaining. `Build()` may only be called once.

| Method | Description |
|---|---|
| `Machine.Create(name)` | Start a builder with the given machine name. |
| `.WithAddressSpace(kind, addressBits, options?)` | Declare an address space. `kind` is `AddressSpaceKind.Program` for a Von Neumann or Harvard program space; the 6502 uses only `Program`. `addressBits` is typically 16. |
| `.WithRam(kind, start, length)` | Map `length` bytes of zero-initialized writable RAM at `start` in the given space. |
| `.WithRom(kind, start, byte[])` | Map a read-only image at `start`. Writes are silently dropped (non-strict default) or throw (strict). |
| `.WithPeripheral(kind, start, length, IPeripheral)` | Map a peripheral at `start` for `length` bytes. The peripheral handles sub-page decode internally (e.g. `offset & 0x03`). |
| `.WithCpu(Func<IMachineContext, ICpuCore>)` | Provide the CPU factory. The factory receives the fully-constructed `IMachineContext` so it can capture address spaces. |
| `.Build()` | Construct and return the `Machine`. Throws `MachineConfigurationException` for invalid configurations. |

### Construction order

1. Address spaces are created and memory is mapped.
2. The CPU factory is called — the CPU may capture the program address space.
3. Peripherals are mapped to the bus, then `Realize(context)` is called on each in registration order.

---

## Complete compilable example

The following example builds a minimal 6502 machine: 4 KiB of RAM and a 256-byte ROM at `$FF00`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

// A tiny ROM program: LDA #$42 (A9 42) followed by JMP $FF00 (4C 00 FF)
byte[] rom = [0xA9, 0x42, 0x4C, 0x00, 0xFF];
// Pad to 256 bytes; the reset vector lives at image offset $FC/$FD ($FFFC/$FFFD on the bus)
var image = new byte[256];
rom.CopyTo(image, 0);
image[0xFC] = 0x00;   // RESET vector lo
image[0xFD] = 0xFF;   // RESET vector hi -> $FF00

var machine = Machine.Create("tiny6502")
    .WithAddressSpace(AddressSpaceKind.Program, addressBits: 16)
    .WithRam(AddressSpaceKind.Program, start: 0x0000, length: 0x1000)  // 4 KiB RAM
    .WithRom(AddressSpaceKind.Program, start: 0xFF00, image)            // 256-byte ROM at $FF00
    .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
    .Build();

machine.Reset();        // PC loads from $FFFC/$FFFD = $FF00
machine.Run(1000);      // run for 1000 cycles; the program loops, leaving A = $42
```

This example compiles and runs. The ROM maps to `$FF00`–`$FFFF`, which includes the 6502 vector table (`$FFFC`/`$FFFD`), and the program loops within the ROM (`JMP $FF00`), so a bounded `Run` completes without faulting.

---

## Adding a peripheral

Implement `IPeripheral` and register it with `.WithPeripheral`:

```csharp
using CpuEmulator.Core;

public sealed class LedRegister : IPeripheral
{
    public string Name => "led";
    private byte _state;

    public void Realize(IMachineContext context) { }  // no IRQ claims in this example

    public uint Read(uint offset, AccessWidth width) => _state;

    public void Write(uint offset, AccessWidth width, uint value)
    {
        _state = unchecked((byte)value);
        Console.WriteLine($"LED state: 0x{_state:X2}");
    }
}
```

Wire it in:

```csharp
var led = new LedRegister();

var machine = Machine.Create("myboard")
    .WithAddressSpace(AddressSpaceKind.Program, addressBits: 16)
    .WithRam(AddressSpaceKind.Program, 0x0000, 0xC000)
    .WithPeripheral(AddressSpaceKind.Program, 0xC000, 0x0100, led)
    .WithRom(AddressSpaceKind.Program, 0xFF00, romImage)
    .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
    .Build();
```

The peripheral handles sub-page decode internally. If the LED register should appear at every 4th offset in the mapped page (partial decode, as `SimpleUart` does), use `offset & 0x03` in `Read`/`Write`.

---

## Wiring the monitor

Construct a `MonitorEngine` over `(ICpuCore, IAddressSpace, IMonitorSupport)`. The 6502 CPU satisfies all three:

```csharp
using CpuEmulator.Monitor;

var cpu = (Mos6502Cpu)machine.Cpu;
var space = machine.Space(AddressSpaceKind.Program);

// Without the run delegate — monitor g/s drive the bare CPU
var engine = new MonitorEngine(cpu, space, cpu);

// With Machine.Run — monitor g/s tick the scheduler (peripherals receive callbacks)
var engineWithScheduler = new MonitorEngine(cpu, space, cpu, machine.Run);
```

Pass the engine to a `MonitorRepl` for a text-driven REPL:

```csharp
using var input  = new StringReader("g 1000\nq\n");
using var output = new StringWriter();

new MonitorRepl(engineWithScheduler, input, output).Run();
Console.Write(output.ToString());
```

For interactive use on stdio with the `*` prompt:

```csharp
new MonitorRepl(engineWithScheduler, Console.In, Console.Out,
                prompt: true, inject: uart.FeedInput).Run();
```

The optional `inject` delegate is called per-byte by the `i TEXT` command. Wire it to your UART's `FeedInput` method (or equivalent) so the user can inject characters into the running guest.

---

## The scheduler: `IScheduler`

Devices see machine time through `IScheduler` (`machine.Scheduler`) — a cycle counter plus an event queue. Peripherals claim it in `Realize` (via `context.Scheduler`) and schedule callbacks in the cycle domain.

| Member | Contract |
|---|---|
| `CurrentCycle` | The device-honest "now": committed scheduler time, OR the CPU's live cycle count when the machine has bound one (a device written mid-slice sees real CPU time), OR — during event dispatch — the firing event's exact cycle (callbacks observe their own fire time). |
| `ScheduleAt(cycle, callback)` | One-shot at an absolute cycle; returns a `ScheduledEvent` handle. Scheduling in the past throws `ArgumentOutOfRangeException`. Same-cycle callbacks fire in FIFO order. |
| `ScheduleEvery(interval, callback)` | Repeating: first fire at now + interval, then every interval. `interval <= 0` throws. One handle cancels the whole chain. |
| `ScheduledEvent.Cancel()` | Idempotent, safe at any time: before the fire (the event never runs), inside its own callback (a repeating chain stops), or after a one-shot fired (no-op). Cancellation is lazy — the queue entry is discarded when it surfaces; it fires nothing and moves no time. |

```csharp
// One-shot with cancellation
ScheduledEvent evt = machine.Scheduler.ScheduleAt(machine.Scheduler.CurrentCycle + 100, OnFire);
evt.Cancel();          // never fires

// Repeat every 64 cycles; stop from inside the callback
ScheduledEvent? tick = null;
tick = machine.Scheduler.ScheduleEvery(64, () =>
{
    if (Done()) tick!.Cancel();   // the chain stops after this fire
});
```

---

## Sharing an interrupt line (wired-OR)

`IInterruptLine.Source()` returns an independent per-device handle on the line. The line is asserted while **any** input is — its own direct `Assert`/`Release` or any source handle. This is how N devices share the 6502's one IRQ pin, exactly like open-collector wired-OR hardware:

```csharp
public void Realize(IMachineContext context)
{
    _irq = context.IrqLine.Source();   // claim a private handle; never Assert the shared line directly
}

// later, in device logic:
if (interruptCondition) _irq.Assert();
else                    _irq.Release();
```

Two devices, one line: if the UART asserts and then the timer asserts, the line stays high; if the UART releases while the timer still holds its source, the line *stays high* — it drops only when every source has released. A source's `IsAsserted` reflects only its own state; the line's `IsAsserted` is the OR.

Re-presenting a high level is safe by design: the 6502's IRQ input stores level idempotently, and the NMI latch edge-detects against its own previous line state — a second source asserting an already-high line never fabricates an NMI edge.

---

## `Machine.Run` delegate contract

The fourth `MonitorEngine` constructor argument is a `Func<long, long>`: cycle budget in, cycles consumed out. The contract requires that **given budget 1 it executes exactly one instruction**. Both `Machine.Run` and the bare `ICpuCore.Run` qualify — this property is what makes per-instruction step reports and trap detection exact.

`Machine.Run` chunks CPU slices to the next live scheduled event: each inner slice runs only up to the next pending event's cycle (or the budget edge), then `_scheduler.AdvanceTo(Cpu.CycleCount)` fires due callbacks. Events therefore fire at their **exact** cycle, and their IRQs land at the very next instruction boundary. With an empty queue, `Run` is one full-budget slice — byte-identical to the pre-timer behavior.

**Mid-slice latency bound:** an event scheduled *during* a running slice (e.g. a guest store that enables the timer mid-slice) still fires at its exact cycle in scheduler time, but its IRQ reaches the CPU at the end of the slice already in flight — latency bounded by the slice length (one instruction under the monitor's budget-1 stepping; up to the full slice under a large `g` budget). This is documented behavior, not a bug; a re-entrant slice abort is recorded as future machinery nothing currently needs.

---

## `Machine` properties

| Property | Type | Description |
|---|---|---|
| `Name` | `string` | The name passed to `Machine.Create`. |
| `Cpu` | `ICpuCore` | The CPU instance created by the factory. |
| `Scheduler` | `IScheduler` | The cycle-event scheduler. |
| `IrqLine` | `IInterruptLine` | Asserts IRQ on the CPU. |
| `NmiLine` | `IInterruptLine` | Asserts NMI on the CPU (edge-triggered). |
| `Space(kind)` | `IAddressSpace` | Returns the address space for the given kind. |

`machine.Reset()` delegates to `Cpu.Reset()` which reads the reset vector and initializes registers.
