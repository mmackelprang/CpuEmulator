using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

public class BlockCacheFlushTests
{
    // A reused JIT must NOT run a stale block when the SAME PC is recompiled from DIFFERENT bytes after a flush.
    [Fact]
    public void FlushAll_makes_the_same_PC_recompile_from_new_bytes()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;

        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        var ram = new byte[0x10000];
        space.MapMemory(0x0000, ram, writable: true);

        // Case A at PC 0x0200: LDA #$11 ; (A9 11)  then a parking JMP * so Run stops.
        ram[0x0200] = 0xA9; ram[0x0201] = 0x11; ram[0x0202] = 0x4C; ram[0x0203] = 0x02; ram[0x0204] = 0x02;
        var cpu = new Mos6502Cpu(space) { PC = 0x0200 };
        var jit = new JittedCpu<Mos6502Cpu>(cpu, Mos6502Cpu.JitTarget, space);
        long budget = 10; jit.Run(ref budget);
        Assert.Equal(0x11, cpu.A);   // ran case A

        // Reuse: re-zero, install Case B at the SAME PC 0x0200: LDA #$22 ; (A9 22) then park.
        space.ClearMappedBacking(ram);
        ram[0x0200] = 0xA9; ram[0x0201] = 0x22; ram[0x0202] = 0x4C; ram[0x0203] = 0x02; ram[0x0204] = 0x02;
        jit.ResetForReuse();         // <-- the new seam: flush cache so 0x0200 recompiles from the NEW bytes
        // ResetForReuse() resets the inner CPU (PC <- reset vector), so re-seed the per-case state AFTER it —
        // exactly the order the JIT runners use (RentJit -> ResetForReuse -> SetRegister per case).
        cpu.A = 0; cpu.PC = 0x0200;
        budget = 10; jit.Run(ref budget);
        Assert.Equal(0x22, cpu.A);   // ran case B, NOT the stale case-A block (which would leave A=0x11)
    }
}
