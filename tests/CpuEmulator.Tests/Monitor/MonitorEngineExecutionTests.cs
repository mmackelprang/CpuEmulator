using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Monitor;

namespace CpuEmulator.Tests.Monitor;

/// <summary>
/// Tests for MonitorEngine execution: Step report (including interrupt-serviced case),
/// Run, RunUntil (target/trap/budget), and address-aware TryAssembleAt (Task 5).
/// </summary>
public class MonitorEngineExecutionTests
{
    /// <summary>64 KiB RAM machine with IRQ→$8000 and NMI→$9000 vectors seeded.</summary>
    private static (MonitorEngine Engine, Mos6502Cpu Cpu, IAddressSpace Space) NewMachine()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);

        // IRQ vector → $8000
        space.Write8(0xFFFE, 0x00);
        space.Write8(0xFFFF, 0x80);
        space.Write8(0x8000, 0xEA); // NOP at IRQ handler

        // NMI vector → $9000
        space.Write8(0xFFFA, 0x00);
        space.Write8(0xFFFB, 0x90);
        space.Write8(0x9000, 0xEA); // NOP at NMI handler

        var cpu = new Mos6502Cpu(space);
        cpu.SetRegister("PC", 0x0200);
        cpu.S = 0xFD;
        var engine = new MonitorEngine(cpu, space, cpu);
        return (engine, cpu, space);
    }

    // ── Step report ──────────────────────────────────────────────────────────

    [Fact]
    public void Step_reports_the_executed_instruction()
    {
        var (engine, cpu, space) = NewMachine();
        space.Write8(0x0200, 0xEA); // NOP
        cpu.SetRegister("PC", 0x0200);

        MonitorStepReport report = engine.Step();

        Assert.Equal(0x0200u, report.PcBefore);
        Assert.False(report.InterruptServiced);
        Assert.Equal("NOP", report.Disassembly);
        Assert.Equal(2, report.Cycles);
        Assert.Contains("PC=0201", report.Registers);
    }

    [Fact]
    public void Step_reports_interrupt_service_not_the_pc_instruction()
    {
        var (engine, cpu, space) = NewMachine();
        space.Write8(0x0200, 0xEA); // NOP — will NOT run; interrupt serviced instead
        cpu.SetRegister("PC", 0x0200);
        cpu.P = 0x20; // I clear — IRQ will be serviced
        cpu.SetIrqLine(true);

        Assert.True(cpu.InterruptPending);
        MonitorStepReport report = engine.Step();

        Assert.True(report.InterruptServiced);
        Assert.Equal("(interrupt serviced)", report.Disassembly);
        Assert.Equal(7, report.Cycles); // 7-cycle interrupt sequence
        // PC should be at the IRQ handler now
        Assert.Contains("PC=8000", report.Registers);
    }

    [Fact]
    public void Step_cycles_are_the_delta()
    {
        var (engine, cpu, space) = NewMachine();
        // A9 42 EA — LDA Immediate (2 cycles) then NOP (2 cycles)
        space.Write8(0x0200, 0xA9);
        space.Write8(0x0201, 0x42);
        space.Write8(0x0202, 0xEA);
        cpu.SetRegister("PC", 0x0200);

        MonitorStepReport r1 = engine.Step(); // LDA #$42
        MonitorStepReport r2 = engine.Step(); // NOP

        Assert.Equal(2, r1.Cycles);
        Assert.Equal(2, r2.Cycles);
    }

    // ── Run ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Run_consumes_the_budget()
    {
        var (engine, cpu, space) = NewMachine();
        // NOP slide — each NOP is 2 cycles, so 10-cycle budget = 5+ NOPs
        for (uint i = 0; i < 20; i++)
            space.Write8(0x0200 + i, 0xEA);
        cpu.SetRegister("PC", 0x0200);

        long cyclesRun = engine.Run(10);

        // May overshoot by one instruction (inherits ICpuCore.Run contract)
        Assert.True(cyclesRun >= 10,
            $"Expected >= 10 cycles consumed, got {cyclesRun}");
    }

    // ── RunUntil ─────────────────────────────────────────────────────────────

    [Fact]
    public void RunUntil_reaches_the_target()
    {
        var (engine, cpu, space) = NewMachine();
        // NOP at 0200, then JMP to $020B at 0201-0203; target is 0x0201
        space.Write8(0x0200, 0xEA); // NOP (2 cycles)
        space.Write8(0x0201, 0x4C); // JMP $020B
        space.Write8(0x0202, 0x0B);
        space.Write8(0x0203, 0x02);
        cpu.SetRegister("PC", 0x0200);

        RunReport report = engine.RunUntil(0x0201, 1_000_000);

        Assert.Equal(RunStopReason.TargetReached, report.Reason);
        Assert.Equal(0x0201u, report.Pc);
        Assert.Equal(2, report.CyclesRun); // NOP = 2 cycles
    }

    [Fact]
    public void RunUntil_detects_the_trap_idiom()
    {
        var (engine, cpu, space) = NewMachine();
        // JMP $0200 — parks PC at 0x0200
        space.Write8(0x0200, 0x4C);
        space.Write8(0x0201, 0x00);
        space.Write8(0x0202, 0x02);
        cpu.SetRegister("PC", 0x0200);

        RunReport report = engine.RunUntil(0xFFFF, 1_000_000);

        Assert.Equal(RunStopReason.Trapped, report.Reason);
        Assert.Equal(0x0200u, report.Pc);
    }

    [Fact]
    public void RunUntil_exhausts_the_budget()
    {
        var (engine, cpu, space) = NewMachine();
        // NOP slide — will not reach any trap or target
        for (uint i = 0; i < 100; i++)
            space.Write8(0x0200 + i, 0xEA);
        cpu.SetRegister("PC", 0x0200);

        RunReport report = engine.RunUntil(0xBEEF, 10);

        Assert.Equal(RunStopReason.BudgetExhausted, report.Reason);
    }

    [Fact]
    public void RunUntil_at_target_returns_immediately()
    {
        var (engine, cpu, _) = NewMachine();
        cpu.SetRegister("PC", 0x0200);

        RunReport report = engine.RunUntil(0x0200, 1_000_000);

        Assert.Equal(RunStopReason.TargetReached, report.Reason);
        Assert.Equal(0x0200u, report.Pc);
        Assert.Equal(0, report.CyclesRun);
    }

    // ── TryAssembleAt ────────────────────────────────────────────────────────

    [Fact]
    public void AssembleAt_writes_the_bytes()
    {
        var (engine, _, space) = NewMachine();

        bool ok = engine.TryAssembleAt(0x0200, "LDA #$42", out byte[] bytes, out string? error);

        Assert.True(ok, error);
        Assert.Equal(new byte[] { 0xA9, 0x42 }, bytes);
        Assert.Equal(0xA9, space.Read8(0x0200));
        Assert.Equal(0x42, space.Read8(0x0201));
    }

    [Fact]
    public void AssembleAt_resolves_backward_branch_targets()
    {
        // BNE $0205 at address $0206: offset = $0205 - ($0206 + 2) = -3 = 0xFD
        var (engine, _, _) = NewMachine();

        bool ok = engine.TryAssembleAt(0x0206, "BNE $0205", out byte[] bytes, out string? error);

        Assert.True(ok, error);
        Assert.Equal(new byte[] { 0xD0, 0xFD }, bytes);
    }

    [Fact]
    public void AssembleAt_resolves_forward_branch_targets()
    {
        // BEQ $0210 at address $0200: offset = $0210 - ($0200 + 2) = 0x0E
        var (engine, _, _) = NewMachine();

        bool ok = engine.TryAssembleAt(0x0200, "BEQ $0210", out byte[] bytes, out string? error);

        Assert.True(ok, error);
        Assert.Equal(new byte[] { 0xF0, 0x0E }, bytes);
    }

    [Fact]
    public void AssembleAt_rejects_out_of_range_branch_targets()
    {
        // BNE $8000 from $0200: offset = $8000 - $0202 = 0x7DFE — out of range
        var (engine, _, _) = NewMachine();

        bool ok = engine.TryAssembleAt(0x0200, "BNE $8000", out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        // The original TryAssemble error (no BNE + Absolute form) should survive
        Assert.Contains("BNE", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssembleAt_rejects_garbage_mnemonic()
    {
        var (engine, _, _) = NewMachine();

        bool ok = engine.TryAssembleAt(0x0200, "FROB #$12", out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("unknown mnemonic", error, StringComparison.OrdinalIgnoreCase);
    }
}
