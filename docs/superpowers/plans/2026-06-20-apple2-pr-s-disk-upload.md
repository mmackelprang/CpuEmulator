# PR-S — disk-upload inbound-binary path Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add the surface's FIRST inbound binary path: a client `<input type=file>` → client validation (ext / 2 MB cap / non-empty) → a binary WS `DK` frame → server re-validation (`.dsk`/`.po` exact length, `.woz` magic) → load into drive N via the shipped PR-Q `InsertDisk`; the UPLOADING → INSERTED / error states drive the panel, and the existing single-text-frame protocol is unaffected.

**Architecture:** A new `FrameCodec.TryDecodeUpload` parses the binary `DK` frame (`'D','K', version, formatByte, then the raw image bytes`) and runs server-side re-validation (`UploadValidator`: `.dsk`/`.po` must be exactly 143,360 bytes; `.woz` must start with the `WOZ1`/`WOZ2` magic — but `.woz` then returns the explicit not-yet-supported reject because no `WozFluxImage` parser ships). `DemoSession.RunAsync`'s receive loop, which today drops every binary frame, gains a binary branch that reassembles the multi-fragment `DK` message (a `.dsk` is 143,360 bytes — far over the 1 KiB receive buffer, so the message arrives across many fragments), validates, and on success calls the hoisted `insertDisk` delegate (reused from PR-R) and pushes an upload-result `ST`-style ack text frame; on failure pushes a calm error. The client gains `uploadDisk(drive, file)` (validate → read bytes → frame → send), an UPLOADING-state hook (`window.uploadState`), and decode of the upload-result ack. The visible drive-panel DOM is row T; S ships the transport + validation + the UPLOADING/INSERTED/error state machine T binds to.

**Tech Stack:** C# 12 / .NET 8 minimal-API WebSockets, `System.Buffers.Binary`, xUnit + `WebApplicationFactory<Program>` + `Microsoft.AspNetCore.TestHost` (binary WS send), vanilla ES5-style `app.js` (`FileReader`/`ArrayBuffer`, `ws.send(Uint8Array)`).

## Global Constraints

