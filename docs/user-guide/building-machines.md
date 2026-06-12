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

The following example builds a minimal 6502 machine: 4 KiB of RAM and a 256-byte ROM at `$FC00`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

// A tiny ROM image: LDA #$42 (A9 42) followed by JMP $FC00 (4C 00 FC)
byte[] rom = [0xA9, 0x42, 0x4C, 0x00, 0xFC];
// Pad to 256 bytes; reset vector at offset 0xFC/$FD = $FC00
var image = new byte[256];
rom.CopyTo(image, 0);
image[0xFC] = 0x00;   // RESET vector lo
image[0xFD] = 0xFC;   // RESET vector hi -> $FC00

var machine = Machine.Create("tiny6502")
    .WithAddressSpace(AddressSpaceKind.Program, addressBits: 16)
    .WithRam(AddressSpaceKind.Program, start: 0x0000, length: 0x1000)  // 4 KiB RAM
    .WithRom(AddressSpaceKind.Program, start: 0xFF00, image)            // 256-byte ROM at $FF00
    .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
    .Build();

machine.Reset();        // PC loads from $FFFC/$FFFD = $FC00
machine.Run(1000);      // run for 1000 cycles
```

This example compiles and runs. The ROM maps to `$FF00`–`$FFFF`, which includes the 6502 vector table (`$FFFC`/`$FFFD`).

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

## `Machine.Run` delegate contract

The fourth `MonitorEngine` constructor argument is a `Func<long, long>`: cycle budget in, cycles consumed out. The contract requires that **given budget 1 it executes exactly one instruction**. Both `Machine.Run` and the bare `ICpuCore.Run` qualify — this property is what makes per-instruction step reports and trap detection exact.

```csharp
// Machine.Run satisfies the contract because its inner loop steps
// until CycleCount >= start + cycles:
public long Run(long cycles) { ... while (Cpu.CycleCount < target) { Cpu.Run(ref budget); ... } }
```

The machine-level `Run` also calls `_scheduler.AdvanceTo(Cpu.CycleCount)` after each CPU slice, which fires any scheduled callbacks. This is why using `Machine.Run` as the delegate causes monitor `g`/`s` to tick peripherals.

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
