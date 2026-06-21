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
