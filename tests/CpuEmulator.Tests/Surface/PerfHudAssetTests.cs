using Microsoft.AspNetCore.Mvc.Testing;

// Alias the web host's Program (the test project also references CpuEmulator.SpecImporter, which has its
// own top-level Program) so WebApplicationFactory<WebProgram> binds unambiguously.
using WebProgram = CpuEmulator.Surface.Web.Program;

namespace CpuEmulator.Tests.Surface;

/// <summary>Served-asset / wiring gate for the perf-overlay HUD (design handoff 2026-06-23-perf-overlay):
/// the served index.html carries the HUD markup (the panel, its aria-label, the row cells, the backtick
/// hint), and app.js carries the backtick toggle + the PF-frame routing (handlePerfText / window.perfStats).
/// Mirrors FaviconAssetTests / WebServerSmokeTests — the visual HUD itself is owner UAT (no browser-
/// automation here), so this gate the DATA + WIRING the visual layer rides on.</summary>
[Trait("Category", "UAT")]
public class PerfHudAssetTests : IClassFixture<WebApplicationFactory<WebProgram>>
{
    private readonly WebApplicationFactory<WebProgram> _factory;
    public PerfHudAssetTests(WebApplicationFactory<WebProgram> factory) => _factory = factory;

    [Fact]
    public async Task Index_html_carries_the_perf_hud_markup()
    {
        using HttpClient client = _factory.CreateClient();
        string html = await client.GetStringAsync("/");

        // The overlay panel, its accessible name, and the canvas-relative wrapper that anchors it.
        Assert.Contains("id=\"perf-hud\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("aria-label=\"performance overlay\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"screen-wrap\"", html, StringComparison.OrdinalIgnoreCase);
        // A representative set of the value cells repaintHud() fills (board/guest/tier + the conditional rows).
        Assert.Contains("id=\"perf-board\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"perf-guest\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"perf-tier\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"perf-jit-row\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("id=\"perf-cpu2-row\"", html, StringComparison.OrdinalIgnoreCase);
        // The discoverability hint (the backtick toggles the HUD).
        Assert.Contains("= perf HUD", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task App_js_wires_the_backtick_toggle_and_the_PF_routing()
    {
        using HttpClient client = _factory.CreateClient();
        string js = await client.GetStringAsync("/app.js");

        // The backtick is intercepted ahead of the wire (the guest no-op key toggles the HUD).
        Assert.Contains("Backquote", js, StringComparison.Ordinal);
        Assert.Contains("toggleHud", js, StringComparison.Ordinal);
        // The PF frame is routed by prefix to handlePerfText -> window.perfStats (separate from the ST path).
        Assert.Contains("\"PF \"", js, StringComparison.Ordinal);
        Assert.Contains("handlePerfText", js, StringComparison.Ordinal);
        Assert.Contains("window.perfStats", js, StringComparison.Ordinal);
        // The client-measured FPS ring (the only client-computed metric).
        Assert.Contains("noteFrameForFps", js, StringComparison.Ordinal);
    }

    // Un-fakeable build-output check (paired with WebServerSmokeTests): under a real `dotnet run` the Web
    // project pins ContentRootPath = AppContext.BaseDirectory, so the client assets must physically land in
    // the bin output dir. The in-memory factory above uses the project dir and is blind to a missing copy.
    [Fact]
    public void Client_assets_are_copied_to_the_build_output_dir()
    {
        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        foreach (string asset in new[] { "index.html", "app.js" })
            Assert.True(File.Exists(Path.Combine(wwwroot, asset)),
                $"wwwroot/{asset} is missing from the build output ({wwwroot}).");
    }
}
