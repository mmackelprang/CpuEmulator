using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using WebProgram = CpuEmulator.Surface.Web.Program;

namespace CpuEmulator.Tests.Surface;

/// <summary>PR-T served-asset gate (hybrid strategy part a): the control-strip DOM, the one new
/// --drive-active token, the control wiring, and the D5 ctrl send are present in the SERVED /app.js +
/// /index.html. This is the automated half — the in-browser visual confirmation (panel rendering, the
/// amber light) is owner UAT. No headless browser, no new JS toolchain.</summary>
[Trait("Category", "UAT")]
public class ControlStripAssetTests : IClassFixture<WebApplicationFactory<WebProgram>>
{
    private readonly WebApplicationFactory<WebProgram> _factory;
    public ControlStripAssetTests(WebApplicationFactory<WebProgram> factory) => _factory = factory;

    [Fact]
    public async Task Index_carries_the_drive_panels_mode_label_token_and_banner()
    {
        using var client = _factory.CreateClient();
        string html = await client.GetStringAsync("/");

        Assert.Contains("id=\"control-strip\"", html);
        Assert.Contains("id=\"drive-1\"", html);
        Assert.Contains("id=\"drive-2\"", html);
        Assert.Contains("id=\"mode-label\"", html);
        Assert.Contains("id=\"asset-banner\"", html);
        Assert.Contains("--drive-active: #d8a657", html);   // the ONE new token, exact value
        Assert.Contains("accept=\".woz,.dsk,.po\"", html);  // the upload picker allow-list
        Assert.Contains("Drive 1", html);
        Assert.Contains("Drive 2", html);
        // The hint line still names the real chords (copy.md §4).
        Assert.Contains("<kbd>Ctrl+B</kbd>", html);
        Assert.Contains("<kbd>Ctrl+Backspace</kbd>", html);
    }

    [Fact]
    public async Task AppJs_carries_the_control_wiring_and_the_ctrl_send()
    {
        using var client = _factory.CreateClient();
        string js = await client.GetStringAsync("/app.js");

        // The renderer + populate + wiring (the panel behavior binds to the shipped seams).
        Assert.Contains("function renderControlStrip", js);
        Assert.Contains("function populateLibrary", js);
        Assert.Contains("function wireDrivePanels", js);
        // The bindings to the shipped P/R/S seams.
        Assert.Contains("window.insertFromLibrary", js);
        Assert.Contains("window.ejectDrive", js);
        Assert.Contains("window.uploadDisk", js);
        Assert.Contains("window.machineStatus", js);
        // D5 client half: the ctrl field is sent and Ctrl+B/Ctrl+C are guarded.
        Assert.Contains("ctrl: ev.ctrlKey", js);
        Assert.Contains("ev.code === \"KeyB\"", js);
        Assert.Contains("ev.code === \"KeyC\"", js);
    }
}
