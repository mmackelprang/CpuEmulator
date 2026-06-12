using System.Text.Json;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// JSON shape for one TomHarte SingleStepTests case.
///
/// File: ProcessorTests/6502/v1/{xx}.json (one file per opcode, 10,000 cases each)
///
/// JSON shape example:
/// <code>
/// { "name": "b1 28 b5",
///   "initial": { "pc": 59082, "s": 39, "a": 57, "x": 33, "y": 174, "p": 96,
///                "ram": [ [59082, 177], [59083, 40], [40, 160], [41, 233], [59982, 119] ] },
///   "final":   { "pc": 59084, "s": 39, "a": 119, "x": 33, "y": 174, "p": 96,
///                "ram": [ ...same shape... ] },
///   "cycles":  [ [59082, 177, "read"], [59083, 40, "read"], [40, 160, "read"],
///                [41, 233, "read"], [59982, 119, "read"] ] }
/// </code>
///
/// The mixed-type cycles arrays (number, number, string) make DTO deserialization more
/// trouble than it is worth; we use JsonDocument traversal instead.
/// </summary>
internal sealed record TomHarteRam(ushort Address, byte Value);

internal sealed record TomHarteCycle(uint Address, byte Value, bool IsRead)
{
    public override string ToString() => $"{(IsRead ? "R" : "W")} {Address:X4}={Value:X2}";
}

internal sealed record TomHarteState(
    ushort Pc, byte S, byte A, byte X, byte Y, byte P, TomHarteRam[] Ram);

internal sealed record TomHarteCase(
    string Name, TomHarteState Initial, TomHarteState Final, TomHarteCycle[] Cycles);

internal static class TomHarteLoader
{
    public static List<TomHarteCase> LoadFile(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var cases = new List<TomHarteCase>(10_000);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            cases.Add(new TomHarteCase(
                element.GetProperty("name").GetString()!,
                ReadState(element.GetProperty("initial")),
                ReadState(element.GetProperty("final")),
                [.. element.GetProperty("cycles").EnumerateArray().Select(ReadCycle)]));
        }
        return cases;
    }

    private static TomHarteState ReadState(JsonElement element) => new(
        (ushort)element.GetProperty("pc").GetUInt32(),
        element.GetProperty("s").GetByte(),
        element.GetProperty("a").GetByte(),
        element.GetProperty("x").GetByte(),
        element.GetProperty("y").GetByte(),
        element.GetProperty("p").GetByte(),
        [.. element.GetProperty("ram").EnumerateArray().Select(static pair =>
        {
            using var items = pair.EnumerateArray();
            items.MoveNext(); ushort address = (ushort)items.Current.GetUInt32();
            items.MoveNext(); byte value = items.Current.GetByte();
            return new TomHarteRam(address, value);
        })]);

    private static TomHarteCycle ReadCycle(JsonElement triple)
    {
        using var items = triple.EnumerateArray();
        items.MoveNext(); uint address = items.Current.GetUInt32();
        items.MoveNext(); byte value = items.Current.GetByte();
        items.MoveNext(); bool isRead = items.Current.GetString() == "read";
        return new TomHarteCycle(address, value, isRead);
    }
}
