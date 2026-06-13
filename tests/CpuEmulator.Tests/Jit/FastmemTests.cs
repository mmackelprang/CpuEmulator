using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Peripherals;
using CpuEmulator.Jit;

namespace CpuEmulator.Tests.Jit;

/// <summary>Task 4: the fastmem split (RAM/ROM direct, MMIO bus callout), MMIO ordering
/// (the device sees a write-cycle-inclusive CycleCount — Ground truth F(a)), the block-entry
/// interrupt check (dispatcher-side for M2-i), the budget==delta contract, and the three
/// device-honest-time interplay pins (Ground truth F a/b/c). The interpreter is the oracle.</summary>
public class FastmemTests
{
    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────
    private static void Poke(AddressSpace space, ushort at, params byte[] bytes)
    {
        for (int i = 0; i < bytes.Length; i++)
            space.Write8((uint)(at + i), bytes[i]);
    }

    /// <summary>A machine with RAM $0000-$CFFF, a UART at $D000, a timer at $D100, RAM $D200-$FFFF,
    /// wired with a JIT-wrapped CPU (the JITted CPU IS the machine's Cpu). Returns the parts the
    /// tests drive. The CPU factory downcasts ctx.Space (runtime type is the concrete AddressSpace,
    /// which the JIT fastmem binding requires).</summary>
    private static (Machine machine, SimpleUart uart, IntervalTimer timer, Mos6502Cpu inner) JitBoard()
    {
        var uart = new SimpleUart();
        var timer = new IntervalTimer();
        Mos6502Cpu inner = null!;
        var machine = Machine.Create("jit-board")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)
            .WithPeripheral(AddressSpaceKind.Program, 0xD000, 0x0100, uart)
            .WithPeripheral(AddressSpaceKind.Program, 0xD100, 0x0100, timer)
            .WithRam(AddressSpaceKind.Program, 0xD200, 0x10000 - 0xD200)
            .WithCpu(ctx =>
            {
                var space = (AddressSpace)ctx.Space(AddressSpaceKind.Program);
                inner = new Mos6502Cpu(space);
                return new JittedCpu(inner, space);
            })
            .Build();
        return (machine, uart, timer, inner);
    }

    private static AddressSpace NewRamSpace()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return space;
    }

    // ── Fastmem RAM round-trip ──────────────────────────────────────────────────────────────────
    [Fact]
    public void Fastmem_RAM_store_and_load_round_trips()
    {
        // LDA #$42 / STA $00 / LDA $00 / JMP-self. After the second LDA, A == 0x42 from RAM.
        var space = NewRamSpace();
        Poke(space, 0x0200, 0xA9, 0x42, 0x85, 0x00, 0xA5, 0x00, 0x4C, 0x06, 0x02);
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        var jit = new JittedCpu(inner, space);

        long budget = 2 + 3 + 3; // LDA# (2) + STA zp (3) + LDA zp (3)
        jit.Run(ref budget);

        Assert.Equal(0x42, inner.A);
        Assert.Equal(0x42, space.Read8(0x00));
    }

    // ── MMIO store routes to the bus (the device sees the write) ────────────────────────────────
    [Fact]
    public void MMIO_store_routes_to_the_bus()
    {
        var (machine, uart, _, inner) = JitBoard();
        var space = (AddressSpace)machine.Space(AddressSpaceKind.Program);
        byte? sent = null;
        uart.OnTransmit = b => sent = b;

        // LDA #$41 / STA $D000 (UART DATA) / JMP-self
        Poke(space, 0x0200, 0xA9, 0x41, 0x8D, 0x00, 0xD0, 0x4C, 0x05, 0x02);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;

        long budget = 2 + 4; // LDA# + STA abs
        machine.Cpu.Run(ref budget);

        Assert.Equal((byte)0x41, sent); // the device saw the write via the bus callout, not fastmem
    }

    // ── MMIO load routes to the bus (the device sees the read) ──────────────────────────────────
    [Fact]
    public void MMIO_load_routes_to_the_bus()
    {
        var (machine, uart, _, inner) = JitBoard();
        var space = (AddressSpace)machine.Space(AddressSpaceKind.Program);
        uart.FeedInput(0x37); // queue one rx byte

        // LDA $D000 (dequeues) / STA $10 / LDA $D000 (drained → 0) / STA $11 / JMP-self
        Poke(space, 0x0200, 0xAD, 0x00, 0xD0, 0x85, 0x10, 0xAD, 0x00, 0xD0, 0x85, 0x11, 0x4C, 0x0A, 0x02);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;

        long budget = 4 + 3 + 4 + 3;
        machine.Cpu.Run(ref budget);

        Assert.Equal(0x37, space.Read8(0x10)); // first read dequeued the fed byte
        Assert.Equal(0x00, space.Read8(0x11)); // second read drained → 0x00
    }

    // ── ROM store is dropped silently ───────────────────────────────────────────────────────────
    [Fact]
    public void ROM_store_is_dropped()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0xE000], writable: true);              // RAM $0000-$DFFF
        var rom = new byte[0x2000];
        rom[0] = 0xAA;                                                          // ROM[$E000] = $AA
        space.MapMemory(0xE000, rom, writable: false);                         // ROM $E000-$FFFF

        // LDA #$55 / STA $E000 (ROM — dropped) / JMP-self, all in RAM
        Poke(space, 0x0200, 0xA9, 0x55, 0x8D, 0x00, 0xE0, 0x4C, 0x05, 0x02);
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        var jit = new JittedCpu(inner, space);

        long budget = 2 + 4;
        var ex = Record.Exception(() => jit.Run(ref budget));

        Assert.Null(ex);                       // no throw — the interpreter drops a ROM write silently
        Assert.Equal(0xAA, space.Read8(0xE000)); // ROM unchanged
    }

    // ── Block-entry interrupt is serviced by the inner interpreter ──────────────────────────────
    [Fact]
    public void Block_entry_interrupt_is_serviced_by_the_inner_interpreter()
    {
        var space = NewRamSpace();
        // IRQ vector $FFFE/$FFFF → $0300 handler
        space.Write8(0xFFFE, 0x00); space.Write8(0xFFFF, 0x03);
        // Main code at $0200: LDA #$01 / JMP-self; handler at $0300.
        Poke(space, 0x0200, 0xA9, 0x01, 0x4C, 0x02, 0x02);
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24; // I clear (0x24 has I? 0x24 = 0010_0100 → I bit (0x04) set!)
        // 0x24 has bit2 (I) SET — clear it so the IRQ is serviceable.
        inner.P = 0x20;
        var jit = new JittedCpu(inner, space);
        jit.SetIrqLine(true); // assert IRQ before Run

        Assert.True(inner.InterruptPending);
        long before = inner.CycleCount;
        long budget = 7; // exactly the 7-cycle interrupt sequence
        jit.Run(ref budget);

        Assert.Equal(0x0300, inner.PC);                  // PC at the vector target
        Assert.Equal(7, inner.CycleCount - before);      // authentic 7-cycle service
    }

    // ── Budget exit leaves CycleCount delta == the decrement ────────────────────────────────────
    [Fact]
    public void Budget_exit_leaves_CycleCount_equal_to_the_decrement()
    {
        var space = NewRamSpace();
        // A run of NOPs (2 cycles each) so a mid-block budget lands cleanly at an instr boundary.
        var prog = new byte[20];
        Array.Fill(prog, (byte)0xEA);
        Poke(space, 0x0200, prog);
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        var jit = new JittedCpu(inner, space);

        long startBudget = 7;       // lands mid-block (3 NOPs = 6, the 4th would overshoot to 8)
        long budget = startBudget;
        long startCycles = inner.CycleCount;
        jit.Run(ref budget);

        long decrement = startBudget - budget;
        long delta = inner.CycleCount - startCycles;
        Assert.Equal(delta, decrement);            // the contract: decrement == CycleCount delta
        Assert.True(delta >= startBudget);         // overshoot is by at most one instruction
        Assert.True(delta <= startBudget + 1);     // (NOP is 2 cycles; overshoot ≤ 1 instr)
    }

    // ── Ground truth F(a): timer enable-write timestamp is exact under the JIT ──────────────────
    [Fact]
    public void Timer_enable_write_timestamp_is_exact_under_JIT()
    {
        var (machine, _, timer, inner) = JitBoard();
        var space = (AddressSpace)machine.Space(AddressSpaceKind.Program);
        timer.Write(1, AccessWidth.Byte, 16); // PERIOD = 16 before the program runs

        // $0200: NOP NOP NOP LDA #$01 STA $D100 JMP-self — STA's write transaction is bus-cycle 12.
        byte[] program = [0xEA, 0xEA, 0xEA, 0xA9, 0x01, 0x8D, 0x00, 0xD1, 0x4C, 0x08, 0x02];
        for (int i = 0; i < program.Length; i++)
            space.Write8((uint)(0x0200 + i), program[i]);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;

        machine.Run(12); // exactly through the STA's write cycle (6 NOP + 2 LDA# + 4 STA)

        var scheduler = (CycleScheduler)machine.Scheduler;
        Assert.True(scheduler.TryPeekNextEventCycle(out long fireCycle));
        Assert.Equal(12 + 16, fireCycle); // write bus-cycle 12 + PERIOD 16 = 28, same as the interpreter

        machine.Run(20); // park loop spins past 28; the chunked Run fires the event
        Assert.Equal(0x01u, timer.Read(3, AccessWidth.Byte));
    }

    // ── Ground truth F(b): a budget-1 slice executes exactly one instruction ────────────────────
    [Fact]
    public void Budget_1_slice_executes_exactly_one_instruction()
    {
        // Monitor-stepping contract: drive Run with budget 1 repeatedly; each call advances exactly
        // one instruction (the budget exit fires after instruction 1). NOP loop so each instr is
        // a clean 2-cycle boundary; the JIT compiles the block once and runs one instr per slice.
        var space = NewRamSpace();
        var prog = new byte[10];
        Array.Fill(prog, (byte)0xEA);
        Poke(space, 0x0200, prog);
        var inner = new Mos6502Cpu(space);
        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;
        var jit = new JittedCpu(inner, space);

        ushort pc0 = inner.PC;
        long budget = 1;
        jit.Run(ref budget);
        Assert.Equal(pc0 + 1, inner.PC);   // one NOP executed (advances PC by 1)

        ushort pc1 = inner.PC;
        budget = 1;
        jit.Run(ref budget);
        Assert.Equal(pc1 + 1, inner.PC);   // exactly one more — the budget exit fires after instr 1
    }

    // ── Ground truth F(c): a fragmented repeating-timer run stays correct ───────────────────────
    [Fact]
    public void Repeating_timer_fragmented_run_stays_correct()
    {
        // The Ground-truth-I timer-counting program, JIT-wrapped via Machine.Run: a repeating
        // timer (period 64) fires; a handler increments $10 and write-1-clears STATUS; the main
        // loop parks when the counter reaches 5. Correctness survives the per-period fragmentation.
        var (machine, _, _, inner) = JitBoard();
        var space = (AddressSpace)machine.Space(AddressSpaceKind.Program);
        space.Write8(0x0010, 0x00);                 // counter
        space.Write8(0xFFFE, 0x00); space.Write8(0xFFFF, 0x03); // IRQ vector → $0300

        // $0200 setup + poll loop:
        //   LDA #$40 / STA $D101 (PERIODL=64) / LDA #$00 / STA $D102 (PERIODH) /
        //   LDA #$07 / STA $D100 (enable|irq|repeat) / CLI /
        //   loop: LDA $10 / CMP #$05 / BNE loop / JMP park
        byte[] setup =
        [
            0xA9, 0x40, 0x8D, 0x01, 0xD1, // LDA #$40 / STA $D101
            0xA9, 0x00, 0x8D, 0x02, 0xD1, // LDA #$00 / STA $D102
            0xA9, 0x07, 0x8D, 0x00, 0xD1, // LDA #$07 / STA $D100
            0x58,                         // CLI
            // $0210 loop:
            0xA5, 0x10,                   // LDA $10
            0xC9, 0x05,                   // CMP #$05
            0xD0, 0xFA,                   // BNE $0210 (-6 from $0216)
            0x4C, 0x16, 0x02,             // $0216 JMP $0216 (park)
        ];
        for (int i = 0; i < setup.Length; i++)
            space.Write8((uint)(0x0200 + i), setup[i]);

        // $0300 handler: PHA / INC $10 / LDA #$01 / STA $D103 (clear STATUS) / PLA / RTI
        byte[] handler = [0x48, 0xE6, 0x10, 0xA9, 0x01, 0x8D, 0x03, 0xD1, 0x68, 0x40];
        for (int i = 0; i < handler.Length; i++)
            space.Write8((uint)(0x0300 + i), handler[i]);

        inner.PC = 0x0200; inner.S = 0xFD; inner.P = 0x24;

        // Drive small slices (like the monitor's `g until`), stopping the moment the main loop
        // parks at $0216 (counter == 5). The chunked Machine.Run fragments at every timer fire;
        // correctness must survive that. Without an `until` the repeating timer keeps incrementing
        // past 5, so we poll PC and break at the park — exactly what the monitor's until does.
        for (int slice = 0; slice < 2000 && inner.PC != 0x0216; slice++)
            machine.Run(64);

        Assert.Equal(5, space.Read8(0x10));   // counter reached 5
        Assert.Equal(0x0216, inner.PC);       // parked at the JMP-self
    }
}