- **Branch + PR:** all work on `feat/apple2-disk-upload`; open a PR to `main`; do not commit to `main` directly.
- **DEPENDS ON PR-R:** S reuses the hoisted `Action<int,byte[],DiskFormat,string>? insertDisk` delegate and the four-arg `surface.InsertDisk(drive,bytes,format,label)` introduced in PR-R Task 3, and the drive-2 status fold-in. If S is implemented before R lands, port R's Task 3 (the `DriveLabels` holder + four-arg insert) and the `insertDisk` hoist first. Per the queue, R ships before S.
- **Interpreter-first invariant:** every gate runs on the interpreter tier.
- **`.dsk`/`.po` are end-to-end-loadable; `.woz` validates magic then returns the explicit not-yet-supported reject** (`DiskImageFactory.FromBytes` throws `NotSupportedException` for raw `.woz`; the `WozFluxImage` follow-on is a separate backlog row). The server NEVER calls `InsertDisk` with a `.woz` payload.
- **Validation limits (verbatim from `docs/design-handoffs/apple-2-plus/interactions.md` §4.4 + `DiskImageFactory.DskBytes`):** client ext allow-list `.woz,.dsk,.po`; client size cap **2 MB** outright reject; client non-empty (0 bytes reject); server `.dsk`/`.po` exact length `143360` (`DiskImageFactory.DskBytes`); server `.woz` magic `WOZ1` (`57 4F 5A 31`) or `WOZ2` (`57 4F 5A 32`).
- **Copy strings (verbatim from `copy.md` §7):** wrong type `Unsupported file — use .woz, .dsk, or .po`; too large `File too large — Disk II images are under ~250 KB`; empty `That file is empty`; server corrupt `That image looks corrupt`; `.woz` not-yet-supported reuses `That image looks corrupt` is WRONG — use a dedicated honest string `.woz upload isn't supported yet — use .dsk or .po` (see Task 2 note). UPLOADING label `Uploading <filename>…` (indeterminate acceptable).
- **The single-text-frame protocol is unaffected:** the `ST` text frame, the FB/AU binary OUT path, and the key-event text IN path stay byte-for-byte. The `DK` frame is a NEW inbound binary message; no outbound frame changes.
- **Comment policy / structured style:** match the existing `FrameCodec.cs` / `Program.cs` doc-comment density; no emojis.
- **No new NuGet dependencies.**
- **Ground truth HEAD:** `main` @ `204cf3d` (PRs #99–#120 merged) plus PR-R landed. All literal code below calls the shipped signatures.

---

## File Structure

**New files:**
- `src/CpuEmulator.Surface.Web/UploadValidator.cs` — server-side re-validation of a decoded upload (length / `.woz` magic / `.woz`-unsupported). Returns a discriminated result (`Ok` / a reason string). Pure, headless-testable.
- `tests/CpuEmulator.Tests/Surface/UploadValidatorTests.cs` — validator unit tests.
- `tests/CpuEmulator.Tests/Surface/UploadDecodeTests.cs` — `FrameCodec.TryDecodeUpload` unit tests.
- `tests/CpuEmulator.Tests/Surface/DiskUploadEndpointTests.cs` — the un-fakeable gate: a binary `DK` frame with a valid `.dsk` payload inserts into drive N; a bad length/magic is rejected; the single-text-frame protocol is unaffected.

**Modified files:**
- `src/CpuEmulator.Surface.Web/FrameCodec.cs` — add `UploadFrame` struct + `TryDecodeUpload` (+ an `EncodeUploadAck` text helper for the result).
- `src/CpuEmulator.Surface.Web/Program.cs` — reassemble + dispatch the binary `DK` frame in `ReceiveKeysAsync` (the binary branch that today `continue`s); push the upload-result ack.
- `src/CpuEmulator.Surface.Web/wwwroot/app.js` — `uploadDisk(drive, file)` (client validation + binary send), the UPLOADING-state hook, and the ack decode.

---

## Task 1: `FrameCodec.TryDecodeUpload` — decode the binary `DK` frame

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/FrameCodec.cs` (add after `TryDecodeDisk` from PR-R)
- Test: `tests/CpuEmulator.Tests/Surface/UploadDecodeTests.cs`

**Interfaces:**
- Consumes: `System.Buffers.Binary` (already imported in `FrameCodec.cs`).
- Produces:
  - `public readonly record struct UploadFrame(int Drive, DiskFormat Format, byte[] Bytes)` — the decoded image.
  - `public static bool FrameCodec.TryDecodeUpload(ReadOnlySpan<byte> frame, out UploadFrame upload)` — parses `['D','K', version(0x01), driveByte(1|2), formatByte(0=woz,1=dsk,2=po), ...imageBytes]`; returns false on a bad tag/version/drive/format or a too-short header. Header is 5 bytes.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/CpuEmulator.Tests/Surface/UploadDecodeTests.cs
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class UploadDecodeTests
{
    private static byte[] FrameOf(byte drive, byte format, byte[] body)
    {
        var f = new byte[5 + body.Length];
        f[0] = (byte)'D'; f[1] = (byte)'K'; f[2] = 0x01; f[3] = drive; f[4] = format;
        body.CopyTo(f.AsSpan(5));
        return f;
    }

    [Fact]
    public void Decodes_a_dsk_upload_into_drive_format_and_bytes()
    {
        byte[] body = { 1, 2, 3, 4 };
        Assert.True(FrameCodec.TryDecodeUpload(FrameOf(2, 1, body), out UploadFrame u));
        Assert.Equal(2, u.Drive);
        Assert.Equal(DiskFormat.Dsk, u.Format);
        Assert.Equal(body, u.Bytes);
    }

    [Fact]
    public void Decodes_po_and_woz_format_bytes()
    {
        Assert.True(FrameCodec.TryDecodeUpload(FrameOf(1, 2, new byte[] { 9 }), out UploadFrame po));
        Assert.Equal(DiskFormat.Po, po.Format);
        Assert.True(FrameCodec.TryDecodeUpload(FrameOf(1, 0, new byte[] { 9 }), out UploadFrame woz));
        Assert.Equal(DiskFormat.Woz, woz.Format);
    }

    [Fact]
    public void Rejects_a_non_DK_tag()
    {
        Assert.False(FrameCodec.TryDecodeUpload(new byte[] { (byte)'F', (byte)'B', 0x01, 1, 1, 0 }, out _));
    }

    [Fact]
    public void Rejects_a_bad_drive_or_format_or_short_frame()
    {
        Assert.False(FrameCodec.TryDecodeUpload(FrameOf(3, 1, new byte[] { 1 }), out _));   // drive 3
        Assert.False(FrameCodec.TryDecodeUpload(FrameOf(1, 9, new byte[] { 1 }), out _));   // format 9
        Assert.False(FrameCodec.TryDecodeUpload(new byte[] { (byte)'D', (byte)'K' }, out _)); // too short
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~UploadDecodeTests"`
Expected: FAIL — `TryDecodeUpload` / `UploadFrame` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/CpuEmulator.Surface.Web/FrameCodec.cs — add this struct just above the FrameCodec class, in the namespace:
/// <summary>A decoded disk-UPLOAD binary frame from the client (design D12 / T-B — the surface's first
/// inbound binary path). Wire: ['D','K', version(0x01), driveByte(1|2), formatByte(0=woz,1=dsk,2=po),
/// ...imageBytes]. The bytes are the raw disk image; the server re-validates (see UploadValidator) before
/// loading them into the running Disk II.</summary>
public readonly record struct UploadFrame(int Drive, DiskFormat Format, byte[] Bytes);
```

```csharp
// src/CpuEmulator.Surface.Web/FrameCodec.cs — add inside the FrameCodec class, after TryDecodeDisk:
    private const int UploadHeaderBytes = 5;   // 'D','K', version, drive, format

    /// <summary>Decode the binary <c>DK</c> upload frame (design D12). Returns false on a bad tag/version,
    /// an out-of-range drive (1..2) or format (0..2), or a frame shorter than the 5-byte header. The image
    /// bytes (everything after the header) are copied into <see cref="UploadFrame.Bytes"/>; an empty body
    /// is allowed here (the validator rejects 0 bytes — keep decode and validation separate).</summary>
    public static bool TryDecodeUpload(ReadOnlySpan<byte> frame, out UploadFrame upload)
    {
        upload = default;
        if (frame.Length < UploadHeaderBytes)
            return false;
        if (frame[0] != (byte)'D' || frame[1] != (byte)'K' || frame[2] != 0x01)
            return false;
        int drive = frame[3];
        if (drive is < 1 or > 2)
            return false;
        DiskFormat format = frame[4] switch
        {
            0 => DiskFormat.Woz,
            1 => DiskFormat.Dsk,
            2 => DiskFormat.Po,
            _ => (DiskFormat)(-1),
        };
        if (format == (DiskFormat)(-1))
            return false;
        byte[] bytes = frame[UploadHeaderBytes..].ToArray();
        upload = new UploadFrame(drive, format, bytes);
        return true;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~UploadDecodeTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/FrameCodec.cs tests/CpuEmulator.Tests/Surface/UploadDecodeTests.cs
git commit -m "feat(apple2): FrameCodec.TryDecodeUpload for the binary DK upload frame (PR-S task 1)"
```

---

## Task 2: `UploadValidator` — server-side re-validation

**Files:**
- Create: `src/CpuEmulator.Surface.Web/UploadValidator.cs`
- Test: `tests/CpuEmulator.Tests/Surface/UploadValidatorTests.cs`

**Interfaces:**
- Consumes: `DiskImageFactory.DskBytes` (the shipped `143360` const); the decoded `UploadFrame` (Task 1).
- Produces:
  - `public readonly record struct UploadResult(bool Ok, string Message)` — `Ok` true with `Message=""` on success, else the calm error copy.
  - `public static UploadResult UploadValidator.Validate(UploadFrame upload)` — `.dsk`/`.po` must be exactly `DiskImageFactory.DskBytes`; `.woz` must start with the `WOZ1`/`WOZ2` magic AND then returns the not-yet-supported message (the honest reject — no `WozFluxImage`); empty body rejected.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/CpuEmulator.Tests/Surface/UploadValidatorTests.cs
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class UploadValidatorTests
{
    private static byte[] DskBytes() => new byte[DiskImageFactory.DskBytes];     // exactly 143,360

    [Fact]
    public void A_correct_length_dsk_validates()
    {
        UploadResult r = UploadValidator.Validate(new UploadFrame(1, DiskFormat.Dsk, DskBytes()));
        Assert.True(r.Ok);
        Assert.Equal("", r.Message);
    }

    [Fact]
    public void A_correct_length_po_validates()
    {
        Assert.True(UploadValidator.Validate(new UploadFrame(1, DiskFormat.Po, DskBytes())).Ok);
    }

    [Fact]
    public void A_wrong_length_dsk_is_rejected_as_corrupt()
    {
        UploadResult r = UploadValidator.Validate(new UploadFrame(1, DiskFormat.Dsk, new byte[100]));
        Assert.False(r.Ok);
        Assert.Equal("That image looks corrupt", r.Message);
    }

    [Fact]
    public void An_empty_body_is_rejected()
    {
        UploadResult r = UploadValidator.Validate(new UploadFrame(1, DiskFormat.Dsk, Array.Empty<byte>()));
        Assert.False(r.Ok);
        Assert.Equal("That file is empty", r.Message);
    }

    [Fact]
    public void A_woz_with_bad_magic_is_corrupt()
    {
        UploadResult r = UploadValidator.Validate(new UploadFrame(1, DiskFormat.Woz, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }));
        Assert.False(r.Ok);
        Assert.Equal("That image looks corrupt", r.Message);
    }

    [Fact]
    public void A_woz_with_good_magic_is_rejected_as_not_yet_supported()
    {
        // WOZ2 magic + the 0xFF byte, padded to a plausible header length.
        var woz = new byte[16];
        woz[0] = 0x57; woz[1] = 0x4F; woz[2] = 0x5A; woz[3] = 0x32; woz[4] = 0xFF;   // "WOZ2"+FF
        UploadResult r = UploadValidator.Validate(new UploadFrame(1, DiskFormat.Woz, woz));
        Assert.False(r.Ok);
        Assert.Equal(".woz upload isn't supported yet — use .dsk or .po", r.Message);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~UploadValidatorTests"`
Expected: FAIL — `UploadValidator` / `UploadResult` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/CpuEmulator.Surface.Web/UploadValidator.cs
namespace CpuEmulator.Surface.Web;

/// <summary>The outcome of server-side upload re-validation (design D12): Ok=true with an empty Message on
/// success, else the calm inline-error copy (copy.md §7). Never throws on a bad image — a malformed upload
/// is a normal user condition, not a server error.</summary>
public readonly record struct UploadResult(bool Ok, string Message);

/// <summary>Server-side re-validation of a decoded <see cref="UploadFrame"/> (design D12 / T-B). Never
/// trust the client: re-check length (.dsk/.po exact <see cref="DiskImageFactory.DskBytes"/>) and the .woz
/// magic. The .woz path validates its magic but then returns the honest "not yet supported" reject — no
/// WozFluxImage parser ships (the runtime DiskImageFactory.FromBytes throws NotSupportedException for raw
/// .woz bytes). The end-to-end-loadable formats are .dsk and .po.</summary>
public static class UploadValidator
{
    // The .woz file magic (research / WOZ spec): "WOZ1" or "WOZ2" then a 0xFF byte. We check the 4-byte
    // ASCII magic only — a strong-enough sniff to distinguish a real .woz from a corrupt/mistyped file.
    private static bool HasWozMagic(byte[] b) =>
        b.Length >= 4 && b[0] == 0x57 && b[1] == 0x4F && b[2] == 0x5A && (b[3] == 0x31 || b[3] == 0x32);

    public static UploadResult Validate(UploadFrame upload)
    {
        byte[] bytes = upload.Bytes;
        if (bytes.Length == 0)
            return new UploadResult(false, "That file is empty");

        switch (upload.Format)
        {
            case DiskFormat.Dsk:
            case DiskFormat.Po:
                return bytes.Length == DiskImageFactory.DskBytes
                    ? new UploadResult(true, "")
                    : new UploadResult(false, "That image looks corrupt");
            case DiskFormat.Woz:
                if (!HasWozMagic(bytes))
                    return new UploadResult(false, "That image looks corrupt");
                // Magic is good, but no parser ships yet — the honest reject (the WozFluxImage follow-on).
                return new UploadResult(false, ".woz upload isn't supported yet — use .dsk or .po");
            default:
                return new UploadResult(false, "That image looks corrupt");
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~UploadValidatorTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/UploadValidator.cs tests/CpuEmulator.Tests/Surface/UploadValidatorTests.cs
git commit -m "feat(apple2): UploadValidator server-side re-validation (PR-S task 2)"
```

---

## Task 3: `EncodeUploadAck` — the upload-result text frame

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/FrameCodec.cs` (add after `EncodeStatus`)
- Test: extend `tests/CpuEmulator.Tests/Surface/UploadDecodeTests.cs` (or a new `UploadAckTests.cs`)

**Interfaces:**
- Consumes: `UploadResult` (Task 2); `System.Text` / `System.Text.Json` (imported).
- Produces: `public static byte[] FrameCodec.EncodeUploadAck(int drive, UploadResult result)` — a text frame `"ST " + {"upload":{"drive":N,"ok":bool,"message":"..."}}`. Reusing the `ST ` prefix keeps the client's text-routing contract (every text frame → `handleStatusText`); the client distinguishes it by the `upload` key. This is the only host→client signal the panel needs for the INSERTED/error transition; the live drive label + motor still flow through the normal `ST` snapshot frame (PR-P/R).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/CpuEmulator.Tests/Surface/UploadAckTests.cs
using System.Text;
using System.Text.Json;
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class UploadAckTests
{
    [Fact]
    public void Ack_is_an_ST_prefixed_json_carrying_drive_ok_and_message()
    {
        byte[] frame = FrameCodec.EncodeUploadAck(2, new UploadResult(false, "That image looks corrupt"));
        string text = Encoding.UTF8.GetString(frame);
        Assert.StartsWith("ST ", text);

        using JsonDocument doc = JsonDocument.Parse(text["ST ".Length..]);
        JsonElement up = doc.RootElement.GetProperty("upload");
        Assert.Equal(2, up.GetProperty("drive").GetInt32());
        Assert.False(up.GetProperty("ok").GetBoolean());
        Assert.Equal("That image looks corrupt", up.GetProperty("message").GetString());
    }

    [Fact]
    public void A_success_ack_has_ok_true_and_empty_message()
    {
        byte[] frame = FrameCodec.EncodeUploadAck(1, new UploadResult(true, ""));
        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(frame)["ST ".Length..]);
        JsonElement up = doc.RootElement.GetProperty("upload");
        Assert.True(up.GetProperty("ok").GetBoolean());
        Assert.Equal("", up.GetProperty("message").GetString());
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~UploadAckTests"`
Expected: FAIL — `EncodeUploadAck` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/CpuEmulator.Surface.Web/FrameCodec.cs — add inside the FrameCodec class, after EncodeStatus:
    /// <summary>Encode the upload-result ack as an <c>ST</c> text frame (design D12 UPLOADING -> INSERTED /
    /// error). The wire reuses the "ST " prefix (the client routes all text to handleStatusText); the
    /// distinguishing <c>upload</c> key tells the client this is an upload result, not a status snapshot.
    /// On ok=true the panel goes INSERTED (the live label arrives via the normal ST snapshot); on ok=false
    /// the panel shows <c>message</c> inline + reverts.</summary>
    public static byte[] EncodeUploadAck(int drive, UploadResult result)
    {
        var body = new { upload = new { drive, ok = result.Ok, message = result.Message } };
        string json = JsonSerializer.Serialize(body);
        return Encoding.UTF8.GetBytes("ST " + json);
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~UploadAckTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/FrameCodec.cs tests/CpuEmulator.Tests/Surface/UploadAckTests.cs
git commit -m "feat(apple2): EncodeUploadAck upload-result text frame (PR-S task 3)"
```

---

## Task 4: Server — reassemble + dispatch the binary `DK` frame (the un-fakeable gate)

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/Program.cs`
- Test: `tests/CpuEmulator.Tests/Surface/DiskUploadEndpointTests.cs`

**Interfaces:**
- Consumes: `FrameCodec.TryDecodeUpload` + `UploadFrame` (Task 1); `UploadValidator.Validate` + `UploadResult` (Task 2); `FrameCodec.EncodeUploadAck` (Task 3); the hoisted `insertDisk` delegate + four-arg `surface.InsertDisk` (from PR-R Task 3/4); the status-frame channel `statusFrames` (for the ack); the shipped `WebApplicationFactory<Program>` + `CreateWebSocketClient` harness.
- Produces: a binary branch in `ReceiveKeysAsync` that reassembles a multi-fragment `DK` message, validates it, and on success calls `insertDisk(drive, bytes, format, label)` + sends an ok ack; on failure sends an error ack. The text key/disk path is unchanged.

- [ ] **Step 1: Write the failing test (the gate)**

```csharp
// tests/CpuEmulator.Tests/Surface/DiskUploadEndpointTests.cs
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using WebProgram = CpuEmulator.Surface.Web.Program;

namespace CpuEmulator.Tests.Surface;

/// <summary>The PR-S gate: a binary DK frame with a valid .dsk payload inserts into drive N; a bad
/// length is rejected (an error ack) — and the single-text-frame (key) protocol is unaffected.</summary>
[Trait("Category", "UAT")]
public class DiskUploadEndpointTests : IClassFixture<WebApplicationFactory<WebProgram>>
{
    private readonly WebApplicationFactory<WebProgram> _factory;
    public DiskUploadEndpointTests(WebApplicationFactory<WebProgram> factory) => _factory = factory;

    // Seed an Apple system ROM so the Apple branch (which wires insertDisk) boots, not the demo.
    private static string SeedRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cpuemu-upgate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "apple2"));
        var sys = new byte[0x3000];
        sys[0x2FFC] = 0x62; sys[0x2FFD] = 0xFA;
        File.WriteAllBytes(Path.Combine(root, "apple2", "apple2plus.rom"), sys);
        return root;
    }

    private WebApplicationFactory<WebProgram> FactoryWithRoot(string root) =>
        _factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, __) =>
                Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", root)));

    private static byte[] DkFrame(byte drive, byte format, byte[] body)
    {
        var f = new byte[5 + body.Length];
        f[0] = (byte)'D'; f[1] = (byte)'K'; f[2] = 0x01; f[3] = drive; f[4] = format;
        body.CopyTo(f.AsSpan(5));
        return f;
    }

    private static async Task<JsonElement?> ReadUploadAckAsync(WebSocket ws)
    {
        var buffer = new byte[8 + 560 * 216 * 4];
        for (int i = 0; i < 400; i++)
        {
            WebSocketReceiveResult r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType != WebSocketMessageType.Text) continue;
            string s = Encoding.UTF8.GetString(buffer, 0, r.Count);
            if (!s.StartsWith("ST ")) continue;
            using JsonDocument doc = JsonDocument.Parse(s["ST ".Length..]);
            if (doc.RootElement.TryGetProperty("upload", out JsonElement up))
                return up.Clone();
        }
        return null;
    }

    [Fact]
    public async Task A_valid_dsk_DK_frame_inserts_into_drive_N()
    {
        string root = SeedRoot();
        try
        {
            WebApplicationFactory<WebProgram> factory = FactoryWithRoot(root);
            WebSocketClient wsClient = factory.Server.CreateWebSocketClient();
            var wsUri = new UriBuilder(factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
            using WebSocket ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

            byte[] dsk = new byte[35 * 16 * 256];                 // exactly 143,360
            await ws.SendAsync(DkFrame(1, 1, dsk), WebSocketMessageType.Binary, true, CancellationToken.None);

            JsonElement? ack = await ReadUploadAckAsync(ws);
            Assert.NotNull(ack);
            Assert.Equal(1, ack!.Value.GetProperty("drive").GetInt32());
            Assert.True(ack!.Value.GetProperty("ok").GetBoolean());
        }
        finally { Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", null); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_wrong_length_dsk_DK_frame_is_rejected()
    {
        string root = SeedRoot();
        try
        {
            WebApplicationFactory<WebProgram> factory = FactoryWithRoot(root);
            WebSocketClient wsClient = factory.Server.CreateWebSocketClient();
            var wsUri = new UriBuilder(factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
            using WebSocket ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

            await ws.SendAsync(DkFrame(1, 1, new byte[100]), WebSocketMessageType.Binary, true, CancellationToken.None);

            JsonElement? ack = await ReadUploadAckAsync(ws);
            Assert.NotNull(ack);
            Assert.False(ack!.Value.GetProperty("ok").GetBoolean());
            Assert.Equal("That image looks corrupt", ack!.Value.GetProperty("message").GetString());
        }
        finally { Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", null); Directory.Delete(root, true); }
    }

    [Fact]
    public async Task A_key_event_still_works_after_the_binary_path_is_added()
    {
        // The single-text-frame (key) protocol is unaffected: send a key, confirm the session keeps
        // streaming FB frames (the binary branch did not break the text path).
        string root = SeedRoot();
        try
        {
            WebApplicationFactory<WebProgram> factory = FactoryWithRoot(root);
            WebSocketClient wsClient = factory.Server.CreateWebSocketClient();
            var wsUri = new UriBuilder(factory.Server.BaseAddress) { Scheme = "ws", Path = "/ws" }.Uri;
            using WebSocket ws = await wsClient.ConnectAsync(wsUri, CancellationToken.None);

            string key = "{\"action\":\"down\",\"code\":\"KeyA\",\"char\":\"A\"}";
            await ws.SendAsync(Encoding.UTF8.GetBytes(key), WebSocketMessageType.Text, true, CancellationToken.None);

            var buffer = new byte[8 + 560 * 216 * 4];
            bool sawFb = false;
            for (int i = 0; i < 200 && !sawFb; i++)
            {
                WebSocketReceiveResult r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Binary && buffer[0] == (byte)'F' && buffer[1] == (byte)'B')
                    sawFb = true;
            }
            Assert.True(sawFb, "the key/text path must stay healthy after the DK binary branch lands");
        }
        finally { Environment.SetEnvironmentVariable("CPUEMULATOR_TESTVECTORS", null); Directory.Delete(root, true); }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DiskUploadEndpointTests"`
Expected: FAIL — the binary `DK` frame is dropped (no ack ever arrives) so `ReadUploadAckAsync` returns null in the first two tests. (The third may pass already — the text path is intact — which is fine.)

- [ ] **Step 3: Add the upload-ack sink + thread it into `ReceiveKeysAsync`**

In `DemoSession.RunAsync`, the `statusFrames` channel already carries text frames to the client via `SendTextAsync` (line 151). Reuse it for the ack. After the `recv` task line, the upload handler needs a way to push acks; pass an `Action<byte[]>` that writes to `statusFrames`. Change the `recv` wiring (the line `Task recv = ReceiveKeysAsync(socket, pump, insertDisk, ejectDisk, ct);` introduced in PR-R) to:

```csharp
        Action<byte[]> pushText = f => statusFrames.Writer.TryWrite(f);
        Task recv = ReceiveKeysAsync(socket, pump, insertDisk, ejectDisk, pushText, ct);
```

- [ ] **Step 4: Replace `ReceiveKeysAsync` with the binary-reassembling version**

Replace the PR-R `ReceiveKeysAsync` (signature + body) with this version, which adds the binary `DK` branch. The receive buffer is 1 KiB but a `.dsk` is 143,360 bytes, so a binary message arrives across many fragments — accumulate into a `MemoryStream` until `result.EndOfMessage`, cap the accumulation at 2 MiB (the design's client cap, re-enforced server-side), then decode + validate + dispatch:

```csharp
    private static async Task ReceiveKeysAsync(WebSocket socket, ISurfacePump pump,
                                              Action<int, byte[], DiskFormat, string>? insertDisk,
                                              Action<int>? ejectDisk,
                                              Action<byte[]> pushText,
                                              CancellationToken ct)
    {
        const int MaxUploadBytes = 2 * 1024 * 1024;   // the design's 2 MB cap, re-enforced server-side
        var buffer = new byte[8192];
        var binaryAccumulator = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                break;
            }

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Accumulate fragments until the whole DK message is in (a .dsk is 143,360 bytes — many
                // 8 KiB fragments). Cap the accumulation to refuse an oversized upload without OOM.
                if (binaryAccumulator.Length + result.Count > MaxUploadBytes)
                {
                    binaryAccumulator.SetLength(0);
                    if (result.EndOfMessage)
                        pushText(FrameCodec.EncodeUploadAck(0,
                            new UploadResult(false, "File too large — Disk II images are under ~250 KB")));
                    continue;
                }
                binaryAccumulator.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                    continue;

                byte[] frame = binaryAccumulator.ToArray();
                binaryAccumulator.SetLength(0);
                DispatchUpload(frame, insertDisk, pushText);
                continue;
            }

            if (result.MessageType != WebSocketMessageType.Text)
                continue;
            string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            // Disk-library commands first (their JSON shape is disjoint from a key event); then keys.
            if (FrameCodec.TryDecodeDisk(json, out DiskCommand cmd))
            {
                if (cmd.Eject)
                    ejectDisk?.Invoke(cmd.Drive);
                else if (insertDisk is not null
                         && CpuEmulator.Machines.DiskCatalog.TryResolve(cmd.Id, out string path, out string fmt))
                {
                    DiskFormat format = fmt switch
                    {
                        "po" => DiskFormat.Po,
                        "woz" => DiskFormat.Woz,
                        _ => DiskFormat.Dsk,
                    };
                    if (format != DiskFormat.Woz)
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        insertDisk(cmd.Drive, bytes, format, Path.GetFileNameWithoutExtension(path));
                    }
                }
            }
            else if (FrameCodec.TryDecodeKey(json, out KeyEvent e))
            {
                pump.PostKey(e);
            }
        }

        binaryAccumulator.Dispose();
    }

    /// <summary>Decode + validate a reassembled DK upload frame and, on success, load it into the running
    /// drive (the shipped PR-Q insert via the hoisted delegate). Always pushes an upload-result ack so the
    /// client's UPLOADING state resolves to INSERTED or the inline error.</summary>
    private static void DispatchUpload(byte[] frame, Action<int, byte[], DiskFormat, string>? insertDisk,
                                       Action<byte[]> pushText)
    {
        if (!FrameCodec.TryDecodeUpload(frame, out UploadFrame upload))
        {
            pushText(FrameCodec.EncodeUploadAck(0, new UploadResult(false, "That image looks corrupt")));
            return;
        }

        UploadResult result = UploadValidator.Validate(upload);
        if (!result.Ok || insertDisk is null)
        {
            pushText(FrameCodec.EncodeUploadAck(upload.Drive, result.Ok
                ? new UploadResult(false, "That image looks corrupt")   // no surface to insert into
                : result));
            return;
        }

        // .woz never reaches here (the validator rejects it as not-yet-supported); .dsk/.po load via Q.
        try
        {
            insertDisk(upload.Drive, upload.Bytes, upload.Format, $"upload ({upload.Format})".ToLowerInvariant());
            pushText(FrameCodec.EncodeUploadAck(upload.Drive, new UploadResult(true, "")));
        }
        catch (Exception)
        {
            // DiskImageFactory/DiskImage throws on a malformed image the length-check let through.
            pushText(FrameCodec.EncodeUploadAck(upload.Drive, new UploadResult(false, "That image looks corrupt")));
        }
    }
```

Note: the larger 8 KiB buffer (was 1 KiB) keeps the key/text path identical (a key JSON is well under 8 KiB) while reducing the fragment count for a 140 KiB upload. The `binaryAccumulator` is disposed on loop exit.

- [ ] **Step 5: Run the gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DiskUploadEndpointTests"`
Expected: PASS (3 tests).

- [ ] **Step 6: Run the smoke + library tests to confirm no regression**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~WebServerSmokeTests|FullyQualifiedName~DiskLibraryEndpointTests"`
Expected: PASS — the FB stream, the `ST` text frame, and the PR-R library path are all unaffected.

- [ ] **Step 7: Commit**

```bash
git add src/CpuEmulator.Surface.Web/Program.cs tests/CpuEmulator.Tests/Surface/DiskUploadEndpointTests.cs
git commit -m "feat(apple2): binary DK upload reassembly + validate + insert (PR-S task 4, the gate)"
```

---

## Task 5: Client — `uploadDisk(drive, file)` + UPLOADING state + ack decode

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/wwwroot/app.js`

**Interfaces:**
- Consumes: the binary `DK` frame contract (Task 1); the upload-result ack (`{"upload":{drive,ok,message}}`, Task 3); the shipped `ws` socket + `handleStatusText`; the design's client validation limits.
- Produces: `window.uploadDisk(drive, file)` (validate → read → binary send), `window.uploadState` (`{1:state,2:state}` for row T's panel: `"idle"|"uploading"|"error"` + `lastError`), and an extension to `handleStatusText` that routes the `upload` ack. No visible DOM — the per-drive `[ Insert… ]` button + the `<input type=file>` are row T; S ships the function + state machine T's button calls.

- [ ] **Step 1: Add `window.uploadState` + the ack route to `handleStatusText`**

In `app.js`, inside `handleStatusText`, the structured-body branch currently handles only the status snapshot. Add an `upload`-key check at the TOP of the `if (body.startsWith("{"))` block (before the `window.machineStatus` assignment), so an upload ack is routed distinctly:

Replace the shipped block (lines 68–78):

```javascript
    if (body.startsWith("{")) {
      let st;
      try { st = JSON.parse(body); } catch { return; }
      window.machineStatus = st;                 // row T binds drive panels to this
      applyAssetBanner(st.asset, banner);
      // The status line: board · mode · the active drive summary (read-only reflection).
      const active = (st.drives || []).find(d => d.motor);
      const driveText = active ? " · drive ●" : "";
      status.textContent = "connected · " + st.board + " · " + st.mode + driveText;
      return;
    }
```

with:

```javascript
    if (body.startsWith("{")) {
      let st;
      try { st = JSON.parse(body); } catch { return; }
      // An upload-result ack (PR-S, design D12): resolve the panel's UPLOADING state to INSERTED or error.
      if (st.upload) {
        const u = st.upload;
        window.uploadState[u.drive] = u.ok ? "idle" : "error";
        window.uploadLastError[u.drive] = u.ok ? "" : (u.message || "That image looks corrupt");
        if (window.onUploadResult) window.onUploadResult(u.drive, u.ok, u.message || "");
        return;
      }
      window.machineStatus = st;                 // row T binds drive panels to this
      applyAssetBanner(st.asset, banner);
      // The status line: board · mode · the active drive summary (read-only reflection).
      const active = (st.drives || []).find(d => d.motor);
      const driveText = active ? " · drive ●" : "";
      status.textContent = "connected · " + st.board + " · " + st.mode + driveText;
      return;
    }
```

- [ ] **Step 2: Add the upload transport + client validation**

Just before the final `})();`, add (in PR-R order this sits beside the library transport; if R's block is present, append after it):

```javascript
  // --- Disk upload (PR-S, design D12 — the surface's first inbound binary path) ---
  // Per-drive UPLOADING state for row T's panel: "idle" | "uploading" | "error", + the last error message.
  window.uploadState = { 1: "idle", 2: "idle" };
  window.uploadLastError = { 1: "", 2: "" };

  // The 2 MB client cap + the extension allow-list (design §4.4). .dsk/.po load end-to-end; .woz is
  // validated client-side but the server returns the not-yet-supported reject (no WozFluxImage yet).
  const UPLOAD_MAX_BYTES = 2 * 1024 * 1024;
  const FORMAT_BYTE = { woz: 0, dsk: 1, po: 2 };

  // Validate a File, then send it as a binary DK frame on the open socket. Row T's [ Insert… ] picker
  // onchange calls this with the chosen File. Returns the client-side error string, or "" if the upload
  // was sent (the server's ack resolves INSERTED / a server-side error).
  window.uploadDisk = function (drive, file) {
    const name = (file && file.name) || "";
    const ext = name.slice(name.lastIndexOf(".")).toLowerCase();   // ".dsk" / ".po" / ".woz"
    const format = { ".woz": "woz", ".dsk": "dsk", ".po": "po" }[ext];
    if (!format) {
      window.uploadLastError[drive] = "Unsupported file — use .woz, .dsk, or .po";
      window.uploadState[drive] = "error";
      return window.uploadLastError[drive];
    }
    if (file.size === 0) {
      window.uploadLastError[drive] = "That file is empty";
      window.uploadState[drive] = "error";
      return window.uploadLastError[drive];
    }
    if (file.size > UPLOAD_MAX_BYTES) {
      window.uploadLastError[drive] = "File too large — Disk II images are under ~250 KB";
      window.uploadState[drive] = "error";
      return window.uploadLastError[drive];
    }
    if (ws.readyState !== WebSocket.OPEN) return "disconnected";

    window.uploadState[drive] = "uploading";
    window.uploadLastError[drive] = "";
    const reader = new FileReader();
    reader.onload = function () {
      const body = new Uint8Array(reader.result);
      const frame = new Uint8Array(5 + body.length);
      frame[0] = 0x44; frame[1] = 0x4B;        // 'D','K'
      frame[2] = 0x01;                          // version
      frame[3] = drive;                         // 1 | 2
      frame[4] = FORMAT_BYTE[format];           // 0=woz 1=dsk 2=po
      frame.set(body, 5);
      ws.send(frame);                           // binary send (ws.binaryType is "arraybuffer")
    };
    reader.readAsArrayBuffer(file);
    return "";
  };
```

- [ ] **Step 3: Verify the client still serves (smoke through the server)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~WebServerSmokeTests"`
Expected: PASS — `/` still serves `index.html` referencing the modified `app.js`.

- [ ] **Step 4: Syntax-check the JS**

Run: `node --check src/CpuEmulator.Surface.Web/wwwroot/app.js` if Node is available; else confirm in the browser during UAT that `window.uploadDisk` is a function and no console error appears. Expected: no syntax error.

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/wwwroot/app.js
git commit -m "feat(apple2): client uploadDisk + UPLOADING state + ack decode (PR-S task 5)"
```

---

## Task 6: Full-suite green + warning-clean + the row's gate

**Files:** none (verification task).

- [ ] **Step 1: Build warning-clean**

Run: `dotnet build CpuEmulator.sln -warnaserror`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 2: Run the full unit suite**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`
Expected: all green (the PR-R baseline + the new PR-S tests), 0 failed, skips unchanged.

- [ ] **Step 3: Confirm the row's un-fakeable gate**

The gate (`docs/BUILDER_QUEUE.md` row S): *a binary `DK` frame with a valid `.dsk` payload inserts into drive N (server-side validation rejects a bad length/magic); the single-text-frame protocol is unaffected.* This is `DiskUploadEndpointTests.A_valid_dsk_DK_frame_inserts_into_drive_N` + `A_wrong_length_dsk_DK_frame_is_rejected` + `A_key_event_still_works_after_the_binary_path_is_added`.

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DiskUploadEndpointTests"`
Expected: PASS (3 tests).

---

## Self-Review (completed by the planner)

**Spec coverage (design D12 / T-B + the queue gate):**
- Client `<input type=file>` → client validation (ext / 2 MB cap / non-empty) → Task 5 (`uploadDisk` validates ext/size/empty before sending; the visible `<input>` + `[ Insert… ]` is row T, which calls `window.uploadDisk`). ✅
- A binary WS `DK` frame → Task 1 (decode) + Task 5 (client binary send). ✅
- Server re-validation (`.dsk`/`.po` exact length; `.woz` magic) → Task 2 (`UploadValidator`). ✅
- Load into drive N via Q's `InsertDisk` → Task 4 (`DispatchUpload` → hoisted `insertDisk`). ✅
- UPLOADING → INSERTED / error states drive the panel → Task 3 (ack frame) + Task 5 (`window.uploadState` + ack decode); the panel DOM is row T binding to this state machine. ✅
- Gate: a binary `DK` frame with a valid `.dsk` inserts into drive N; bad length/magic rejected; single-text-frame protocol unaffected → Task 4 (all three gate tests). ✅
- `.woz` validates magic then returns the explicit not-yet-supported message → Task 2 (`A_woz_with_good_magic_is_rejected_as_not_yet_supported`); never inserted (validator rejects before `DispatchUpload` calls `insertDisk`). ✅

**Placeholder scan:** every step carries literal code/commands; no `TBD`/`implement later`/`similar to Task N`. The one copy-string correction (the `.woz`-unsupported message, NOT `That image looks corrupt`) is called out explicitly in Global Constraints and Task 2. ✅

**Type consistency:** `UploadFrame(Drive,Format,Bytes)`, `UploadResult(Ok,Message)`, `TryDecodeUpload`, `UploadValidator.Validate`, `EncodeUploadAck(drive,result)`, `DispatchUpload`, `window.uploadDisk`/`uploadState`/`uploadLastError` are used identically across tasks. The four-arg `insertDisk` delegate matches PR-R's `Action<int,byte[],DiskFormat,string>`. ✅

**Multi-fragment correctness:** a 143,360-byte `.dsk` exceeds the receive buffer (now 8 KiB), so Task 4 reassembles fragments until `EndOfMessage` with a 2 MiB cap — the load-bearing detail an implementer would otherwise miss (the shipped 1 KiB buffer + single-read assumption would truncate every real upload). ✅

**Drift flagged:** the `.woz` upload path is intentionally end-to-end-incomplete (validates magic, then the honest reject) pending the `WozFluxImage` backlog row — consistent with the shipped `DiskImageFactory.FromBytes` `NotSupportedException`.

---

## Execution Handoff

Plan complete and saved. Builder picks this up after PR-R lands and the row is on `main`. Recommended execution: subagent-driven-development (fresh subagent per task, two-stage review between tasks).
