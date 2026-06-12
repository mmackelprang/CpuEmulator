using CpuEmulator.Host;

namespace CpuEmulator.Tests.Host;

/// <summary>
/// Tests for HostOptions.TryParse — the pure, testable surface of the host's
/// command line. Program.Main is thin console glue exercised by manual smoke.
/// </summary>
public class HostOptionsTests
{
    [Fact]
    public void Empty_args_is_repl_mode()
    {
        bool ok = HostOptions.TryParse([], out HostOptions options, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.False(options.Demo);
        Assert.Null(options.LoadPath);
    }

    [Fact]
    public void Demo_flag_sets_demo()
    {
        bool ok = HostOptions.TryParse(["--demo"], out HostOptions options, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(options.Demo);
    }

    [Fact]
    public void Load_defaults_at_to_0200_and_pc_to_null()
    {
        bool ok = HostOptions.TryParse(["--load", "x.bin"], out HostOptions options, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("x.bin", options.LoadPath);
        Assert.Equal(0x0200u, options.LoadAt);
        Assert.Null(options.Pc);
    }

    [Fact]
    public void Load_with_at_and_pc_parses_dollar_hex()
    {
        bool ok = HostOptions.TryParse(["--load", "x.bin", "--at", "$A000", "--pc", "$0400"],
            out HostOptions options, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal("x.bin", options.LoadPath);
        Assert.Equal(0xA000u, options.LoadAt);
        Assert.Equal(0x0400u, options.Pc);
    }

    [Fact]
    public void At_without_dollar_prefix_parses_monitor_convention()
    {
        bool ok = HostOptions.TryParse(["--load", "x.bin", "--at", "A000"],
            out HostOptions options, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Equal(0xA000u, options.LoadAt);
    }

    [Fact]
    public void At_without_load_is_an_error()
    {
        bool ok = HostOptions.TryParse(["--at", "$A000"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("--at", error);
        Assert.Contains("--load", error);
    }

    [Fact]
    public void Pc_without_load_is_an_error()
    {
        bool ok = HostOptions.TryParse(["--pc", "$0400"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("--pc", error);
        Assert.Contains("--load", error);
    }

    [Fact]
    public void Bad_address_is_an_error()
    {
        bool ok = HostOptions.TryParse(["--load", "x.bin", "--at", "zz"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Demo_and_load_are_mutually_exclusive()
    {
        bool ok = HostOptions.TryParse(["--demo", "--load", "x.bin"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void Unknown_option_is_an_error_naming_the_option()
    {
        bool ok = HostOptions.TryParse(["--frob"], out _, out string? error);

        Assert.False(ok);
        Assert.Equal("unknown option '--frob'", error);
    }

    // ── --terminal ────────────────────────────────────────────────────────────

    [Fact]
    public void Terminal_flag_sets_terminal()
    {
        bool ok = HostOptions.TryParse(["--terminal"], out HostOptions options, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(options.Terminal);
        Assert.False(options.Demo);
    }

    [Fact]
    public void Terminal_and_demo_are_mutually_exclusive()
    {
        bool ok = HostOptions.TryParse(["--terminal", "--demo"], out _, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("--terminal", error);
        Assert.Contains("--demo", error);
    }

    [Fact]
    public void Terminal_with_load_and_pc_is_a_legal_combo()
    {
        // Load a binary, set PC, then free-run it in terminal mode.
        bool ok = HostOptions.TryParse(["--terminal", "--load", "x.bin", "--pc", "$0300"],
            out HostOptions options, out string? error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.True(options.Terminal);
        Assert.Equal("x.bin", options.LoadPath);
        Assert.Equal(0x0300u, options.Pc);
    }
}
