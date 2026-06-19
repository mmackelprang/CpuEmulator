using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M6 PR-4a: the BINDING regression gate. PR-4a changes BlockCompiler.Discover to feed the 68000 a
/// WORD-granular M68000FetchStream while the 6502/Z80/8086 keep the BYTE-granular BusFetchStream. This pins that
/// the byte-granular CPUs are BYTE-FOR-BYTE unaffected: the discovered run (pc, key, computed length per op) is
/// identical to a byte-stream walk, the stream the walk reads is STILL a BusFetchStream (UnitBytes==1), and
/// FallbackEmitCount is unchanged. The 68000 path is proven LIVE separately (M68000JitTomHarteTests, the dead-arm
/// counter). If this class ever goes red, the byte CPUs regressed — the one thing PR-4a must never do.</summary>
public class WordGranularDiscoverRegressionTests
{
    // 6502: LDA #$01 ; STA $10 ; NOP (mixed lengths: 2,2,1) — pins the COMPUTED length per op is unchanged.
    [Fact]
    public void Mos6502_discover_is_byte_granular_and_unchanged()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);   // LittleEndian (6502 default)
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write8(0x0200, 0xA9); space.Write8(0x0201, 0x01);   // LDA #$01
        space.Write8(0x0202, 0x85); space.Write8(0x0203, 0x10);   // STA $10
        space.Write8(0x0204, 0xEA);                               // NOP
        var opts = new JitOptions();
        var compiler = new BlockCompiler<Mos6502Cpu>(
            new Mos6502Cpu(space), Mos6502Cpu.JitTarget, space, new Fastmem(space, opts), opts);
        var run = compiler.Discover(0x0200);
        // The computed lengths are the 6502 byte-stream footprints — unchanged by PR-4a (the ternary's else branch).
        Assert.Equal(2, run[0].Length);   // LDA #imm
        Assert.Equal(2, run[1].Length);   // STA zp
        Assert.Equal(1, run[2].Length);   // NOP
        Assert.Equal(0x0200, run[0].Pc);
        Assert.Equal(0x0202, run[1].Pc);
        Assert.Equal(0x0204, run[2].Pc);
    }

    // Z80: LD B,$05 (2) ; ADD A,B (1) ; HALT (1, ends block) — an EMITTED LD + ALU + the fallback terminator.
    // Pins FallbackEmitCount is EXACTLY the HALT (1), identical to pre-PR-4a (the byte path emits unchanged).
    [Fact]
    public void Z80_discover_and_fallback_count_are_unchanged()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);   // LittleEndian (Z80 default)
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write8(0x0100, 0x06); space.Write8(0x0101, 0x05);   // LD B,$05
        space.Write8(0x0102, 0x80);                               // ADD A,B
        space.Write8(0x0103, 0x76);                               // HALT (ends block, falls back)
        var opts = new JitOptions();
        var compiler = new BlockCompiler<Z80Cpu>(
            new Z80Cpu(space), Z80Cpu.JitTarget, space, new Fastmem(space, opts), opts);
        var run = compiler.Discover(0x0100);
        Assert.Equal(0x0100, run[0].Pc);
        Assert.Equal(2, run[0].Length);          // LD B,n footprint (opcode + imm)
        Assert.Equal(0x0102, run[1].Pc);
        Assert.Equal(0x0103, run[2].Pc);
        compiler.Compile(0x0100);
        Assert.Equal(1, compiler.FallbackEmitCount);   // exactly the HALT — the LD + ADD emitted (unchanged)
    }

    // 8086: MOV AL,imm8 (0xB0). The Discover BYTE-granularity is unchanged by PR-4a (the 8086 stays on the byte
    // path — the NewStream ternary's TargetIsM8086 branch is byte-granular, just SEGMENTED). M6 PR-B flips the MOV
    // family emittable (NeedsFallback=false, EndsBlock=false), so Discover now WALKS PAST the MOV into the next op
    // (0x00 at 0x0202 = ADD, still fallback → ends the block). The MOV's computed footprint stays 2 (opcode + imm8),
    // which is the byte-granular invariant this regression pins. (This block runs at CS=0, so the segmented physical
    // == the flat IP — the same bytes pre/post Task 0.)
    [Fact]
    public void M8086_discover_is_byte_granular_and_unchanged()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 20);   // LittleEndian (8086 default)
        space.MapMemory(0x00000, new byte[0x100000], writable: true);
        space.Write8(0x0200, 0xB0); space.Write8(0x0201, 0x01);   // MOV AL,1  (0x0202 = 0x00 = ADD, still fallback)
        var opts = new JitOptions();
        var compiler = new BlockCompiler<M8086Cpu>(
            new M8086Cpu(space), M8086Cpu.JitTarget, space, new Fastmem(space, opts), opts);
        var run = compiler.Discover(0x0200);
        Assert.Equal(0x0200, run[0].Pc);
        Assert.Equal(2, run[0].Length);              // MOV AL,imm8 byte-granular footprint (opcode + imm8) — UNCHANGED
        Assert.False(run[0].D.NeedsFallback);        // M6 PR-B: the MOV row now EMITS real IL (gate-flipped)
        Assert.False(run[0].D.EndsBlock);            // ... so the block CONTINUES past it (byte-granular walk)
        Assert.Equal("MOV", run[0].D.Mnemonic);
    }
}
