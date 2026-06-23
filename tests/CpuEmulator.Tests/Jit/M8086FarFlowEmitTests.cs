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

    /// <summary>A BlockCompiler over a fully-mapped 1 MB space with the code at (cs&lt;&lt;4)+ip — the compiler-level
    /// seam for the emit-vs-fallback probes (FallbackEmitCount / M8086FarFlowEmitSelections). Mirrors
    /// M8086FlowEmitTests.Make.</summary>
    private static BlockCompiler<M8086Cpu> MakeCompiler(ushort cs, ushort ip, params byte[] code)
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        bus.MapMemory(0, new byte[0x100000], writable: true);
        uint phys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) bus.Write8((phys + (uint)i) & 0xFFFFF, code[i]);
        var cpu = new M8086Cpu(bus);
        cpu.SetRegister("CS", cs); cpu.SetRegister("IP", ip);
        var opts = new JitOptions();
        return new BlockCompiler<M8086Cpu>(cpu, M8086Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);
    }

    /// <summary>The LINEAR block key Compile() expects after FF-1: (CS&lt;&lt;4)+IP wrapped to 20 bits — NOT the bare IP.
    /// The MakeCompiler-based compile-only probes seed CS non-zero, so the block must be keyed under the linear
    /// address, matching the dispatcher's keying (RunJitOne goes through JittedCpu.Run, which already keys here).</summary>
    private static uint LinearKey(ushort cs, ushort ip) => (uint)(((cs << 4) + ip) & 0xFFFFF);

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

    // ─────────────────────────── far indirect JMP (FF /5) / CALL (FF /3) — m16:16 from memory ───────────────────────────

    /// <summary>FF /5 far JMP indirect (m16:16): CS:IP load from the far pointer in memory — offset at EA, segment
    /// at EA+2. FF 2E 00 02 = JMP FAR [0x0200] (mod=00, reg=5, rm=110 disp16). Far pointer at DS:0x0200.</summary>
    [Fact]
    public void Far_jmp_indirect_ff5_loads_cs_ip_from_memory()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ip = 0x0000;
        const ushort newIp = 0x0100, newCs = 0x4000;
        byte[] code = [0xFF, 0x2E, 0x00, 0x02];   // FF /5 mod=00 rm=110 (disp16) → JMP FAR [0x0200]

        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) jbus.Write8(cphys + (uint)i, code[i]);
        jbus.Write8(0x0200, unchecked((byte)newIp)); jbus.Write8(0x0201, (byte)(newIp >> 8));   // DS=0 → far ptr at 0x0200
        jbus.Write8(0x0202, unchecked((byte)newCs)); jbus.Write8(0x0203, (byte)(newCs >> 8));
        var inner = new M8086Cpu(jbus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip); inner.SetRegister("DS", 0x0000);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
        long budget = 1; jit.Run(ref budget);

        var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        ibus.MapMemory(0, new byte[0x100000], writable: true);
        for (int i = 0; i < code.Length; i++) ibus.Write8(cphys + (uint)i, code[i]);
        ibus.Write8(0x0200, unchecked((byte)newIp)); ibus.Write8(0x0201, (byte)(newIp >> 8));
        ibus.Write8(0x0202, unchecked((byte)newCs)); ibus.Write8(0x0203, (byte)(newCs >> 8));
        var interp = new M8086Cpu(ibus);
        interp.SetRegister("CS", cs); interp.SetRegister("IP", ip); interp.SetRegister("DS", 0x0000);
        interp.Step();

        Assert.True(jit.M8086FarFlowEmitSelections > 0, "far JMP indirect FF /5 was not emitted.");
        Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));   // == newIp
        Assert.Equal(interp.GetRegister("CS"), inner.GetRegister("CS"));   // == newCs
        Assert.Equal((ushort)newIp, (ushort)inner.GetRegister("IP"));
        Assert.Equal((ushort)newCs, (ushort)inner.GetRegister("CS"));
    }

    /// <summary>FF /3 far CALL indirect (m16:16): read (IP,CS) from memory, push the far return frame (CS then IP),
    /// set CS:IP. FF 1E 00 02 = CALL FAR [0x0200]. Asserts CS:IP + SP -= 4 + the pushed frame vs the oracle.</summary>
    [Fact]
    public void Far_call_indirect_ff3_pushes_frame_and_loads_cs_ip_from_memory()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ip = 0x0000, ss = 0x3000, sp = 0x0100;
        const ushort newIp = 0x0100, newCs = 0x4000;
        byte[] code = [0xFF, 0x1E, 0x00, 0x02];   // FF /3 mod=00 rm=110 (disp16) → CALL FAR [0x0200]

        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) jbus.Write8(cphys + (uint)i, code[i]);
        jbus.Write8(0x0200, unchecked((byte)newIp)); jbus.Write8(0x0201, (byte)(newIp >> 8));
        jbus.Write8(0x0202, unchecked((byte)newCs)); jbus.Write8(0x0203, (byte)(newCs >> 8));
        var inner = new M8086Cpu(jbus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip); inner.SetRegister("DS", 0x0000);
        inner.SetRegister("SS", ss); inner.SetRegister("SP", sp);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
        long budget = 1; jit.Run(ref budget);

        var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        ibus.MapMemory(0, new byte[0x100000], writable: true);
        for (int i = 0; i < code.Length; i++) ibus.Write8(cphys + (uint)i, code[i]);
        ibus.Write8(0x0200, unchecked((byte)newIp)); ibus.Write8(0x0201, (byte)(newIp >> 8));
        ibus.Write8(0x0202, unchecked((byte)newCs)); ibus.Write8(0x0203, (byte)(newCs >> 8));
        var interp = new M8086Cpu(ibus);
        interp.SetRegister("CS", cs); interp.SetRegister("IP", ip); interp.SetRegister("DS", 0x0000);
        interp.SetRegister("SS", ss); interp.SetRegister("SP", sp);
        interp.Step();

        Assert.True(jit.M8086FarFlowEmitSelections > 0, "far CALL indirect FF /3 was not emitted.");
        Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));   // == newIp
        Assert.Equal(interp.GetRegister("CS"), inner.GetRegister("CS"));   // == newCs
        Assert.Equal(interp.GetRegister("SP"), inner.GetRegister("SP"));   // SP -= 4
        // The pushed far frame (IP at the lower word, CS above) — byte-identical to the oracle.
        uint stackPhys = (uint)(((ss << 4) + interp.GetRegister("SP")) & 0xFFFFF);
        for (uint k = 0; k < 4; k++)
            Assert.Equal(ibus.Read8(stackPhys + k), jbus.Read8(stackPhys + k));
        const ushort retIp = ip + 4;   // fallThrough = pc + length(FF 1E 00 02) = $0004
        Assert.Equal((byte)(retIp & 0xFF), jbus.Read8(stackPhys));
        Assert.Equal((byte)(cs & 0xFF), jbus.Read8(stackPhys + 2));
    }

    /// <summary>FF /5 with mod=11 (FF EB = mod=11, reg=5, rm=3=BP+DI): the interpreter does NOT special-case
    /// register-direct far indirect — ComputeX86Ea ignores mod, so it resolves to the MEMORY EA [BP+DI] (SS-based)
    /// and reads a far pointer there. The JIT EA machinery mirrors that one-for-one, so the emitted CS:IP must be
    /// byte-identical to the interpreter. Seed BP/DI and a far pointer at SS:[BP+DI]; assert CS:IP match.</summary>
    [Fact]
    public void Far_jmp_indirect_mod11_resolves_to_memory_like_the_oracle()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        const ushort cs = 0x2000, ip = 0x0000, ss = 0x1000, bp = 0x0100, di = 0x0020;
        const ushort newIp = 0x0ABC, newCs = 0x3000;
        byte[] code = [0xFF, 0xEB];   // FF /5 mod=11 rm=3 → EA = [BP+DI] (SS default, no disp)
        // far pointer at SS:[BP+DI] = phys (ss<<4)+(bp+di) = 0x10000 + 0x0120 = 0x10120
        uint ptrPhys = (uint)(((ss << 4) + ((bp + di) & 0xFFFF)) & 0xFFFFF);

        var jbus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        jbus.MapMemory(0, new byte[0x100000], writable: true);
        uint cphys = (uint)(((cs << 4) + ip) & 0xFFFFF);
        for (int i = 0; i < code.Length; i++) jbus.Write8(cphys + (uint)i, code[i]);
        jbus.Write8(ptrPhys, unchecked((byte)newIp)); jbus.Write8(ptrPhys + 1, (byte)(newIp >> 8));
        jbus.Write8(ptrPhys + 2, unchecked((byte)newCs)); jbus.Write8(ptrPhys + 3, (byte)(newCs >> 8));
        var inner = new M8086Cpu(jbus);
        inner.SetRegister("CS", cs); inner.SetRegister("IP", ip);
        inner.SetRegister("SS", ss); inner.SetRegister("BP", bp); inner.SetRegister("DI", di);
        var jit = new JittedCpu<M8086Cpu>(inner, M8086Cpu.JitTarget, jbus);
        long budget = 1; jit.Run(ref budget);

        var ibus = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);
        ibus.MapMemory(0, new byte[0x100000], writable: true);
        for (int i = 0; i < code.Length; i++) ibus.Write8(cphys + (uint)i, code[i]);
        ibus.Write8(ptrPhys, unchecked((byte)newIp)); ibus.Write8(ptrPhys + 1, (byte)(newIp >> 8));
        ibus.Write8(ptrPhys + 2, unchecked((byte)newCs)); ibus.Write8(ptrPhys + 3, (byte)(newCs >> 8));
        var interp = new M8086Cpu(ibus);
        interp.SetRegister("CS", cs); interp.SetRegister("IP", ip);
        interp.SetRegister("SS", ss); interp.SetRegister("BP", bp); interp.SetRegister("DI", di);
        interp.Step();

        Assert.True(jit.M8086FarFlowEmitSelections > 0, "far JMP indirect (mod=11) was not emitted.");
        Assert.Equal(interp.GetRegister("IP"), inner.GetRegister("IP"));   // byte-identical EA resolution
        Assert.Equal(interp.GetRegister("CS"), inner.GetRegister("CS"));
    }

    // ─────────────────────────── emit-not-fallback + the fallback-stays-fallback exclusion ───────────────────────────

    /// <summary>The far opcodes LEFT the fallback path (ADR 0019 FF-2 gate 1, the FallbackEmitCount-drop pin): each
    /// of 9A/EA/CB/CA/FF /3 /5 compiles to a block whose ONLY op is the far transfer with ZERO fallbacks and the far
    /// arm dispatched (M8086FarFlowEmitSelections &gt; 0). Before FF-2 every one of these fell back (FallbackEmitCount
    /// == 1, far selections == 0).</summary>
    [Theory]
    [InlineData(new byte[] { 0xEA, 0x00, 0x01, 0x00, 0x40 })]            // EA far JMP 0x4000:0x0100
    [InlineData(new byte[] { 0x9A, 0x00, 0x01, 0x00, 0x40 })]            // 9A far CALL 0x4000:0x0100
    [InlineData(new byte[] { 0xCB })]                                    // CB RETF
    [InlineData(new byte[] { 0xCA, 0x04, 0x00 })]                        // CA RETF imm16
    [InlineData(new byte[] { 0xFF, 0x2E, 0x00, 0x02 })]                  // FF /5 far JMP [0x0200]
    [InlineData(new byte[] { 0xFF, 0x1E, 0x00, 0x02 })]                  // FF /3 far CALL [0x0200]
    public void Far_opcode_emits_with_zero_fallback(byte[] code)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var c = MakeCompiler(0x2000, 0x0000, code);
        _ = c.Compile(LinearKey(0x2000, 0x0000));        // FF-1: Compile takes the LINEAR key (CS<<4)+IP, not the bare IP
        Assert.Equal(0, c.FallbackEmitCount);            // the far op EMITTED real IL — NOTHING fell back
        Assert.True(c.M8086FarFlowEmitSelections > 0,    // ... and the FAR arm actually dispatched (non-vacuous)
            "the far arm was not selected — the far gate-flip / dispatch route is not wired.");
        Assert.Equal(0, c.M8086FlowEmitSelections);      // the NEAR arm never sees a far op
    }

    /// <summary>The NEGATIVE control (ADR 0019 Decision 3): INT3 (CC), INT n (CD), INTO (CE), IRET (CF), and BOUND
    /// (62) STAY interpreter-fallback — the far arm never claims them (they are not in IsM8086FarFlowOpcode /
    /// IsEmittableX86FarFlow). Each compiles to a single FALLBACK block (FallbackEmitCount == 1) with the far arm
    /// dispatched ZERO times.</summary>
    [Theory]
    [InlineData(new byte[] { 0xCC })]                    // INT3
    [InlineData(new byte[] { 0xCD, 0x21 })]              // INT 21h
    [InlineData(new byte[] { 0xCE })]                    // INTO
    [InlineData(new byte[] { 0xCF })]                    // IRET
    [InlineData(new byte[] { 0x62, 0x06, 0x00, 0x02 })]  // BOUND r16, m16&16
    public void Int_into_iret_bound_stay_fallback(byte[] code)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var c = MakeCompiler(0x2000, 0x0000, code);
        _ = c.Compile(LinearKey(0x2000, 0x0000));        // FF-1: Compile takes the LINEAR key (CS<<4)+IP, not the bare IP
        Assert.Equal(1, c.FallbackEmitCount);            // it fell back (the interpreter is the oracle)
        Assert.Equal(0, c.M8086FarFlowEmitSelections);   // the far arm NEVER claimed it (Decision 3)
    }
}
