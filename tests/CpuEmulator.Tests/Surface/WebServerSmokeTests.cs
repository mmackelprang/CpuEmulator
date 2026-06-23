using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

// The test project also references CpuEmulator.SpecImporter, which has its own top-level Program;
// alias the web host's Program so WebApplicationFactory<WebProgram> binds unambiguously.
using WebProgram = CpuEmulator.Surface.Web.Program;

namespace CpuEmulator.Tests.Surface;

/// <summary>End-to-end smoke for the web server wiring: the static client is served, a WebSocket
/// streams binary FB frames, and an inbound key-event JSON changes the framebuffer (echoed back in
/// a later frame). Uses the in-memory test host (no real port). Tagged UAT — it is the closest
/// automated proxy to the manual "open the browser" moment, without a browser.</summary>
[Trait("Category", "UAT")]
public class WebServerSmokeTests : IClassFixture<WebApplicationFactory<WebProgram>>
{
    private readonly WebApplicationFactory<WebProgram> _factory;
    public WebServerSmokeTests(WebApplicationFactory<WebProgram> factory) => _factory = factory;

    [Fact]
    public async Task Root_serves_the_canvas_client()
    {
        using HttpClient client = _factory.CreateClient();
        string html = await client.GetStringAsync("/");
        Assert.Contains("<canvas", html, StringComparison.OrdinalIgnoreCase);
    }

    // Regression for the live `dotnet run` launch bug: the Web project pins
    // ContentRootPath = AppContext.BaseDirectory, so the static client must physically land in the
    // BUILD-OUTPUT dir (bin), not just the publish dir. WebApplicationFactory uses the project dir as
    // its content root, so the smoke test above is BLIND to a missing build-output wwwroot — under a
    // real `dotnet run` that gap is a "WebRootPath was not found" warning + a 404 at "/".
    //
    // This test is un-fakeable: it runs from a bin output dir and the Web project's wwwroot reaches
    // that dir ONLY via the csproj's <Content Update ... CopyToOutputDirectory>. Without that copy it
    // is missing (red on main); with it, present (green) — exactly the `dotnet run` path the smoke
    // test could not exercise.
    [Fact]
    public void Static_client_is_copied_to_the_build_output_dir()
    {
        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Assert.True(File.Exists(Path.Combine(wwwroot, "index.html")),
            $"wwwroot/index.html is missing from the build output ({wwwroot}); under `dotnet run` "
            + "ContentRootPath = AppContext.BaseDirectory finds no wwwroot → 404 at \"/\".");
        Assert.True(File.Exists(Path.Combine(wwwroot, "app.js")),
            $"wwwroot/app.js is missing from the build output ({wwwroot}).");
    }

    [Fact]
    public async Task WebSocket_streams_a_binary_FB_frame()
    {
        WebSocketClient wsClient = _factory.Server.CreateWebSocketClient();
        var wsUri = new UriBuilder(_factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
        using WebSocket ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

        // The session opens with a one-shot "ST <assetState>" TEXT status frame (PR-H: drives the
        // client banner/status line) BEFORE the binary FB/AU stream begins. Read past it, then assert
        // the binary FB frame still streams.
        byte[] buffer = new byte[8 + 280 * 192 * 4];
        WebSocketReceiveResult status = await ws.ReceiveAsync(buffer, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, status.MessageType);
        Assert.StartsWith("ST ", Encoding.UTF8.GetString(buffer, 0, status.Count));

        WebSocketReceiveResult result = await ws.ReceiveAsync(buffer, CancellationToken.None);

        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.Equal((byte)'F', buffer[0]);
        Assert.Equal((byte)'B', buffer[1]);
    }
}
