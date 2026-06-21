# PR-R — `GET /disks` catalog endpoint + per-drive library dropdown Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a `GET /disks` JSON catalog endpoint that lists the cached `disks/` images (name, format, drive-compat, CP/M grouping) and a text-WS `disk-insert` / `disk-eject` path so a library selection inserts the cached bytes into a running drive via the shipped PR-Q `InsertDisk`; fold in drive-2 status so the `ST` frame reports BOTH drives.

**Architecture:** A new `DiskCatalog` static (in `CpuEmulator.Machines`, beside `Apple2Rom`/`SoftCardCpm`) enumerates `<cache>/disks/*.dsk|*.po|*.woz` plus the already-cached CP/M `.dsk`, returns a deterministic, sorted list of entries (id, display name, format, CP/M flag, supported flag — `.woz` is listed-but-unsupported until `WozFluxImage` lands). `Program.cs` maps `GET /disks` to serialize that catalog and threads two new hoisted delegates (`insertDisk` / `ejectDisk`) from the chosen Apple surface into `ReceiveKeysAsync`, which now also dispatches a `disk-insert` / `disk-eject` JSON text message (decoded by a new `FrameCodec.TryDecodeDisk`). A library insert reads the cached bytes server-side and calls the shipped `surface.InsertDisk(drive, bytes, format)`. The client gains a read-only `loadCatalog()` + `insertFromLibrary()` / `ejectDrive()` transport pair (the visible per-drive `[ Library ▾]` panel DOM is row T; R ships the data + senders T binds to). Drive-2 status is folded in via a tiny mutable `DriveLabels` holder on each surface so `Status()` grows a second `DriveStatus`.

**Tech Stack:** C# 12 / .NET 8 minimal-API (`WebApplication`), `System.Text.Json`, xUnit + `WebApplicationFactory<Program>` + `Microsoft.AspNetCore.TestHost`, vanilla ES5-style `app.js`.

## Global Constraints

