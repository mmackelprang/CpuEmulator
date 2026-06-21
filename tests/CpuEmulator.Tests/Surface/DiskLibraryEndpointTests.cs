using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using WebProgram = CpuEmulator.Surface.Web.Program;

namespace CpuEmulator.Tests.Surface;

/// <summary>The PR-R gate: the catalog lists a seeded cache dir, and selecting an entry inserts it into a
/// running drive via the shipped PR-Q insert path. Uses the in-memory test host for the WS session-health
/// leg. NOTE: this class never mutates the process-global <c>CPUEMULATOR_TESTVECTORS</c> env var — that var
/// is read live by the parallel TomHarte/Klaus vector suites, so mutating it here would corrupt their vector
/// resolution under the assembly's parallel test collections. The catalog-listing leg uses the
/// <c>DiskCatalog.List(root)</c> test seam (the same seam <see cref="CpuEmulator.Machines.Apple2Rom"/> uses),
/// and the insert leg drives the resolve -> read -> insert path directly with the seam; the WS leg only
/// asserts the receive loop stays healthy on a disk-insert text frame (board-agnostic, no asset needed).</summary>
[Trait("Category", "UAT")]
public class DiskLibraryEndpointTests : IClassFixture<WebApplicationFactory<WebProgram>>
{
    private readonly WebApplicationFactory<WebProgram> _factory;
    public DiskLibraryEndpointTests(WebApplicationFactory<WebProgram> factory) => _factory = factory;

    // A cache root seeded with one .dsk in disks/. The CP/M boot disk is omitted (the listing leg only needs
    // the library entry). Pure temp dir — never wired to the process env var.
    private static string SeedRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cpuemu-libgate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "disks"));
        File.WriteAllBytes(Path.Combine(root, "disks", "DOS33.dsk"), DistinctiveDsk());
        return root;
    }

    private static byte[] DistinctiveDsk()
    {
        var img = new byte[35 * 16 * 256];
        for (int i = 0; i < img.Length; i++) img[i] = (byte)((i + 1) & 0xFF);
        return img;
    }

    [Fact]
    public void The_catalog_lists_the_seeded_library_dir()
    {
        string root = SeedRoot();
        try
        {
            // The /disks endpoint is a one-line wrapper over DiskCatalog.List(); the listing logic is what
            // the gate exercises. Drive it through the seam so no process-global env var is touched.
            var entries = CpuEmulator.Machines.DiskCatalog.List(root);
            CpuEmulator.Machines.DiskCatalogEntry? dos =
                entries.FirstOrDefault(e => e.Id == "lib/DOS33.dsk");
            Assert.NotNull(dos);
            Assert.Equal("dsk", dos!.Format);
            Assert.True(dos.Supported);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task GET_disks_serves_a_json_array()
    {
        // The endpoint is wired and returns a JSON array (it reads the server's own default cache root,
        // which may be empty or populated — either way an array). This proves the route exists + serializes
        // without depending on a seeded asset (the listing content is covered by the seam test above).
        using HttpClient client = _factory.CreateClient();
        string json = await client.GetStringAsync("/disks");
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }

    [Fact]
    public async Task A_disk_insert_text_frame_keeps_the_live_session_streaming()
    {
        // The un-fakeable session-health leg: a disk-insert text frame goes through the (reordered) receive
        // loop without crashing it, and the session keeps streaming FB frames. Board-agnostic (the demo /
        // Spectrum / Apple branch all stream FB) — no env var, no seeded asset. If the server's own cache has
        // no such disk the insert is a no-op; the invariant under test is "the new disk-command branch does
        // not break the receive loop / FB stream".
        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
        using WebSocket ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

        string insert = "{\"action\":\"disk-insert\",\"drive\":1,\"id\":\"lib/DOS33.dsk\"}";
        await ws.SendAsync(Encoding.UTF8.GetBytes(insert), WebSocketMessageType.Text, true, CancellationToken.None);

        byte[] buffer = new byte[8 + 560 * 216 * 4];
        bool sawFb = false;
        for (int i = 0; i < 300 && !sawFb; i++)
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Binary && buffer[0] == (byte)'F' && buffer[1] == (byte)'B')
                sawFb = true;
        }
        Assert.True(sawFb, "the session must keep streaming after a disk-insert text frame");
    }

    [Fact]
    public void A_resolved_library_disk_loads_and_a_running_read_pulls_a_nibble()
    {
        string root = SeedRoot();
        try
        {
            // Resolve the catalog id to bytes exactly as the server's disk-insert handler does — via the
            // root seam, so no process-global env var is touched.
            Assert.True(CpuEmulator.Machines.DiskCatalog.TryResolve("lib/DOS33.dsk",
                out string path, out string format, root));
            byte[] bytes = File.ReadAllBytes(path);
            var fmt = format switch
            {
                "po" => CpuEmulator.Surface.Web.DiskFormat.Po,
                "woz" => CpuEmulator.Surface.Web.DiskFormat.Woz,
                _ => CpuEmulator.Surface.Web.DiskFormat.Dsk,
            };

            var sys = new byte[0x3000];
            sys[0x2FFC] = 0x62; sys[0x2FFD] = 0xFA;
            CpuEmulator.Surface.Web.Apple2Surface surface = CpuEmulator.Surface.Web.Apple2Surface.Create(
                sys, diskBootRom: null, charRom: null, frameSink: _ => { }, audioSink: _ => { });
            surface.InsertDisk(1, bytes, fmt, "DOS33");

            var bus = surface.Machine.Space(CpuEmulator.Core.AddressSpaceKind.Program);
            bus.Read8(0xC0E9);   // motor on
            bool sawNibble = false;
            for (int i = 0; i < 50_000 && !sawNibble; i++)
                if ((bus.Read8(0xC0EC) & 0x80) != 0) sawNibble = true;
            Assert.True(sawNibble, "a resolved-and-loaded library .dsk must read back a nibble");
        }
        finally { Directory.Delete(root, true); }
    }
}
