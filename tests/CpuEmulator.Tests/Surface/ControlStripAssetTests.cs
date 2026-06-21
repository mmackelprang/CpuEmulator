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
        // The hint line still names the real chords (copy.md §4) — the booted state is server-rendered markup.
        Assert.Contains("<kbd>Ctrl+B</kbd>", html);
        Assert.Contains("<kbd>Ctrl+Backspace</kbd>", html);

        // --- Apple ][+ surface-polish gate (chore/apple2-surface-polish) ---
        // M2: the status line announces changes to assistive tech.
        Assert.Contains("id=\"status\"", html);
        Assert.Contains("aria-live=\"polite\"", html);
        Assert.Contains("<div id=\"status\" aria-live=\"polite\">", html);
        // L1: the asset-banner reads as a calm panel — #ccc text, no undocumented #cc8 anywhere.
        Assert.Contains("color: #ccc", html);
        Assert.DoesNotContain("#cc8", html);
        // L2: --drive-active stays; the redundant --drive-idle is gone (it duplicated #888/--muted).
        Assert.Contains("--drive-active: #d8a657", html);
        Assert.DoesNotContain("--drive-idle", html);
        // L3: a focus-visible ring is present on the control-strip controls.
        Assert.Contains(":focus-visible", html);
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

    [Fact]
    public async Task AppJs_carries_the_apple2_surface_polish_copy_and_state()
    {
        using var client = _factory.CreateClient();
        string js = await client.GetStringAsync("/app.js");

        // H1: the close/error status strings (copy.md §3).
        Assert.Contains("disconnected — reload to reconnect", js);
        Assert.Contains("connection error — is the server running?", js);
        // H2: the three-line §5 banner, the <kbd>-wrapped script, and NO "(or .ps1)" run-on.
        Assert.Contains("Apple ][+ ROMs not found — showing the demo pattern.", js);
        Assert.Contains("tools/get-apple2-roms.sh", js);
        Assert.Contains("<kbd>tools/get-apple2-roms.sh</kbd>", js);
        Assert.Contains("then reload this page.", js);
        Assert.DoesNotContain("(or .ps1)", js);
        // H3: the demo-fallback hint omits the chords (copy.md §4).
        Assert.Contains("Fetch the ROMs to boot a real Apple ][+:", js);
        // H4 / copy.md §6.5: the disabled-drive note (split across the two panels per layout.md §3).
        Assert.Contains("Insert a disk after", js);
        Assert.Contains("fetching the Apple ROMs.", js);
        // M3: the ad-hoc " · drive ●" status tail is gone — the per-drive light carries motor state.
        Assert.DoesNotContain("drive ●", js);
        // M4: the 3 s "no frame yet" diagnostic (copy.md §3).
        Assert.Contains("connected · waiting for first frame…", js);
        // M6: the UPLOADING label interpolates the captured filename.
        Assert.Contains("\"Uploading \" + name + \"…\"", js);
    }
}
