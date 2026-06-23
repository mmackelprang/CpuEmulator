using CpuEmulator.Core;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>ADR 0019 FF-2: the 8086 FAR control-flow emit arm (far CALL/JMP direct 9A/EA, far RETF CB/CA, far
/// indirect FF /3 /5) is LIVE (not all-fallback) and DISPATCHES, and the emitted CS:IP + the far stack frame +
/// SP are BYTE-IDENTICAL to the interpreter oracle (M8086Cpu.Control.cs + Stack.cs). The far direct forms
/// (9A/EA) carry a compile-time-constant (newCS,newIP) and CHAIN to the projected linear key ((newCS&lt;&lt;4)+newIP);
/// the far RETF (CB/CA) + far indirect (FF /3 /5) are dynamic and EXIT. INT/INTO/IRET/BOUND stay fallback.
/// The headline byte-identity gate is the full 8088 TomHarte JIT sweep (M8088JitTom over 9A/EA/CA/CB/FF.3/FF.5);
/// this file pins the non-vacuity (M8086FarFlowEmitSelections &gt; 0) + the densest correctness pockets (the far
/// frame push/pop order, the CS write, the imm16 SP adjust), comparing a JIT run against a fresh interpreter
/// stepped from the SAME initial state. The interpreter is the oracle.</summary>
public class M8086FarFlowEmitTests
{
    // ─────────────────────────── shared single-block harness ───────────────────────────

    /// <summary>Drive ONE far-flow block through JittedCpu&lt;M8086Cpu&gt; (budget 1 — the far op ends the block) over a
    /// fresh 1 MB space seeded with the code at (cs&lt;&lt;4)+ip; return the resulting CS/IP and the far-emit-selection
    /// count via <paramref name="farEmitCount"/>. <paramref name="seeds"/> sets extra registers (SS/SP/DS/…).</summary>
    private static (ushort Cs, ushort Ip) RunJitOne(
        ushort cs, ushort ip, byte[] code, out int farEmitCount, params (string Name, ushort Value)[] seeds)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) bus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
        var inner = new M8086Cpu(bus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip);
        foreach (var (n, v) in seeds) inner.SetRegister(n, v);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, bus);
        long budget = 1; jit.Run(ref budget);
        farEmitCount = jit.M8086FarFlowEmitSelections;
        return ((ushort)inner.GetRegister("CS"), (ushort)inner.GetRegister("IP"));
    }

    /// <summary>Step ONE far-flow instruction through a fresh interpreter (the oracle) from the SAME seeds; return
    /// the resulting CS/IP.</summary>
    private static (ushort Cs, ushort Ip) RunInterpOne(
        ushort cs, ushort ip, byte[] code, params (string Name, ushort Value)[] seeds)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) bus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
        var cpu = new M8086Cpu(bus);
        cpu.SetRegister("CS", cs); cpu.SetRegister("IP", ip);
        foreach (var (n, v) in seeds) cpu.SetRegister(n, v);
        cpu.Step();
        return ((ushort)cpu.GetRegister("CS"), (ushort)cpu.GetRegister("IP"));
    }

    // ─────────────────────────── far RETF (CB / CA) — pop IP then CS ───────────────────────────

    /// <summary>CB RETF: pop IP (lower addr), then pop CS; the new CS:IP and the SP (+= 4) match the interpreter.
    /// The far frame is pre-seeded at SS:SP (IP lo) and SS:SP+2 (CS lo). The CS half is the new thing FF-2 adds.</summary>
    [Fact]
    public void Retf_pops_ip_then_cs_and_exits()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ip = 0x0000, ss = 0x3000, sp = 0x0100;
        const ushort retIp = 0x1234, retCs = 0x5678;
        byte[] code = [0xCB];   // RETF

        // ── JIT run: seed the far return frame at SS:SP (IP lo) and SS:SP+2 (CS lo) ──
        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        jbus.Write8(cphys, code[0]);
        uint sphys = (uint)(((ss << 4) + sp) & 0xFFFFF);
        jbus.Write8(sphys, (byte)(retIp & 0xFF)); jbus.Write8(sphys + 1, (byte)(retIp >> 8));
        jbus.Write8(sphys + 2, (byte)(retCs & 0xFF)); jbus.Write8(sphys + 3, (byte)(retCs >> 8));
        var inner = new M8086Cpu(jbus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip);
        inner.SetRegister("SS", ss); inner.SetRegister("SP", sp);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
        long budget = 1; jit.Run(ref budget);

        // ── interpreter oracle: same seed, one Step ──
        var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        ibus.MapMemory(0, new byte[0x100000], writable: true);
        ibus.Write8(cphys, code[0]);
        ibus.Write8(sphys, (byte)(retIp & 0xFF)); ibus.Write8(sphys + 1, (byte)(retIp >> 8));
        ibus.Write8(sphys + 2, (byte)(retCs & 0xFF)); ibus.Write8(sphys + 3, (byte)(retCs >> 8));
        var interp = new M8086Cpu(ibus);
        interp.SetRegister("CS", cs); interp.SetRegister("IP", ip);
        interp.SetRegister("SS", ss); interp.SetRegister("SP", sp);
        interp.Step();

        Assert.True(jit.M8086FarFlowEmitSelections > 0, "RETF was not emitted (the far arm never dispatched).");
        Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));   // == retIp
        Assert.Equal(interp.GetRegister("CS"), inner.GetRegister("CS"));   // == retCs (the far half — the new thing)
        Assert.Equal(interp.GetRegister("SP"), inner.GetRegister("SP"));   // SP += 4
        Assert.Equal((ushort)retCs, (ushort)inner.GetRegister("CS"));
        Assert.Equal((ushort)retIp, (ushort)inner.GetRegister("IP"));
    }

    /// <summary>CA RETF imm16: pop IP, pop CS, then SP += imm16. RETF 4 → SP advances by 4 (pops) + 4 (imm).</summary>
    [Fact]
    public void Retf_imm16_adds_imm_to_sp_after_the_pops()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ip = 0x0000, ss = 0x3000, sp = 0x0100;
        const ushort retIp = 0x1234, retCs = 0x5678;
        byte[] code = [0xCA, 0x04, 0x00];   // RETF 4

        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) jbus.Write8(cphys + (uint)i, code[i]);
        uint sphys = (uint)(((ss << 4) + sp) & 0xFFFFF);
        jbus.Write8(sphys, (byte)(retIp & 0xFF)); jbus.Write8(sphys + 1, (byte)(retIp >> 8));
        jbus.Write8(sphys + 2, (byte)(retCs & 0xFF)); jbus.Write8(sphys + 3, (byte)(retCs >> 8));
        var inner = new M8086Cpu(jbus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip);
        inner.SetRegister("SS", ss); inner.SetRegister("SP", sp);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
        long budget = 1; jit.Run(ref budget);

        var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        ibus.MapMemory(0, new byte[0x100000], writable: true);
        for (int i = 0; i < code.Length; i++) ibus.Write8(cphys + (uint)i, code[i]);
        ibus.Write8(sphys, (byte)(retIp & 0xFF)); ibus.Write8(sphys + 1, (byte)(retIp >> 8));
        ibus.Write8(sphys + 2, (byte)(retCs & 0xFF)); ibus.Write8(sphys + 3, (byte)(retCs >> 8));
        var interp = new M8086Cpu(ibus);
        interp.SetRegister("CS", cs); interp.SetRegister("IP", ip);
        interp.SetRegister("SS", ss); interp.SetRegister("SP", sp);
        interp.Step();

        Assert.True(jit.M8086FarFlowEmitSelections > 0, "RETF imm16 was not emitted.");
        Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));
        Assert.Equal(interp.GetRegister("CS"), inner.GetRegister("CS"));
        Assert.Equal(interp.GetRegister("SP"), inner.GetRegister("SP"));   // SP += 4 (pops) + 4 (imm) = sp + 8
        Assert.Equal((ushort)(sp + 8), (ushort)inner.GetRegister("SP"));
    }

    // ─────────────────────────── far direct JMP (EA) / CALL (9A) — constant target ───────────────────────────

    /// <summary>EA far JMP ptr16:16: CS:IP land at the immediate's (newCS,newIP). The 4 imm bytes are
    /// IP_lo IP_hi CS_lo CS_hi (offset first, then segment).</summary>
    [Fact]
    public void Far_jmp_ea_sets_cs_and_ip_from_the_immediate()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ip = 0x0000;
        const ushort newIp = 0x0100, newCs = 0x4000;
        byte[] code = [0xEA, unchecked((byte)newIp), (byte)(newIp >> 8), unchecked((byte)newCs), (byte)(newCs >> 8)];

        var (innerCs, innerIp) = RunJitOne(cs, ip, code, out int farEmit);
        var (interpCs, interpIp) = RunInterpOne(cs, ip, code);

        Assert.True(farEmit > 0, "far JMP EA was not emitted (the far arm never dispatched).");
        Assert.Equal(interpIp, innerIp);   // == newIp
        Assert.Equal(interpCs, innerCs);   // == newCs (the far half)
        Assert.Equal(newIp, innerIp);
        Assert.Equal(newCs, innerCs);
    }

    /// <summary>9A far CALL ptr16:16: CS:IP land at the immediate; the far return frame is pushed CS-then-IP
    /// (IP at the lower address) onto SS:SP, SP -= 4. Asserts CS:IP + SP + the exact pushed frame vs the oracle.</summary>
    [Fact]
    public void Far_call_9a_pushes_far_frame_and_sets_cs_ip()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ip = 0x0000, ss = 0x3000, sp = 0x0100;
        const ushort newIp = 0x0100, newCs = 0x4000;
        byte[] code = [0x9A, unchecked((byte)newIp), (byte)(newIp >> 8), unchecked((byte)newCs), (byte)(newCs >> 8)];

        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) jbus.Write8(cphys + (uint)i, code[i]);
        var inner = new M8086Cpu(jbus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip);
        inner.SetRegister("SS", ss); inner.SetRegister("SP", sp);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
        long budget = 1; jit.Run(ref budget);

        var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        ibus.MapMemory(0, new byte[0x100000], writable: true);
        for (int i = 0; i < code.Length; i++) ibus.Write8(cphys + (uint)i, code[i]);
        var interp = new M8086Cpu(ibus);
        interp.SetRegister("CS", cs); interp.SetRegister("IP", ip);
        interp.SetRegister("SS", ss); interp.SetRegister("SP", sp);
        interp.Step();

        Assert.True(jit.M8086FarFlowEmitSelections > 0, "far CALL 9A was not emitted.");
        Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));   // == newIp
        Assert.Equal(interp.GetRegister("CS"), inner.GetRegister("CS"));   // == newCs
        Assert.Equal(interp.GetRegister("SP"), inner.GetRegister("SP"));   // SP -= 4
        Assert.Equal((ushort)newIp, (ushort)inner.GetRegister("IP"));
        Assert.Equal((ushort)newCs, (ushort)inner.GetRegister("CS"));
        Assert.Equal((ushort)(sp - 4), (ushort)inner.GetRegister("SP"));
        // The pushed far frame: IP at the lower word (the new SP), CS just above (SP+2). Byte-identical to the oracle.
        uint stackPhys = (uint)(((ss << 4) + interp.GetRegister("SP")) & 0xFFFFF);
        for (uint k = 0; k < 4; k++)
            Assert.Equal(ibus.Read8(stackPhys + k), jbus.Read8(stackPhys + k));
        const ushort retIp = ip + 5;   // fallThrough = pc + length(9A) = $0005
        Assert.Equal((byte)(retIp & 0xFF), jbus.Read8(stackPhys));               // IP lo (lower address)
        Assert.Equal((byte)(retIp >> 8), jbus.Read8(stackPhys + 1));             // IP hi
        Assert.Equal((byte)(cs & 0xFF), jbus.Read8(stackPhys + 2));              // CS lo (upper word)
        Assert.Equal((byte)(cs >> 8), jbus.Read8(stackPhys + 3));               // CS hi
    }
}
