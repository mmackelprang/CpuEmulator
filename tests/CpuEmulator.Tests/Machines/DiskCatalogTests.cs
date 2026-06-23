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
            // .woz is now supported (WozFluxImage parses WOZ2 — backlog row W shipped).
            DiskCatalogEntry woz = entries.Single(e => e.Format == "woz");
            Assert.True(woz.Supported);
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
