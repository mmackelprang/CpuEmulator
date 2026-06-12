using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

public class Mos6502ProgramTests
{
    /// <summary>CPU with 64 KiB RAM, program bytes at 0x0200, PC set there.</summary>
    private static (Mos6502Cpu Cpu, AddressSpace Space) NewCpu(params byte[] program)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        for (uint i = 0; i < program.Length; i++)
            space.Write8(0x0200 + i, program[i]);
        var cpu = new Mos6502Cpu(space);
        cpu.SetRegister("PC", 0x0200);
        return (cpu, space);
    }

    [Fact]
    public void Countdown_loop_executes_with_exact_cycle_total()
    {
        // 0200: A2 FB     LDX #$FB      2
        // 0202: E8        INX           2     ┐
        // 0203: D0 FD     BNE $0202     3/2   ┘ ×5 iterations (4 taken, last not-taken)
        //
        // X starts at 0xFB; 5 increments wrap it to 0x00 (0xFB→FC→FD→FE→FF→00, setting Z).
        // BNE taken after INX 1–4 (target 0x0202 from 0x0205, page 0x02xx) → 3 cy each.
        // Final BNE not-taken (Z set after X wraps to 0) → 2 cy.
        // cycles: LDX(2) + 5×INX(2) + 4×BNE-taken(3) + 1×BNE-not-taken(2) = 2+10+12+2 = 26
        var (cpu, _) = NewCpu(0xA2, 0xFB, 0xE8, 0xD0, 0xFD);

        // Bounded loop: a branch-polarity regression would oscillate PC forever and hang
        // the test runner (xUnit has no default per-test timeout); the guard converts a
        // hang into a clear assertion failure. Template for chunk-4's trap-loop boots.
        int guard = 0;
        while (cpu.GetRegister("PC") != 0x0205 && ++guard < 1000)
            cpu.Step();

        Assert.Equal(0x0205ul, cpu.GetRegister("PC"));
        Assert.Equal(0ul, cpu.GetRegister("X"));
        Assert.Equal(26, cpu.CycleCount);
    }

    [Fact]
    public void Store_load_roundtrip_through_memory()
    {
        // LDA #$5A; STA $1234; LDA #$00; LDA $1234 → A=0x5A again
        var (cpu, _) = NewCpu(0xA9, 0x5A, 0x8D, 0x34, 0x12, 0xA9, 0x00, 0xAD, 0x34, 0x12);
        for (int i = 0; i < 4; i++)
            cpu.Step();

        Assert.Equal(0x5Aul, cpu.GetRegister("A"));
    }

    [Fact]
    public void Jsr_rts_roundtrip_returns_past_the_call()
    {
        // Layout @0x0200: JSR $8000 (3 bytes); NOP (1 byte) at 0x0203
        //   @0x8000: RTS
        // After JSR: PC=0x8000, S decremented by 2 (pushed 0x02,0x02 return-1)
        // After RTS: PC=0x0203, S restored
        // Total cycles: JSR(6) + RTS(6) = 12
        var (cpu, space) = NewCpu(
            0x20, 0x00, 0x80,  // JSR $8000
            0xEA);             // NOP at 0x0203
        space.Write8(0x8000, 0x60); // RTS
        cpu.SetRegister("S", 0xFD); // match hardware post-reset S

        ulong sAfterJsr = 0;
        cpu.Step(); // JSR
        sAfterJsr = cpu.GetRegister("S");
        cpu.Step(); // RTS

        Assert.Equal(0x0203ul, cpu.GetRegister("PC")); // landed past the 3-byte JSR
        Assert.Equal(0x00FDul, cpu.GetRegister("S"));  // S restored to initial (FD after startup)
        Assert.Equal(12, cpu.CycleCount);
    }

    [Fact]
    public void Countdown_loop_with_DEX_CPX_BNE()
    {
        // LDX #3 (0xA2 0x03) = 2 cy
        // loop: DEX (0xCA) = 2 cy; CPX #0 (0xE0 0x00) = 2 cy; BNE loop (0xD0 0xFB) = 3/2 cy
        // 3 iterations of DEX: X=3→2→1→0
        // Iteration 1 (X=3→2): DEX+CPX+BNE-taken (target 0x0202 from 0x0207) → 2+2+3=7
        // Iteration 2 (X=2→1): DEX+CPX+BNE-taken → 7
        // Iteration 3 (X=1→0): DEX+CPX+BNE-not-taken (Z=1) → 2+2+2=6
        // Total: LDX(2) + 3×DEX(2) + 3×CPX(2) + 2×BNE-taken(3) + 1×BNE-not-taken(2)
        //      = 2 + 6 + 6 + 6 + 2 = 22
        var (cpu, _) = NewCpu(
            0xA2, 0x03,        // 0200: LDX #3
            0xCA,              // 0202: DEX
            0xE0, 0x00,        // 0203: CPX #0
            0xD0, 0xFB);       // 0205: BNE $0202 (offset 0xFB = -5 from 0x0207 → 0x0202)

        int guard = 0;
        while (cpu.GetRegister("PC") != 0x0207 && ++guard < 500)
            cpu.Step();

        Assert.Equal(0x0207ul, cpu.GetRegister("PC"));
        Assert.Equal(0ul, cpu.GetRegister("X"));
        Assert.Equal(22, cpu.CycleCount);
    }

    [Fact]
    public void Program_runs_inside_a_Machine_via_reset_vector()
    {
        var machine = Machine.Create("breadboard")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();
        var space = machine.Space(AddressSpaceKind.Program);
        // LDA #$42; STA $1000; JMP $0205 (self-loop: JMP back at 0x0205)
        byte[] program = [0xA9, 0x42, 0x8D, 0x00, 0x10, 0x4C, 0x05, 0x02];
        for (uint i = 0; i < program.Length; i++)
            space.Write8((uint)(0x0200 + i), program[i]);
        space.Write8(0xFFFC, 0x00);
        space.Write8(0xFFFD, 0x02);

        machine.Reset();
        machine.Run(100);

        Assert.Equal(0x42, space.Read8(0x1000));
        Assert.Equal(0x0205ul, machine.Cpu.GetRegister("PC")); // parked on JMP-self
    }
}
