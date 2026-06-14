using System.Text.Json;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// JSON shape for one SingleStepTests Z80 case (the repo SingleStepTests/z80, v1, 1000 cases/file).
/// The schema DIFFERS from the 6502's (TomHarteCase.cs): the alternate set is carried as PACKED
/// 16-bit pairs af_/bc_/de_/hl_; there are specials i/r/wz/ei/iff1/iff2/im/p/q; the cycles array is
/// [address, value|null, "rwmi"] where the 4-char pin string is position 1 = r/-, 2 = w/-,
/// 3 = m(memory request)/-, 4 = i(I/O request)/-; null value = an electrically-disconnected T-state;
/// and a SEPARATE ports array [addr16, value, "r"/"w"] carries I/O transactions.
/// </summary>
internal sealed record Z80Ram(ushort Address, byte Value);

/// <summary>One T-state's bus snapshot. <see cref="HasData"/> is false for an internal (null-bus)
/// T-state. <see cref="IsMemRead"/>/<see cref="IsMemWrite"/> are true only when a memory request
/// (pin 'm') accompanies the read/write direction.</summary>
internal sealed record Z80Cycle(uint Address, byte Value, bool HasData, string Pins)
{
    public bool IsRead => Pins.Length > 0 && Pins[0] == 'r';
    public bool IsWrite => Pins.Length > 1 && Pins[1] == 'w';
    public bool IsMemReq => Pins.Length > 2 && Pins[2] == 'm';
    public bool IsMemRead => IsRead && IsMemReq;
    public bool IsMemWrite => IsWrite && IsMemReq;

    public override string ToString() =>
        HasData ? $"{Pins} {Address:X4}={Value:X2}" : $"{Pins} {Address:X4}=--";
}

/// <summary>One I/O port transaction (the separate Z80 ports array).</summary>
internal sealed record Z80Port(ushort Address, byte Value, bool IsRead)
{
    public override string ToString() => $"{(IsRead ? "IN" : "OUT")} {Address:X4}={Value:X2}";
}

internal sealed record Z80State(
    ushort Pc, ushort Sp, byte A, byte B, byte C, byte D, byte E, byte F, byte H, byte L,
    byte I, byte R, ushort Wz, ushort Ix, ushort Iy,
    ushort Af_, ushort Bc_, ushort De_, ushort Hl_,
    int Im, bool Iff1, bool Iff2, int Ei, int P, int Q, Z80Ram[] Ram);

internal sealed record Z80TomHarteCase(
    string Name, Z80State Initial, Z80State Final, Z80Cycle[] Cycles, Z80Port[] Ports);

internal static class Z80TomHarteLoader
{
    public static List<Z80TomHarteCase> LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var cases = new List<Z80TomHarteCase>(1000);
        foreach (var element in doc.RootElement.EnumerateArray())
            cases.Add(Parse(element));
        return cases;
    }

    public static Z80TomHarteCase Parse(JsonElement element)
    {
        var ports = element.TryGetProperty("ports", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(ReadPort).ToArray()
            : [];
        return new Z80TomHarteCase(
            element.GetProperty("name").GetString()!,
            ReadState(element.GetProperty("initial")),
            ReadState(element.GetProperty("final")),
            [.. element.GetProperty("cycles").EnumerateArray().Select(ReadCycle)],
            ports);
    }

    private static byte B(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetByte() : (byte)0;

    private static ushort U16(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (ushort)v.GetUInt32() : (ushort)0;

    private static int I32(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static bool Bit(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() != 0;

    private static Z80State ReadState(JsonElement e) => new(
        U16(e, "pc"), U16(e, "sp"), B(e, "a"), B(e, "b"), B(e, "c"), B(e, "d"), B(e, "e"),
        B(e, "f"), B(e, "h"), B(e, "l"), B(e, "i"), B(e, "r"), U16(e, "wz"), U16(e, "ix"), U16(e, "iy"),
        U16(e, "af_"), U16(e, "bc_"), U16(e, "de_"), U16(e, "hl_"),
        I32(e, "im"), Bit(e, "iff1"), Bit(e, "iff2"), I32(e, "ei"), I32(e, "p"), I32(e, "q"),
        [.. e.GetProperty("ram").EnumerateArray().Select(static pair =>
        {
            using var items = pair.EnumerateArray();
            items.MoveNext(); ushort address = (ushort)items.Current.GetUInt32();
            items.MoveNext(); byte value = items.Current.GetByte();
            return new Z80Ram(address, value);
        })]);

    private static Z80Cycle ReadCycle(JsonElement triple)
    {
        using var items = triple.EnumerateArray();
        items.MoveNext();
        uint address = items.Current.ValueKind == JsonValueKind.Number ? items.Current.GetUInt32() : 0;
        items.MoveNext();
        bool hasData = items.Current.ValueKind == JsonValueKind.Number;
        byte value = hasData ? items.Current.GetByte() : (byte)0;
        items.MoveNext();
        string pins = items.Current.GetString() ?? "----";
        return new Z80Cycle(address, value, hasData, pins);
    }

    private static Z80Port ReadPort(JsonElement triple)
    {
        using var items = triple.EnumerateArray();
        items.MoveNext(); ushort address = (ushort)items.Current.GetUInt32();
        items.MoveNext(); byte value = items.Current.GetByte();
        items.MoveNext(); string dir = items.Current.GetString() ?? "r";
        return new Z80Port(address, value, dir == "r");
    }
}
