using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

// Alias the web host's Program (the test project also references CpuEmulator.SpecImporter, which has
// its own top-level Program) so WebApplicationFactory<WebProgram> binds unambiguously.
using WebProgram = CpuEmulator.Surface.Web.Program;

namespace CpuEmulator.Tests.Surface;

/// <summary>Serve-asset gate for the stylized chip favicon: the SVG primary and the PNG fallbacks are
/// reachable with the right content types, the build-output dir actually carries them (the `dotnet run`
/// path that the in-memory smoke test is blind to — see WebServerSmokeTests), and index.html wires the
/// icon links. Mirrors WebServerSmokeTests / the PR-#156 build-output-copy pattern.</summary>
[Trait("Category", "UAT")]
public class FaviconAssetTests : IClassFixture<WebApplicationFactory<WebProgram>>
{
    private readonly WebApplicationFactory<WebProgram> _factory;
    public FaviconAssetTests(WebApplicationFactory<WebProgram> factory) => _factory = factory;

    [Fact]
    public async Task Favicon_svg_serves_200_as_svg()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage res = await client.GetAsync("/favicon.svg");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/svg+xml", res.Content.Headers.ContentType?.MediaType);
        // Sanity: it is the chip art, not an empty/placeholder file.
        string svg = await res.Content.ReadAsStringAsync();
        Assert.Contains("<svg", svg, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Favicon_png_fallback_serves_200_as_png()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage res = await client.GetAsync("/favicon-32.png");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Apple_touch_icon_serves_200_as_png()
    {
        using HttpClient client = _factory.CreateClient();
        using HttpResponseMessage res = await client.GetAsync("/apple-touch-icon.png");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("image/png", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Index_html_wires_the_icon_links()
    {
        using HttpClient client = _factory.CreateClient();
        string html = await client.GetStringAsync("/");

        // The SVG-primary link, the PNG fallback, and the apple-touch-icon must all be present.
        Assert.Contains("rel=\"icon\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("type=\"image/svg+xml\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/favicon.svg", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/favicon-32.png", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("apple-touch-icon", html, StringComparison.OrdinalIgnoreCase);
    }

    // Un-fakeable build-output check, paired with WebServerSmokeTests.Static_client_is_copied_to_the_
    // build_output_dir: under a real `dotnet run` the Web project pins ContentRootPath =
    // AppContext.BaseDirectory, so the favicon assets must physically land in the bin output dir via the
    // csproj's <Content Update="wwwroot\**" CopyToOutputDirectory>. The in-memory factory above uses the
    // project dir as its content root and is therefore blind to a missing build-output copy.
    [Fact]
    public void Favicon_assets_are_copied_to_the_build_output_dir()
    {
        string wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        foreach (string asset in new[] { "favicon.svg", "favicon-32.png", "apple-touch-icon.png" })
        {
            Assert.True(File.Exists(Path.Combine(wwwroot, asset)),
                $"wwwroot/{asset} is missing from the build output ({wwwroot}); under `dotnet run` "
                + "ContentRootPath = AppContext.BaseDirectory would 404 it.");
        }
    }
}
