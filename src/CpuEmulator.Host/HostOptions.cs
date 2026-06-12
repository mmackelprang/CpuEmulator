namespace CpuEmulator.Host;

/// <summary>
/// Parsed host command line. <see cref="TryParse"/> is the pure, testable surface;
/// Program.Main is thin console glue over it. Addresses follow the monitor convention:
/// hex with an optional <c>$</c> prefix.
/// </summary>
public sealed record HostOptions(bool Demo, string? LoadPath, uint LoadAt, uint? Pc, bool Terminal)
{
    public const string Usage =
        "usage: CpuEmulator.Host [--demo | [--terminal] [--load <bin> [--at $addr] [--pc $addr]]]";

    private const uint DefaultLoadAt = 0x0200;

    /// <summary>
    /// Parse args. On success returns true with <paramref name="error"/> null; on failure
    /// returns false with <paramref name="options"/> set to defaults and an error message.
    /// The --at/--pc-require---load rule is validated after all args are consumed, so
    /// option order does not matter.
    /// </summary>
    public static bool TryParse(string[] args, out HostOptions options, out string? error)
    {
        bool demo = false;
        bool terminal = false;
        string? loadPath = null;
        uint loadAt = DefaultLoadAt;
        uint? pc = null;
        bool sawAt = false, sawPc = false;

        options = new HostOptions(Demo: false, LoadPath: null, LoadAt: DefaultLoadAt, Pc: null,
                                  Terminal: false);

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--demo":
                    demo = true;
                    break;

                case "--terminal":
                    terminal = true;
                    break;

                case "--load":
                    if (++i >= args.Length)
                        return Fail("--load requires a file path", out error);
                    loadPath = args[i];
                    break;

                case "--at":
                    if (++i >= args.Length)
                        return Fail("--at requires an address", out error);
                    if (!TryParseAddress(args[i], out loadAt))
                        return Fail($"bad address '{args[i]}' for --at", out error);
                    sawAt = true;
                    break;

                case "--pc":
                    if (++i >= args.Length)
                        return Fail("--pc requires an address", out error);
                    if (!TryParseAddress(args[i], out uint pcValue))
                        return Fail($"bad address '{args[i]}' for --pc", out error);
                    pc = pcValue;
                    sawPc = true;
                    break;

                default:
                    return Fail($"unknown option '{args[i]}'", out error);
            }
        }

        if (demo && loadPath is not null)
            return Fail("--demo and --load are mutually exclusive", out error);
        if (demo && terminal)
            return Fail("--demo and --terminal are mutually exclusive", out error);
        if (loadPath is null && sawAt)
            return Fail("--at requires --load", out error);
        if (loadPath is null && sawPc)
            return Fail("--pc requires --load", out error);

        options = new HostOptions(demo, loadPath, loadAt, pc, terminal);
        error = null;
        return true;
    }

    private static bool Fail(string message, out string? error)
    {
        error = message;
        return false;
    }

    /// <summary>Hex address, '$' prefix optional (monitor convention).</summary>
    private static bool TryParseAddress(string s, out uint address)
    {
        address = 0;
        string t = s.Trim();
        if (t.StartsWith("$", StringComparison.Ordinal))
            t = t.Substring(1);
        return uint.TryParse(t, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out address);
    }
}