- **Branch + PR:** all work on `feat/apple2-disk-library`; open a PR to `main`; do not commit to `main` directly.
- **Interpreter-first invariant:** every gate runs on the interpreter tier; no JIT-only behavior.
- **`.dsk`/`.po` are end-to-end-loadable; `.woz` is listed-but-disabled.** Raw `.woz` runtime insert throws `NotSupportedException` in the shipped `DiskImageFactory.FromBytes` — the catalog marks `.woz` entries `supported:false` with the note; never call `InsertDisk` with a `.woz` library item. (The `WozFluxImage` follow-on is a separate backlog row.)
- **Cache root convention (verbatim from `Apple2Rom`/`SoftCardCpm`):** `Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "cpuemulator", "vectors")`. Disk library images live under `<root>/disks/`; the CP/M boot disk is the already-cached `<root>/cpm/softcard-cpm.dsk` (`SoftCardCpm.TryGetDiskPath()`).
- **Copy strings (verbatim from `docs/design-handoffs/apple-2-plus/copy.md`):** empty catalog option text `No cached disks — see tools/get-*`; library placeholder option `Insert from library…`; loading option `Loading…`; insert-failed inline error `Couldn't load that disk — it may have been removed from the cache.`
- **Comment policy / structured style:** match the existing `Apple2Rom.cs` / `Program.cs` doc-comment density; no emojis in any UI string or comment.
- **No new NuGet dependencies.**
- **Ground truth HEAD:** `main` @ `204cf3d` (PRs #99–#120 merged). All literal code below calls the shipped signatures at that HEAD.

---

## File Structure

**New files:**
- `src/CpuEmulator.Machines/DiskCatalog.cs` — enumerates the cached disk library + the CP/M disk into `DiskCatalogEntry` records. Pure file-system read; no ASP.NET dependency (testable headless, mirrors `Apple2Rom`'s test-seam `root` parameter).
- `tests/CpuEmulator.Tests/Machines/DiskCatalogTests.cs` — headless catalog tests (seeded temp dir).
- `tests/CpuEmulator.Tests/Surface/DiskLibraryEndpointTests.cs` — `GET /disks` HTTP + WS `disk-insert` end-to-end (the un-fakeable gate).
- `tests/CpuEmulator.Tests/Surface/DiskInsertDecodeTests.cs` — `FrameCodec.TryDecodeDisk` unit tests.
- `tests/CpuEmulator.Tests/Surface/DriveTwoStatusTests.cs` — the drive-2 fold-in gate.

**Modified files:**
- `src/CpuEmulator.Surface.Web/FrameCodec.cs` — add `DiskCommand` struct + `TryDecodeDisk`.
- `src/CpuEmulator.Surface.Web/Program.cs` — map `GET /disks`; hoist `insertDisk`/`ejectDisk`; pass them into `ReceiveKeysAsync`; dispatch `disk-insert`/`disk-eject`.
- `src/CpuEmulator.Surface.Web/Apple2Surface.cs`, `SoftCardSurface.cs`, `SoftCardVidexSurface.cs` — add the mutable `DriveLabels` holder; grow `Status()` to two drives; update `InsertDisk`/`EjectDisk` to track per-drive labels.
- `src/CpuEmulator.Surface.Web/wwwroot/app.js` — add `loadCatalog()` + `insertFromLibrary()` + `ejectDrive()` transport helpers (read-only catalog fetch + text senders T binds to).

---

## Task 1: `DiskCatalog` — enumerate the cached disk library

**Files:**
- Create: `src/CpuEmulator.Machines/DiskCatalog.cs`
- Test: `tests/CpuEmulator.Tests/Machines/DiskCatalogTests.cs`

**Interfaces:**
- Consumes: the cache-root convention (verbatim from `Apple2Rom`/`SoftCardCpm`); `SoftCardCpm.TryGetDiskPath(string? root)`.
- Produces:
  - `public sealed record DiskCatalogEntry(string Id, string Name, string Format, bool Cpm, bool Supported)` — `Id` is the catalog key the client echoes back on insert; `Format` is one of `"dsk"`/`"po"`/`"woz"`; `Cpm` groups the CP/M disk last; `Supported` is `false` for `.woz` (no `WozFluxImage` yet).
  - `public static IReadOnlyList<DiskCatalogEntry> DiskCatalog.List(string? root = null)` — deterministic (sorted), CP/M grouped last.
  - `public static bool DiskCatalog.TryResolve(string id, out string path, out string format, string? root = null)` — maps a catalog id back to an absolute path + format for the server-side insert; `false` if the id is unknown or its file vanished.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/CpuEmulator.Tests/Machines/DiskCatalogTests.cs
using CpuEmulator.Machines;

namespace CpuEmulator.Tests.Machines;

public class DiskCatalogTests
{
    // A seeded cache root: <root>/disks/*.dsk|*.po|*.woz + <root>/cpm/softcard-cpm.dsk.
    private static string SeedRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "cpuemu-disks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "disks"));
        Directory.CreateDirectory(Path.Combine(root, "cpm"));
        File.WriteAllBytes(Path.Combine(root, "disks", "DOS33.dsk"), new byte[35 * 16 * 256]);
        File.WriteAllBytes(Path.Combine(root, "disks", "ProDOS.po"), new byte[35 * 16 * 256]);
        File.WriteAllBytes(Path.Combine(root, "disks", "Choplifter.woz"), new byte[256]);
        File.WriteAllBytes(Path.Combine(root, "cpm", "softcard-cpm.dsk"), new byte[35 * 16 * 256]);
        return root;
    }

    [Fact]
    public void List_enumerates_dsk_po_woz_and_groups_the_cpm_disk_last()
    {
        string root = SeedRoot();
        try
        {
            IReadOnlyList<DiskCatalogEntry> entries = DiskCatalog.List(root);

            // Three library images + the CP/M disk.
            Assert.Equal(4, entries.Count);
            // The CP/M disk is grouped last and flagged.
            DiskCatalogEntry last = entries[^1];
            Assert.True(last.Cpm);
            Assert.Equal("dsk", last.Format);
            // .woz is listed but unsupported (no WozFluxImage yet).
            DiskCatalogEntry woz = entries.Single(e => e.Format == "woz");
            Assert.False(woz.Supported);
            // .dsk/.po are supported.
            Assert.True(entries.Single(e => e.Format == "dsk" && !e.Cpm).Supported);
            Assert.True(entries.Single(e => e.Format == "po").Supported);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void List_on_an_absent_disks_dir_returns_an_empty_catalog()
    {
        string root = Path.Combine(Path.GetTempPath(), "cpuemu-empty-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(DiskCatalog.List(root));
    }

    [Fact]
    public void TryResolve_maps_a_library_id_back_to_its_path_and_format()
    {
        string root = SeedRoot();
        try
        {
            DiskCatalogEntry dsk = DiskCatalog.List(root).First(e => e.Format == "dsk" && !e.Cpm);
            Assert.True(DiskCatalog.TryResolve(dsk.Id, out string path, out string format, root));
            Assert.True(File.Exists(path));
            Assert.Equal("dsk", format);
            Assert.False(DiskCatalog.TryResolve("no-such-id", out _, out _, root));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DiskCatalogTests"`
Expected: FAIL — `DiskCatalog` / `DiskCatalogEntry` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/CpuEmulator.Machines/DiskCatalog.cs
namespace CpuEmulator.Machines;

/// <summary>One entry in the cached disk library exposed by <c>GET /disks</c> (design D11 / T-C). The
/// <see cref="Id"/> is the opaque key the client echoes back on a library insert; <see cref="Format"/> is
/// one of "dsk"/"po"/"woz"; <see cref="Cpm"/> groups the SoftCard CP/M disk last; <see cref="Supported"/>
/// is false for ".woz" until a thin WozFluxImage parser ships (a separable IFluxImage follow-on — the
/// runtime <see cref="CpuEmulator.Surface.Web"/> DiskImageFactory.FromBytes throws NotSupportedException for
/// raw .woz bytes today). The UI lists .woz disabled-with-note; it never inserts one.</summary>
public sealed record DiskCatalogEntry(string Id, string Name, string Format, bool Cpm, bool Supported);

/// <summary>Lists the cached disk-library images for the surface's per-drive [ Library ▾] select (design
/// D11 / T-C). The cache root mirrors <see cref="Apple2Rom"/>/<see cref="SoftCardCpm"/>
/// ($CPUEMULATOR_TESTVECTORS, default ~/.cache/cpuemulator/vectors). Library images live under
/// &lt;root&gt;/disks/ (*.dsk, *.po, *.woz); the SoftCard CP/M boot disk is the already-cached
/// &lt;root&gt;/cpm/softcard-cpm.dsk, listed last + flagged. Pure file-system read — no ASP.NET dependency,
/// so it tests headless; the optional <paramref name="root"/> is the same test seam Apple2Rom uses.</summary>
public static class DiskCatalog
{
    private static string CacheRoot =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_TESTVECTORS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "vectors");

    private static readonly string[] LibraryExtensions = { ".dsk", ".po", ".woz" };

    /// <summary>The cached library, sorted by name with the CP/M disk grouped last. Absent dir -> empty.</summary>
    public static IReadOnlyList<DiskCatalogEntry> List(string? root = null)
    {
        string baseRoot = root ?? CacheRoot;
        var entries = new List<DiskCatalogEntry>();

        string disksDir = Path.Combine(baseRoot, "disks");
        if (Directory.Exists(disksDir))
        {
            foreach (string file in Directory.EnumerateFiles(disksDir)
                                             .Where(f => LibraryExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();   // ".dsk"/".po"/".woz"
                string format = ext.TrimStart('.');                        // "dsk"/"po"/"woz"
                entries.Add(new DiskCatalogEntry(
                    Id: "lib/" + Path.GetFileName(file),
                    Name: Path.GetFileNameWithoutExtension(file),
                    Format: format,
                    Cpm: false,
                    Supported: format != "woz"));
            }
        }

        // The SoftCard CP/M boot disk (already cached under <root>/cpm/) — grouped last, flagged CP/M.
        string? cpm = SoftCardCpm.TryGetDiskPath(baseRoot);
        if (cpm is not null)
            entries.Add(new DiskCatalogEntry(
                Id: "cpm",
                Name: "SoftCard CP/M 2.2",
                Format: "dsk",
                Cpm: true,
                Supported: true));

        return entries;
    }

    /// <summary>Map a catalog id back to its absolute path + format for the server-side insert. False if
    /// the id is unknown or its file has been removed from the cache since the catalog was listed.</summary>
    public static bool TryResolve(string id, out string path, out string format, string? root = null)
    {
        path = string.Empty;
        format = string.Empty;
        if (string.IsNullOrEmpty(id))
            return false;
        string baseRoot = root ?? CacheRoot;

        if (id == "cpm")
        {
            string? cpm = SoftCardCpm.TryGetDiskPath(baseRoot);
            if (cpm is null) return false;
            path = cpm; format = "dsk"; return true;
        }

        const string prefix = "lib/";
        if (!id.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        string fileName = id[prefix.Length..];
        // Guard against path traversal: the id carries a bare file name only.
        if (fileName != Path.GetFileName(fileName))
            return false;
        string candidate = Path.Combine(baseRoot, "disks", fileName);
        if (!File.Exists(candidate))
            return false;
        path = candidate;
        format = Path.GetExtension(candidate).TrimStart('.').ToLowerInvariant();
        return true;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DiskCatalogTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Machines/DiskCatalog.cs tests/CpuEmulator.Tests/Machines/DiskCatalogTests.cs
git commit -m "feat(apple2): DiskCatalog enumerates the cached disk library (PR-R task 1)"
```

---

## Task 2: `FrameCodec.TryDecodeDisk` — decode the `disk-insert` / `disk-eject` text message

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/FrameCodec.cs` (add after `TryDecodeKey`, before `MapDomCode`)
- Test: `tests/CpuEmulator.Tests/Surface/DiskInsertDecodeTests.cs`

**Interfaces:**
- Consumes: `System.Text.Json` (already imported in `FrameCodec.cs`).
- Produces:
  - `public readonly record struct DiskCommand(bool Eject, int Drive, string Id)` — `Eject` distinguishes `disk-eject` from `disk-insert`; `Id` is the catalog id (empty for eject).
  - `public static bool FrameCodec.TryDecodeDisk(string json, out DiskCommand cmd)` — parses `{"action":"disk-insert","drive":N,"id":"..."}` or `{"action":"disk-eject","drive":N}`; returns false for any other JSON (so the key path is never shadowed).

- [ ] **Step 1: Write the failing test**

```csharp
// tests/CpuEmulator.Tests/Surface/DiskInsertDecodeTests.cs
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class DiskInsertDecodeTests
{
    [Fact]
    public void Decodes_a_disk_insert_with_drive_and_id()
    {
        Assert.True(FrameCodec.TryDecodeDisk(
            "{\"action\":\"disk-insert\",\"drive\":2,\"id\":\"lib/DOS33.dsk\"}", out var cmd));
        Assert.False(cmd.Eject);
        Assert.Equal(2, cmd.Drive);
        Assert.Equal("lib/DOS33.dsk", cmd.Id);
    }

    [Fact]
    public void Decodes_a_disk_eject_with_drive()
    {
        Assert.True(FrameCodec.TryDecodeDisk("{\"action\":\"disk-eject\",\"drive\":1}", out var cmd));
        Assert.True(cmd.Eject);
        Assert.Equal(1, cmd.Drive);
    }

    [Fact]
    public void Rejects_a_key_event_json_so_the_key_path_is_never_shadowed()
    {
        Assert.False(FrameCodec.TryDecodeDisk("{\"action\":\"down\",\"code\":\"KeyA\",\"char\":\"a\"}", out _));
    }

    [Fact]
    public void Rejects_an_out_of_range_drive()
    {
        Assert.False(FrameCodec.TryDecodeDisk("{\"action\":\"disk-insert\",\"drive\":9,\"id\":\"lib/x.dsk\"}", out _));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DiskInsertDecodeTests"`
Expected: FAIL — `TryDecodeDisk` / `DiskCommand` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/CpuEmulator.Surface.Web/FrameCodec.cs — add this struct just above the class, inside the namespace:
/// <summary>A decoded disk-library command from the client's text WS path (design D11/D13): a
/// <c>disk-insert</c> (drive + catalog id) or a <c>disk-eject</c> (drive). The library bytes live
/// server-side, so the wire carries only the id; the server resolves + loads them.</summary>
public readonly record struct DiskCommand(bool Eject, int Drive, string Id);
```

```csharp
// src/CpuEmulator.Surface.Web/FrameCodec.cs — add inside the FrameCodec class, after TryDecodeKey:
    /// <summary>Decode a disk-library command: <c>{"action":"disk-insert","drive":N,"id":"..."}</c> or
    /// <c>{"action":"disk-eject","drive":N}</c>. Returns false for any other JSON (so the inbound key
    /// path, which the session tries first, is never shadowed) or an out-of-range drive (1..2).</summary>
    public static bool TryDecodeDisk(string json, out DiskCommand cmd)
    {
        cmd = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            string action = root.TryGetProperty("action", out JsonElement a) ? a.GetString() ?? "" : "";
            bool eject = action == "disk-eject";
            if (action != "disk-insert" && !eject)
                return false;
            if (!root.TryGetProperty("drive", out JsonElement d) || d.ValueKind != JsonValueKind.Number)
                return false;
            int drive = d.GetInt32();
            if (drive is < 1 or > 2)
                return false;
            string id = !eject && root.TryGetProperty("id", out JsonElement i) ? i.GetString() ?? "" : "";
            if (!eject && id.Length == 0)
                return false;
            cmd = new DiskCommand(eject, drive, id);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DiskInsertDecodeTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/CpuEmulator.Surface.Web/FrameCodec.cs tests/CpuEmulator.Tests/Surface/DiskInsertDecodeTests.cs
git commit -m "feat(apple2): FrameCodec.TryDecodeDisk for the library insert/eject text path (PR-R task 2)"
```

---

## Task 3: Drive-2 status fold-in — `Status()` reports BOTH drives

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/Apple2Surface.cs`
- Modify: `src/CpuEmulator.Surface.Web/SoftCardSurface.cs`
- Modify: `src/CpuEmulator.Surface.Web/SoftCardVidexSurface.cs`
- Test: `tests/CpuEmulator.Tests/Surface/DriveTwoStatusTests.cs`

**Interfaces:**
- Consumes: the shipped `Apple2DiskII Disk` (`MotorOn` is a single shared motor line — both drive entries report it, matching the real one-motor Disk II) + the shipped `InsertDisk`/`EjectDisk`.
- Produces: a mutable `DriveLabels` holder per surface (`Label1` defaulting to the ctor `Drive1Label`, `Label2` defaulting to `"—"`); `Status()` now returns a two-element `Drives` array; `InsertDisk(drive, bytes, format, label)` and `EjectDisk(drive)` update the per-drive label. The PR-Q two-arg `InsertDisk(drive, bytes, format)` is kept as a thin overload (label defaults to `"—"`) so existing call sites and tests stay green.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/CpuEmulator.Tests/Surface/DriveTwoStatusTests.cs
using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class DriveTwoStatusTests
{
    private static byte[] SystemRom()
    {
        var rom = new byte[0x3000];
        rom[0x2FFC] = 0x62; rom[0x2FFD] = 0xFA;   // reset vector
        return rom;
    }

    private static byte[] Dsk() => new byte[35 * 16 * 256];

    [Fact]
    public void Status_reports_two_drives_with_per_drive_labels()
    {
        Apple2Surface surface = Apple2Surface.Create(
            SystemRom(), diskBootRom: null, charRom: null,
            frameSink: _ => { }, audioSink: _ => { });

        // Two drive entries from the start (drive 2 is real since PR-Q).
        MachineStatus s0 = surface.Status();
        Assert.Equal(2, s0.Drives.Count);
        Assert.Equal("—", s0.Drives[0].Label);
        Assert.Equal("—", s0.Drives[1].Label);

        // Insert into drive 2 with a label -> only the 2nd entry updates.
        surface.InsertDisk(drive: 2, bytes: Dsk(), format: DiskFormat.Dsk, label: "DOS33");
        MachineStatus s1 = surface.Status();
        Assert.Equal("—", s1.Drives[0].Label);
        Assert.Equal("DOS33", s1.Drives[1].Label);

        // Eject drive 2 -> back to "—".
        surface.EjectDisk(drive: 2);
        Assert.Equal("—", surface.Status().Drives[1].Label);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DriveTwoStatusTests"`
Expected: FAIL — `Status().Drives.Count` is 1; no `label:` overload.

- [ ] **Step 3: Write minimal implementation (all three surfaces, identical pattern)**

In `Apple2Surface.cs`, add the holder field + update `Status()`/`InsertDisk`/`EjectDisk`. Replace the shipped `Status()`, `InsertDisk`, `EjectDisk` block (lines 54–73) with:

```csharp
    // Mutable per-drive labels for the ST frame (design D9/D14): the immutable record can't hold runtime
    // label state, so a tiny holder tracks each drive's current image label, updated on insert/eject.
    private readonly DriveLabels _labels = new();

    /// <summary>Snapshot the REAL machine state for the <c>ST</c> status frame (design D14): the board
    /// name, the live video-mode label, and the live per-drive motor + image label. Both modeled drives
    /// (PR-Q made drive 2 real) report the shared motor line + their tracked label.</summary>
    public MachineStatus Status() => new(
        Board: "Apple ][+",
        Asset: "apple",
        Mode: Video.ModeLabel,
        Drives:
        [
            new DriveStatus(Disk.MotorOn, _labels.Label1),
            new DriveStatus(Disk.MotorOn, _labels.Label2),
        ]);

    /// <summary>Insert a disk image (raw bytes + format) into <paramref name="drive"/> at runtime — the
    /// in-session swap the library (R) and upload (S) paths call (design T-D / D11–D12). Builds the
    /// IFluxImage via DiskImageFactory, hands it to the live Disk II controller, and tracks the per-drive
    /// label for the ST frame.</summary>
    public void InsertDisk(int drive, byte[] bytes, DiskFormat format, string label)
    {
        Disk.Insert(drive, DiskImageFactory.FromBytes(bytes, format));
        _labels.Set(drive, label);
    }

    /// <summary>PR-Q's two-arg overload (label defaults to "—") — kept so existing call sites/tests are
    /// unchanged.</summary>
    public void InsertDisk(int drive, byte[] bytes, DiskFormat format) =>
        InsertDisk(drive, bytes, format, "—");

    /// <summary>Eject <paramref name="drive"/>'s image at runtime (design D13 — allowed mid-access, no
    /// confirm). The drive reads nothing until a re-insert; its label returns to "—".</summary>
    public void EjectDisk(int drive)
    {
        Disk.Eject(drive);
        _labels.Set(drive, "—");
    }
```

Add the constructor wiring so `Label1` defaults to the surface's `Drive1Label`. In `Apple2Surface.Create`, the `_labels` holder needs the ctor label; since `_labels` is a field initializer it cannot see the ctor arg, so initialize it in `Status()`-readiness by setting it in `Create` via a small private setter. Replace the field declaration above with a field set from the record's `Drive1Label` using a property-backed holder constructed lazily — concretely, initialize in `Create` right before returning:

```csharp
        var surface = new Apple2Surface(machine, video, keyboard, speaker, host, disk, drive1Label);
        surface._labels.Set(1, drive1Label);   // drive 1 starts at the ctor label ("—" for the plain ][+)
        return surface;
```

(Replace the shipped final two lines of `Create` — `var host = ...; return new Apple2Surface(...);` — with the `host` line unchanged plus the three lines above.)

Create the shared holder file:

```csharp
// src/CpuEmulator.Surface.Web/DriveLabels.cs
namespace CpuEmulator.Surface.Web;

/// <summary>Mutable per-drive image labels for the ST status frame (design D9/D14). The surface records
/// are immutable, but the drive labels change as disks insert/eject at runtime (R's library + S's upload),
/// so a tiny holder tracks them. The motor is the controller's shared one-motor line (Apple2DiskII.MotorOn);
/// only the labels are per-drive here.</summary>
internal sealed class DriveLabels
{
    public string Label1 { get; private set; } = "—";
    public string Label2 { get; private set; } = "—";

    public void Set(int drive, string label)
    {
        if (drive == 1) Label1 = label;
        else if (drive == 2) Label2 = label;
    }
}
```

Apply the identical `_labels` field, `Status()`, `InsertDisk` overloads, `EjectDisk`, and the `Create`-tail `surface._labels.Set(1, drive1Label)` to `SoftCardSurface.cs` and `SoftCardVidexSurface.cs` — verbatim except:
- `SoftCardSurface.Status()` keeps `Board: "Apple ][+ SoftCard"`, `Asset: "softcard-cpm"`, `Mode: Video.ModeLabel`.
- `SoftCardVidexSurface.Status()` keeps `Board: "Apple ][+ SoftCard"`, `Asset: "softcard-cpm-videx"`, `Mode: Display.ActiveIndex == VidexIndex ? "Videx 80×24 · CP/M" : Video.ModeLabel`.
- In each `Create`, the return becomes:
  ```csharp
        var surface = new SoftCardSurface(machine, video, keyboard, speaker, host, disk, drive1Label);
        surface._labels.Set(1, drive1Label);   // drive 1 starts at the ctor label ("CP/M")
        return surface;
  ```
  and likewise for `SoftCardVidexSurface` (its ctor takes the longer arg list shown in the shipped file: `new SoftCardVidexSurface(machine, video, videx, mux, keyboard, speaker, host, disk, drive1Label)`).

- [ ] **Step 4: Run the new test + the existing surface-status/swap tests to verify nothing regressed**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DriveTwoStatusTests|FullyQualifiedName~Apple2SurfaceStatusTests|FullyQualifiedName~Apple2SurfaceDiskSwapTests|FullyQualifiedName~StatusFrameCodecTests"`
Expected: PASS. Note `Apple2SurfaceStatusTests.Status_reads_real_board_mode_and_drive_state` asserts `Assert.Single(s.Drives)` on the shipped single-drive `Status()`.

- [ ] **Step 5: Update the now-stale single-drive assertion in the shipped test**

The shipped `Apple2SurfaceStatusTests.Status_reads_real_board_mode_and_drive_state` (line 26) asserts `Assert.Single(s.Drives)`. Drive 2 is real and now reported. Change that one assertion:

```csharp
        // Two modeled drives (PR-Q made drive 2 real; PR-R reports both); both empty at boot.
        Assert.Equal(2, s.Drives.Count);
        Assert.False(s.Drives[0].MotorOn);
        Assert.Equal("—", s.Drives[0].Label);
        Assert.Equal("—", s.Drives[1].Label);
```

(Replace the three lines `Assert.Single(s.Drives); Assert.False(s.Drives[0].MotorOn); Assert.Equal("—", s.Drives[0].Label);` with the four lines above.)

- [ ] **Step 6: Re-run the affected suite**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DriveTwoStatusTests|FullyQualifiedName~Apple2SurfaceStatusTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src/CpuEmulator.Surface.Web/DriveLabels.cs src/CpuEmulator.Surface.Web/Apple2Surface.cs src/CpuEmulator.Surface.Web/SoftCardSurface.cs src/CpuEmulator.Surface.Web/SoftCardVidexSurface.cs tests/CpuEmulator.Tests/Surface/DriveTwoStatusTests.cs tests/CpuEmulator.Tests/Surface/Apple2SurfaceStatusTests.cs
git commit -m "feat(apple2): ST frame reports BOTH drives with per-drive labels (PR-R drive-2 fold-in)"
```

---

## Task 4: `GET /disks` endpoint + the `disk-insert` / `disk-eject` WS dispatch (the un-fakeable gate)

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/Program.cs`
- Test: `tests/CpuEmulator.Tests/Surface/DiskLibraryEndpointTests.cs`

**Interfaces:**
- Consumes: `DiskCatalog.List` / `DiskCatalog.TryResolve` (Task 1); `FrameCodec.TryDecodeDisk` + `DiskCommand` (Task 2); the surface `InsertDisk(drive,bytes,format,label)` / `EjectDisk(drive)` (Task 3); the shipped `WebApplicationFactory<Program>` + `CreateWebSocketClient` harness pattern (`WebServerSmokeTests`).
- Produces: a `GET /disks` JSON endpoint (array of `{id,name,format,cpm,supported}`); a hoisted `Action<int,byte[],DiskFormat,string>? insertDisk` + `Action<int>? ejectDisk` threaded into `ReceiveKeysAsync`, which dispatches `disk-insert`/`disk-eject` (server resolves the catalog id, reads the cached bytes, calls the surface insert).

- [ ] **Step 1: Write the failing test (the gate)**

```csharp
// tests/CpuEmulator.Tests/Surface/DiskLibraryEndpointTests.cs
using System.Net;
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
}
```

Note: the second test asserts session health post-insert (the un-fakeable end-to-end "selecting an entry inserts it into drive N without crashing the live session"); the per-nibble read-back is already covered headlessly by `Apple2SurfaceDiskSwapTests` (the shipped Q gate) and by Task 5's direct insert assertion below.

- [ ] **Step 2: Write a direct server-insert gate (un-fakeable nibble read) that does not need WS framing**

Add to the same test file — this is the un-fakeable proof a resolved-then-loaded library disk actually reads back, exercising `DiskCatalog.TryResolve` → `File.ReadAllBytes` → `surface.InsertDisk`:

```csharp
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
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DiskLibraryEndpointTests"`
Expected: FAIL — `GET /disks` returns 404 (endpoint not mapped); the WS insert is silently ignored (no dispatch). `A_resolved_library_disk_loads...` may already pass (it exercises only Task 1 + Task 3), which is fine.

- [ ] **Step 4: Map `GET /disks` in `Program.cs`**

In `Program.Main`, after the `app.Map("/ws", …)` block and before `app.Run();`, add:

```csharp
        // The disk-library catalog (design D11 / T-C): the per-drive [ Library ▾] select fetches this.
        // A pure file-system read of the cache (no machine state) — served as compact JSON.
        app.MapGet("/disks", () =>
        {
            var entries = CpuEmulator.Machines.DiskCatalog.List()
                .Select(e => new { id = e.Id, name = e.Name, format = e.Format, cpm = e.Cpm, supported = e.Supported })
                .ToArray();
            return Results.Json(entries);
        });
```

- [ ] **Step 5: Thread `insertDisk`/`ejectDisk` from the surface into `ReceiveKeysAsync`**

In `DemoSession.RunAsync`, hoist two delegates beside `statusProvider` (after the `Func<MachineStatus>? statusProvider = null;` line, line 80):

```csharp
        Action<int, byte[], DiskFormat, string>? insertDisk = null;   // library/upload insert (R/S)
        Action<int>? ejectDisk = null;                                // library eject (R)
```

In the SoftCard branch (the `if (appleRom is not null && cpmDisk is not null)` block), after `statusProvider = () => softcard.Status() with { Asset = asset };` add:

```csharp
            insertDisk = (drive, bytes, format, label) => softcard.InsertDisk(drive, bytes, format, label);
            ejectDisk = drive => softcard.EjectDisk(drive);
```

In the Apple branch (the `else if (appleRom is not null)` block), after `statusProvider = () => apple.Status() with { Asset = asset };` add:

```csharp
            insertDisk = (drive, bytes, format, label) => apple.InsertDisk(drive, bytes, format, label);
            ejectDisk = drive => apple.EjectDisk(drive);
```

(The Spectrum/demo branches leave both null — no Apple disk to insert.)

Change the `recv` task wiring (line 152) to pass the new delegates:

```csharp
        Task recv = ReceiveKeysAsync(socket, pump, insertDisk, ejectDisk, ct);
```

Replace the `ReceiveKeysAsync` signature + body (lines 185–203) with:

```csharp
    private static async Task ReceiveKeysAsync(WebSocket socket, ISurfacePump pump,
                                              Action<int, byte[], DiskFormat, string>? insertDisk,
                                              Action<int>? ejectDisk,
                                              CancellationToken ct)
    {
        var buffer = new byte[1024];
        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", ct);
                break;
            }
            if (result.MessageType != WebSocketMessageType.Text)
                continue;
            string json = Encoding.UTF8.GetString(buffer, 0, result.Count);
            // Keys first (the hot path); then disk-library commands (design D11/D13).
            if (FrameCodec.TryDecodeKey(json, out KeyEvent e))
            {
                pump.PostKey(e);
            }
            else if (FrameCodec.TryDecodeDisk(json, out DiskCommand cmd))
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
                    // .woz library items are listed-disabled in the UI; never reach here. Guard anyway —
                    // DiskImageFactory.FromBytes throws NotSupportedException for .woz, so skip it.
                    if (format != DiskFormat.Woz)
                    {
                        byte[] bytes = File.ReadAllBytes(path);
                        insertDisk(cmd.Drive, bytes, format, Path.GetFileNameWithoutExtension(path));
                    }
                }
            }
        }
    }
```

Note: `TryDecodeKey` returns true for `disk-insert`/`disk-eject` JSON only if those parse as a key event — they do not (the `action` is neither "up" nor matched to a code; `MapDomCode` returns `KeyCode.None`, but `TryDecodeKey` still returns `true` with a `None` key). To avoid the key path swallowing disk commands, try disk decode FIRST. Reorder: check `TryDecodeDisk` before `TryDecodeKey`:

```csharp
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
```

Use this reordered form as the final body. Confirm `using CpuEmulator.Machines;` is not needed (the code uses the fully-qualified `CpuEmulator.Machines.DiskCatalog`); `System.IO` (`File`, `Path`) is implicitly available via the SDK's global usings.

- [ ] **Step 6: Run the gate to verify it passes**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DiskLibraryEndpointTests"`
Expected: PASS (3 tests).

- [ ] **Step 7: Run the smoke tests to confirm the existing wire is unaffected**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~WebServerSmokeTests"`
Expected: PASS (2 tests) — the single-text-frame protocol + FB stream are unchanged.

- [ ] **Step 8: Commit**

```bash
git add src/CpuEmulator.Surface.Web/Program.cs tests/CpuEmulator.Tests/Surface/DiskLibraryEndpointTests.cs
git commit -m "feat(apple2): GET /disks endpoint + disk-insert/eject WS dispatch (PR-R task 4, the gate)"
```

---

## Task 5: Client transport — `loadCatalog()` + `insertFromLibrary()` + `ejectDrive()`

**Files:**
- Modify: `src/CpuEmulator.Surface.Web/wwwroot/app.js`

**Interfaces:**
- Consumes: the `GET /disks` endpoint (Task 4); the `disk-insert`/`disk-eject` text WS path (Task 4); the shipped `ws` socket + `window.machineStatus`.
- Produces: `window.diskCatalog` (the fetched array, for row T's panel render); `window.insertFromLibrary(drive, id)` and `window.ejectDrive(drive)` (text senders row T's panel buttons call). No visible DOM here — the per-drive `[ Library ▾]` panel is row T; R ships the data + senders so T is pure DOM.

- [ ] **Step 1: Add the catalog fetch + senders to `app.js`**

Just before the final `})();` (after the `window.addEventListener("keydown", … Ctrl+Backspace …)` block, line 148), add:

```javascript
  // --- Disk library (PR-R, design D11) ---
  // Fetch the cached-disk catalog (GET /disks) once on load; row T's drive panels render window.diskCatalog.
  // Read-only data — the client never fabricates entries; the server lists the real cache.
  window.diskCatalog = [];
  function loadCatalog() {
    fetch("/disks")
      .then((r) => (r.ok ? r.json() : []))
      .then((list) => { window.diskCatalog = Array.isArray(list) ? list : []; })
      .catch(() => { window.diskCatalog = []; });
  }
  loadCatalog();

  // Insert a library disk into drive N (text WS, design D11). The bytes are already server-side; the wire
  // carries only the catalog id. Row T's [ Library ▾] onchange calls this.
  window.insertFromLibrary = function (drive, id) {
    if (ws.readyState !== WebSocket.OPEN || !id) return;
    ws.send(JSON.stringify({ action: "disk-insert", drive: drive, id: id }));
  };

  // Eject drive N (text WS, design D13). Row T's [ Eject ] calls this.
  window.ejectDrive = function (drive) {
    if (ws.readyState !== WebSocket.OPEN) return;
    ws.send(JSON.stringify({ action: "disk-eject", drive: drive }));
  };
```

- [ ] **Step 2: Verify the client still serves + parses (smoke through the server)**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~WebServerSmokeTests"`
Expected: PASS — `Root_serves_the_canvas_client` confirms `/` still serves `index.html` (which references `app.js`); the JS additions are syntactically valid (the test host serves the static file unchanged).

- [ ] **Step 3: Manually sanity-check the JS parses (no Node required — a quick byte/paren check)**

Run: `node --check src/CpuEmulator.Surface.Web/wwwroot/app.js` if Node is available; else open the page in a browser during UAT and confirm `window.diskCatalog` is an array and no console error appears. Expected: no syntax error.

- [ ] **Step 4: Commit**

```bash
git add src/CpuEmulator.Surface.Web/wwwroot/app.js
git commit -m "feat(apple2): client loadCatalog + insertFromLibrary/ejectDrive transport (PR-R task 5)"
```

---

## Task 6: Full-suite green + warning-clean + the row's gate

**Files:** none (verification task).

- [ ] **Step 1: Build warning-clean**

Run: `dotnet build CpuEmulator.sln -warnaserror`
Expected: Build succeeded, 0 warnings.

- [ ] **Step 2: Run the full unit suite**

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`
Expected: all green (the PR-Q baseline 7239 passed + the new PR-R tests; 6 skipped unchanged). 0 failed.

- [ ] **Step 3: Confirm the row's un-fakeable gate in one place**

The gate (`docs/BUILDER_QUEUE.md` row R): *the endpoint lists a seeded cache dir + selecting an entry inserts it into drive N (reuse Q's runtime insert).* This is `DiskLibraryEndpointTests.GET_disks_lists_the_seeded_library` (lists a seeded dir) + `Selecting_a_library_entry_inserts_it_into_drive_N` (the live-session insert) + `A_resolved_library_disk_loads_and_a_running_read_pulls_a_nibble` (the un-fakeable read-back).

Run: `dotnet test tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj --filter "FullyQualifiedName~DiskLibraryEndpointTests"`
Expected: PASS (3 tests).

- [ ] **Step 4: Commit the BUILDER_QUEUE row-R status flip (done in the queue edit when merging) — no code commit here.**

---

## Self-Review (completed by the planner)

**Spec coverage (design D11 / T-C + the queue gate):**
- `GET /disks` catalog listing name/format/drive-compat/CP/M grouping → Task 1 (`DiskCatalog`) + Task 4 (the endpoint). ✅
- Per-drive `[ Library ▾]` populates from it → Task 5 (`loadCatalog` + `window.diskCatalog`; the visible select is row T, which binds to this data). ✅
- Insert via the shipped Q `InsertDisk` path → Task 4 (server resolves id → `surface.InsertDisk`). ✅
- Empty catalog → disabled select with the calm named-script hint → the copy string `No cached disks — see tools/get-*` is in Global Constraints; the empty-catalog `List` returns `[]` (Task 1 test `List_on_an_absent_disks_dir_returns_an_empty_catalog`); the disabled-select render is row T's DOM bound to the empty `window.diskCatalog`. ✅ (R ships the empty-catalog data path; T renders the disabled option.)
- `.dsk`/`.po` only; `.woz` listed-disabled-with-note, don't crash → Task 1 marks `.woz` `Supported:false`; Task 4's dispatch guards `format != DiskFormat.Woz`. ✅
- Drive-2 status fold-in (the ST frame reports BOTH drives) → Task 3. ✅
- Gate: lists a seeded cache dir + selecting inserts into drive N → Task 4 + Task 6. ✅

**Placeholder scan:** Task 2 Step 1 intentionally flags a deliberate-typo placeholder and immediately supplies the corrected test body; all other steps carry literal code. No `TBD`/`implement later`/`similar to Task N`. ✅

**Type consistency:** `DiskCatalogEntry(Id,Name,Format,Cpm,Supported)`, `DiskCommand(Eject,Drive,Id)`, `DiskCatalog.List`/`TryResolve`, `DriveLabels.Set`, `InsertDisk(drive,bytes,format,label)` are used identically across tasks. The four-arg `InsertDisk` is introduced in Task 3 and consumed in Task 4. ✅

**Drift flagged:** none new beyond the already-recorded `.woz` / `WozFluxImage` follow-on (the queue's WozFluxImage backlog row). `MotorOn` is a single shared motor line — both drive entries report it (correct for the one-motor Disk II); only labels are per-drive.

---

## Execution Handoff

Plan complete and saved. Builder picks this up after the queue row is on `main`. Recommended execution: subagent-driven-development (fresh subagent per task, two-stage review between tasks).
