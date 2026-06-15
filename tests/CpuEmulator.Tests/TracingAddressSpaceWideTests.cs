using CpuEmulator.Core;
using CpuEmulator.Tests.Mos6502;
using Xunit;

namespace CpuEmulator.Tests;

/// <summary>M4.2 (ADR 0003 Decision 2 / Decision D4) — the TracingAddressSpace records per-access size
/// (.b/.w/.l). The M4.5 mnemonic-keyed TomHarte gate diffs WORD-granular transactions (the
/// ["r", 4, 6, 3076, ".w", 1657] vector shape), so a Read16 must record ONE Word transaction, not two
/// bytes. A byte access records Byte (the default — the 6502 trace tests are unchanged).</summary>
public class TracingAddressSpaceWideTests
{
    private static (TracingAddressSpace tracer, AddressSpace inner) NewTracer(Endianness endianness)
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, 16, endianness: endianness);
        inner.MapMemory(0x0000, new byte[0x1000], writable: true);
        return (new TracingAddressSpace(inner), inner);
    }

    [Fact]
    public void Byte_access_records_a_single_Byte_transaction()
    {
        var (tracer, _) = NewTracer(Endianness.BigEndian);
        tracer.Write8(0x0010, 0xAB);
        _ = tracer.Read8(0x0010);
        Assert.Collection(tracer.Trace,
            e => { Assert.Equal(0x0010u, e.Address); Assert.False(e.IsRead); Assert.Equal(AccessWidth.Byte, e.Width); },
            e => { Assert.Equal(0x0010u, e.Address); Assert.True(e.IsRead);  Assert.Equal(AccessWidth.Byte, e.Width); });
    }

    [Fact]
    public void Word_access_records_a_single_Word_transaction()
    {
        var (tracer, _) = NewTracer(Endianness.BigEndian);
        tracer.Write16(0x0020, 0xBEEF);
        _ = tracer.Read16(0x0020);
        // ONE Word write + ONE Word read — NOT four byte entries. The full 16-bit value is recorded
        // (not truncated to a byte), so the M4.5 trace gate can diff the word value.
        Assert.Collection(tracer.Trace,
            e => { Assert.Equal(0x0020u, e.Address); Assert.False(e.IsRead); Assert.Equal(AccessWidth.Word, e.Width); Assert.Equal(0xBEEFu, e.Value); },
            e => { Assert.Equal(0x0020u, e.Address); Assert.True(e.IsRead);  Assert.Equal(AccessWidth.Word, e.Width); Assert.Equal(0xBEEFu, e.Value); });
    }

    [Fact]
    public void Long_access_records_a_single_Long_transaction()
    {
        var (tracer, _) = NewTracer(Endianness.BigEndian);
        tracer.Write32(0x0040, 0xDEADBEEF);
        _ = tracer.Read32(0x0040);
        // The full 32-bit value is recorded (not truncated to a byte).
        Assert.Collection(tracer.Trace,
            e => { Assert.Equal(0x0040u, e.Address); Assert.False(e.IsRead); Assert.Equal(AccessWidth.Long, e.Width); Assert.Equal(0xDEADBEEFu, e.Value); },
            e => { Assert.Equal(0x0040u, e.Address); Assert.True(e.IsRead);  Assert.Equal(AccessWidth.Long, e.Width); Assert.Equal(0xDEADBEEFu, e.Value); });
    }

    [Fact]
    public void Word_write_actually_writes_through_to_the_inner_space_big_endian()
    {
        var (tracer, inner) = NewTracer(Endianness.BigEndian);
        tracer.Write16(0x0050, 0x1234);
        // The trace is one Word entry, but the bytes still land in the inner RAM, big-endian:
        Assert.Equal(0x12, inner.Read8(0x0050));
        Assert.Equal(0x34, inner.Read8(0x0051));
        Assert.Equal((ushort)0x1234, tracer.Read16(0x0050));   // (adds a Word read to the trace)
    }
}
