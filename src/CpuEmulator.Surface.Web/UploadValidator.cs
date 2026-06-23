namespace CpuEmulator.Surface.Web;

/// <summary>The outcome of server-side upload re-validation (design D12): Ok=true with an empty Message on
/// success, else the calm inline-error copy (copy.md §7). Never throws on a bad image — a malformed upload
/// is a normal user condition, not a server error.</summary>
public readonly record struct UploadResult(bool Ok, string Message);

/// <summary>Server-side re-validation of a decoded <see cref="UploadFrame"/> (design D12 / T-B). Never
/// trust the client: re-check length (.dsk/.po exact <see cref="DiskImageFactory.DskBytes"/>) and the .woz
/// magic. The .woz path validates its magic and accepts — WozFluxImage parses WOZ2 (backlog row W shipped);
/// a malformed body is caught at construction (DiskImageFactory.FromBytes throws InvalidDataException). The
/// end-to-end-loadable formats are .dsk, .po, and .woz.</summary>
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
                // WozFluxImage parses WOZ2 (backlog row W). A malformed body is caught at construction
                // (DiskImageFactory.FromBytes throws InvalidDataException), surfaced as the generic error.
                return new UploadResult(true, "");
            default:
                return new UploadResult(false, "That image looks corrupt");
        }
    }
}
