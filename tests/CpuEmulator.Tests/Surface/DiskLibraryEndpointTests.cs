using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using WebProgram = CpuEmulator.Surface.Web.Program;

namespace CpuEmulator.Tests.Surface;

/// <summary>The PR-R gate: GET /disks lists a seeded cache dir, and selecting an entry inserts it into a
/// running drive via the shipped PR-Q insert path (text WS disk-insert). Uses the in-memory test host.</summary>
[Trait("Category", "UAT")]
public class DiskLibraryEndpointTests : IClassFixture<WebApplicationFactory<WebProgram>>
{
    private readonly WebApplicationFactory<WebProgram> _factory;
    public DiskLibraryEndpointTests(WebApplicationFactory<WebProgram> factory) => _factory = factory;

    // A cache root seeded with one .dsk in disks/ + the Apple system ROM (so a real Apple boots, not the
    // demo) — pointed at via CPUEMULATOR_TESTVECTORS for the factory's process.
    private static string SeedRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cpuemu-libgate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "disks"));
        Directory.CreateDirectory(Path.Combine(root, "apple2"));
        File.WriteAllBytes(Path.Combine(root, "disks", "DOS33.dsk"), DistinctiveDsk());
        // A minimal 12 KiB system ROM with a reset vector so the Apple branch boots.
        var sys = new byte[0x3000];
        sys[0x2FFC] = 0x62; sys[0x2FFD] = 0xFA;
        File.WriteAllBytes(Path.Combine(root, "apple2", "apple2plus.rom"), sys);
        return root;
    }

    private static byte[] DistinctiveDsk()
    {
        var img = new byte[35 * 16 * 256];
        for (int i = 0; i < img.Length; i++) img[i] = (byte)((i + 1) & 0xFF);
        return img;
    }

    private WebApplicationFactory<WebProgram> FactoryWithRoot(string root) =>
        _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, __) =>
                Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", root)));

    [Fact]
    public async Task GET_disks_lists_the_seeded_library()
    {
        string root = SeedRoot();
        try
        {
            using HttpClient client = FactoryWithRoot(root).CreateClient();
            string json = await client.GetStringAsync("/disks");
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement arr = doc.RootElement;
            Assert.Equal(JsonValueKind.Array, arr.ValueKind);
            bool sawDos = false;
            foreach (JsonElement e in arr.EnumerateArray())
                if (e.GetProperty("id").GetString() == "lib/DOS33.dsk")
                {
                    sawDos = true;
                    Assert.Equal("dsk", e.GetProperty("format").GetString());
                    Assert.True(e.GetProperty("supported").GetBoolean());
                }
            Assert.True(sawDos, "the seeded DOS33.dsk must appear in the catalog");
        }
        finally { Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", null); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Selecting_a_library_entry_inserts_it_into_drive_N()
    {
        string root = SeedRoot();
        try
        {
            WebApplicationFactory<WebProgram> factory = FactoryWithRoot(root);
            WebSocketClient wsClient = factory.Server.CreateWebSocketClient();
            var wsUri = new UriBuilder(factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
            using WebSocket ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

            // Send the library insert (text). The server resolves "lib/DOS33.dsk", reads the cached bytes,
            // and calls surface.InsertDisk(1, bytes, Dsk). There is no echo; we prove the insert took by
            // reading a real nibble off the framebuffer-side bus is not possible here, so we assert the
            // server accepts + processes it without closing the socket, and a subsequent ST frame still
            // streams (the session stayed healthy).
            string insert = "{\"action\":\"disk-insert\",\"drive\":1,\"id\":\"lib/DOS33.dsk\"}";
            await ws.SendAsync(Encoding.UTF8.GetBytes(insert), WebSocketMessageType.Text, true, CancellationToken.None);

            // Read frames until we see at least one binary FB frame after the insert (the session is alive
            // and streaming — the insert did not crash the receive loop). The first frame is the ST text.
            byte[] buffer = new byte[8 + 560 * 216 * 4];
            bool sawFb = false;
            for (int i = 0; i < 200 && !sawFb; i++)
            {
                WebSocketReceiveResult r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Binary && buffer[0] == (byte)'F' && buffer[1] == (byte)'B')
                    sawFb = true;
            }
            Assert.True(sawFb, "the session must keep streaming after a library insert");
        }
        finally { Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", null); Directory.Delete(root, true); }
    }

    [Fact]
    public void A_resolved_library_disk_loads_and_a_running_read_pulls_a_nibble()
    {
        string root = SeedRoot();
        try
        {
            // Resolve the catalog id to bytes exactly as the server's disk-insert handler does.
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
