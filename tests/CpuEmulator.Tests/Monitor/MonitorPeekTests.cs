using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Tests.Monitor;

/// <summary>Tests that monitor display reads (m, d, s) use TryPeek rather than live bus reads
/// where the device is honest, and fall back to live reads when TryPeek returns false.</summary>
public class MonitorPeekTests
{
    /// <summary>A peripheral with an honest TryPeek — Read should NOT be called by display ops.</summary>
    private sealed class PeekCapablePeripheral : IPeripheral
    {
        public string Name => "peekable";
        public int ReadCount { get; private set; }
        public byte PeekValue { get; set; } = 0xAA;
        public byte ReadValue { get; set; } = 0xBB;

        public void Realize(IMachineContext context) { }
        public uint Read(uint offset, AccessWidth width) { ReadCount++; return ReadValue; }
        public void Write(uint offset, AccessWidth width, uint value) { }
        public bool TryPeek(uint offset, out byte value) { value = PeekValue; return true; }
    }

    /// <summary>A peripheral with no honest TryPeek — the default returns false; display should
    /// fall back to Read.</summary>
    private sealed class NoPeekPeripheral : IPeripheral
    {
        public string Name => "noPeek";
        public int ReadCount { get; private set; }
        public byte ReadValue { get; set; } = 0xCC;

        public void Realize(IMachineContext context) { }
        public uint Read(uint offset, AccessWidth width) { ReadCount++; return ReadValue; }
        public void Write(uint offset, AccessWidth width, uint value) { }
        // Uses default TryPeek — returns false
    }

    private static (MonitorEngine engine, Mos6502Cpu cpu) BuildEngine(
        IPeripheral peripheral, uint peripheralBase = 0xD000)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x0000, new byte[0xD000], writable: true);
        space.MapPeripheral(peripheralBase, 0x0100, peripheral);
        space.MapMemory(0xD100, new byte[0x2F00], writable: true);
        var cpu = new Mos6502Cpu(space);
        cpu.SetRegister("PC", 0x0200);
        cpu.S = 0xFD;
        return (new MonitorEngine(cpu, space, cpu), cpu);
    }

    [Fact]
    public void ReadMemory_over_honest_peripheral_does_not_call_Read()
    {
        var p = new PeekCapablePeripheral { PeekValue = 0xAA, ReadValue = 0xBB };
        var (engine, _) = BuildEngine(p);

        string dump = engine.ReadMemory(0xD000, 1);

        // Display shows the peeked value, Read was never called
        Assert.Contains("AA", dump);
        Assert.Equal(0, p.ReadCount);
    }

    [Fact]
    public void Disassemble_over_mmio_does_not_perturb()
    {
        // Feed 'A','B' into a UART, disassemble over the DATA register — queue must be intact
        var uart = new SimpleUart();
        uart.FeedInput((byte)'A');
        uart.FeedInput((byte)'B');

        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x0000, new byte[0xD000], writable: true);
        space.MapPeripheral(0xD000, 0x0100, uart);
        space.MapMemory(0xD100, new byte[0x2F00], writable: true);
        var cpu = new Mos6502Cpu(space);
        cpu.SetRegister("PC", 0x0200);
        cpu.S = 0xFD;
        var engine = new MonitorEngine(cpu, space, cpu);

        engine.Disassemble(0xD000, 1); // peek over DATA — must not dequeue

        // Both bytes still in queue
        Assert.Equal((uint)'A', uart.Read(0, AccessWidth.Byte));
        Assert.Equal((uint)'B', uart.Read(0, AccessWidth.Byte));
    }

    /// <summary>A constant-value peripheral counting peeks and live reads separately.
    /// Peek == live == 0xEA (NOP), so the CPU executes the same bytes the report shows
    /// (the plan's risk note (b): peeked and live values must stay consistent).</summary>
    private sealed class ConstantPeripheral : IPeripheral
    {
        public string Name => "constant";
        public int ReadCount { get; private set; }
        public int PeekCount { get; private set; }

        public void Realize(IMachineContext context) { }
        public uint Read(uint offset, AccessWidth width) { ReadCount++; return 0xEA; }
        public void Write(uint offset, AccessWidth width, uint value) { }
        public bool TryPeek(uint offset, out byte value) { PeekCount++; value = 0xEA; return true; }
    }

    [Fact]
    public void Step_report_prefetch_does_not_perturb()
    {
        // PC sits over a constant-value peek-capable fake (peek == live == 0xEA/NOP).
        // Step()'s 3-byte disassembly prefetch must go through TryPeek; the only live
        // Reads are the CPU's own execution reads (NOP = 2 bus reads: opcode fetch +
        // dummy operand fetch). If the prefetch reverted to live reads, ReadCount
        // would be 5 (3 prefetch + 2 execution) — this pin fails on a revert.
        var p = new ConstantPeripheral();
        var space = new AddressSpace(AddressSpaceKind.Program, 16);
        space.MapMemory(0x0000, new byte[0xD000], writable: true);
        space.MapPeripheral(0xD000, 0x0100, p);
        space.MapMemory(0xD100, new byte[0x2F00], writable: true);
        var cpu = new Mos6502Cpu(space);
        cpu.SetRegister("PC", 0xD000);
        cpu.S = 0xFD;
        var engine = new MonitorEngine(cpu, space, cpu);

        MonitorStepReport report = engine.Step();

        Assert.Equal(3, p.PeekCount);  // the prefetch peeked all three bytes
        Assert.Equal(2, p.ReadCount);  // execution only — 5 would mean the prefetch read live
        Assert.Contains("NOP", report.Disassembly);
    }

    [Fact]
    public void ReadMemory_falls_back_to_live_reads_without_peek()
    {
        // When TryPeek returns false, ReadMemory falls back to live bus Read — documented.
        var p = new NoPeekPeripheral { ReadValue = 0xCC };
        var (engine, _) = BuildEngine(p);

        string dump = engine.ReadMemory(0xD000, 1);

        // Fallback: Read was called and its value appears
        Assert.Contains("CC", dump);
        Assert.True(p.ReadCount > 0);
    }
}
