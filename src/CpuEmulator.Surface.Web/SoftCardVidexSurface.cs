using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>Composes the Apple ][+ SoftCard + Videx for the web surface — the CP/M-display capstone
/// (ADR 0016, PR-O), the SoftCardSurface twin PLUS the Videx 80-column display. Identical to
/// SoftCardSurface EXCEPT (1) a VidexVideoterm is wired into the IOU + the SoftCardVidexBoard, and (2) the
/// host's display is a DisplayMultiplexer([apple40, videx80]) whose active source follows the Videx's
/// guest-driven ActiveChanged signal (ADR 0016 Decision 1/2 — CP/M's terminal driver enabling the Videx
/// switches the host display Apple-40 -> Videx-80, no UI toggle). The MachineHost re-sizes its buffer
/// 280x192 -> 560x216 on the switch (PR-M). CP/M boots on the Z80 (interpreter tier) translated against
/// shared RAM and drives the Videx terminal. The Videx ROMs are optional (synthetic fallback).
///
/// This one surface serves BOTH cached CP/M configurations, selected by the caller via the
/// <c>sectorOrder</c> + <c>controlPortBase</c> params (defaults preserve the shipped 2.2 rig byte-for-byte):
/// the 40-col CP/M 2.2 master (<see cref="SectorOrderKind.Cpm"/> per-track skew, slot-5 $C500 control port —
/// the defaults) AND the 80-col apl2cpm3 CP/M 3.1 master (<see cref="SectorOrderKind.Cpm3"/> raw-DOS33
/// skew, slot-4 $C400 control port — see <see cref="CreateApl2Cpm3"/>; ADR 0018-A / V80-1).</summary>
public sealed record SoftCardVidexSurface(
    Machine Machine, Apple2Video Video, VidexVideoterm Videx, DisplayMultiplexer Display,
    Apple2Keyboard Keyboard, Apple2Speaker Speaker, MachineHost Host,
    Apple2DiskII Disk, string Drive1Label)
{
    private const int AppleIndex = 0;
    private const int VidexIndex = 1;

    /// <param name="sectorOrder">The Disk II nibblization skew for drive 1 (ADR 0018-A). Defaults to
    /// <see cref="SectorOrderKind.Cpm"/> (the 2.2 per-track skew) so the shipped 2.2 callers are unchanged;
    /// apl2cpm3 passes <see cref="SectorOrderKind.Cpm3"/> (raw DOS33 on every track).</param>
    /// <param name="controlPortBase">The page the SoftCard control port decodes (ADR 0018 Decision 1).
    /// Defaults to <see cref="SoftCardBoard.ControlPortBaseSlot5"/> ($C500) so the 2.2 board is byte-for-byte
    /// unchanged; apl2cpm3 passes <see cref="SoftCardBoard.ControlPortBaseSlot4"/> ($C400, slot 4).</param>
    public static SoftCardVidexSurface Create(byte[] systemRom, byte[] diskBootRom, byte[]? charRom,
                                              byte[]? videxCharRom, byte[]? videxFirmware,
                                              IBlockDevice cpmDisk,
                                              Action<byte[]> frameSink, Action<byte[]> audioSink,
                                              ExecutionTier tier = ExecutionTier.Interpreter,
                                              string drive1Label = "CP/M",
                                              SectorOrderKind sectorOrder = SectorOrderKind.Cpm,
                                              uint controlPortBase = SoftCardBoard.ControlPortBaseSlot5)
    {
        ArgumentNullException.ThrowIfNull(systemRom);
        ArgumentNullException.ThrowIfNull(diskBootRom);
        ArgumentNullException.ThrowIfNull(cpmDisk);

        var state = new Apple2VideoState();
        var placeholder = new AddressSpace(AddressSpaceKind.Program, 16);
        placeholder.MapMemory(0x0000, new byte[0x10000], writable: true);
        var video = new Apple2Video(placeholder, state, charRom);
        var keyboard = new Apple2Keyboard(state);
        var speaker = new Apple2Speaker(state);
        var lc = new Apple2LanguageCard(systemRom);
        var videx = new VidexVideoterm(videxCharRom, videxFirmware);
        // Drive 1 = the CP/M .dsk, re-nibblized with the caller's data-track skew onto the unchanged Disk II
        // head (Cpm per-track for 2.2; Cpm3 raw-DOS33-on-every-track for apl2cpm3 — ADR 0018-A).
        var drive1 = new DskFluxImage(cpmDisk, sectorOrder);
        var disk = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk, videx);   // PR-N's 4-arg ctor (the Videx $C0Bx delegate)

        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, diskBootRom, videx,
            controlPortBase: controlPortBase);
        Machine machine = BoardMachineFactory.Build(spec, tier);

        video.Realize(machine);
        speaker.Realize(machine);
        // The Videx is Realized by the factory (its own "videx" board slot) — no explicit Realize here.
        machine.Reset();

        // The two display sources behind the host: Apple 40-col (index 0, active at boot) + the Videx
        // 80-col (index 1). The guest-driven auto-switch: the Videx's ActiveChanged drives SetActive
        // (ADR 0016 Decision 2 — the user never picks; CP/M's terminal driver enabling the Videx is the
        // switch). The MachineHost re-sizes its buffer on the switch (PR-M).
        var mux = new DisplayMultiplexer([video, videx], initialActive: AppleIndex);
        videx.ActiveChanged += active => mux.SetActive(active ? VidexIndex : AppleIndex);

        var host = new MachineHost(machine, mux, keyboard, frameSink, speaker, audioSink);
        var surface = new SoftCardVidexSurface(
            machine, video, videx, mux, keyboard, speaker, host, disk, drive1Label);
        surface._labels.Set(1, drive1Label);   // drive 1 starts at the ctor label ("CP/M")
        return surface;
    }

    /// <summary>The apl2cpm3 80-col CP/M 3.1 rig: a thin <see cref="Create"/> forwarder that selects the
    /// apl2cpm3 configuration — <see cref="SectorOrderKind.Cpm3"/> (raw-DOS33-on-every-track skew, ADR 0018-A)
    /// + <see cref="SoftCardBoard.ControlPortBaseSlot4"/> ($C400, slot 4 — apl2cpm3 hard-codes STA $C400 to
    /// start the Z80) + the "CP/M 3.1" drive-1 label (V80-1). The shipped 2.2 path keeps <see cref="Create"/>
    /// with its defaults (Cpm skew, slot-5 $C500) untouched.</summary>
    public static SoftCardVidexSurface CreateApl2Cpm3(byte[] systemRom, byte[] diskBootRom, byte[]? charRom,
                                                      byte[]? videxCharRom, byte[]? videxFirmware,
                                                      IBlockDevice cpmDisk,
                                                      Action<byte[]> frameSink, Action<byte[]> audioSink,
                                                      ExecutionTier tier = ExecutionTier.Interpreter) =>
        Create(systemRom, diskBootRom, charRom, videxCharRom, videxFirmware, cpmDisk, frameSink, audioSink,
               tier, drive1Label: "CP/M 3.1",
               sectorOrder: SectorOrderKind.Cpm3,
               controlPortBase: SoftCardBoard.ControlPortBaseSlot4);

    // Mutable per-drive labels for the ST frame (design D9/D14): the immutable record can't hold runtime
    // label state, so a tiny holder tracks each drive's current image label, updated on insert/eject.
    private readonly DriveLabels _labels = new();

    /// <summary>Snapshot the REAL machine state for the <c>ST</c> status frame (design D14): the SoftCard
    /// board name, the live per-drive motor + image label, and the mode label read from the LIVE
    /// display multiplexer — when the Videx is the active source (CP/M's terminal driver enabled it) the
    /// mode is the Videx 80-col label, else the Apple 40-col video-mode label. Both modeled drives (PR-Q
    /// made drive 2 real) report the shared motor line + their tracked label.</summary>
    public MachineStatus Status() => new(
        Board: "Apple ][+ SoftCard",
        Asset: "softcard-cpm-videx",
        Mode: Display.ActiveIndex == VidexIndex ? "Videx 80×24 · CP/M" : Video.ModeLabel,
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
}
