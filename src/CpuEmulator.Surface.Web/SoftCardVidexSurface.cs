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
/// shared RAM and drives the Videx terminal. The Videx ROMs are optional (synthetic fallback).</summary>
public sealed record SoftCardVidexSurface(
    Machine Machine, Apple2Video Video, VidexVideoterm Videx, DisplayMultiplexer Display,
    Apple2Keyboard Keyboard, Apple2Speaker Speaker, MachineHost Host,
    Apple2DiskII Disk, string Drive1Label)
{
    private const int AppleIndex = 0;
    private const int VidexIndex = 1;

    public static SoftCardVidexSurface Create(byte[] systemRom, byte[] diskBootRom, byte[]? charRom,
                                              byte[]? videxCharRom, byte[]? videxFirmware,
                                              IBlockDevice cpmDisk,
                                              Action<byte[]> frameSink, Action<byte[]> audioSink,
                                              ExecutionTier tier = ExecutionTier.Interpreter,
                                              string drive1Label = "CP/M")
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
        // Drive 1 = the CP/M .dsk, re-nibblized with the CP/M data-track skew onto the unchanged Disk II head.
        var drive1 = new DskFluxImage(cpmDisk, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk, videx);   // PR-N's 4-arg ctor (the Videx $C0Bx delegate)

        BoardSpec spec = SoftCardVidexBoard.Spec(systemRom, iou, disk, diskBootRom, videx);
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
        return new SoftCardVidexSurface(
            machine, video, videx, mux, keyboard, speaker, host, disk, drive1Label);
    }

    /// <summary>Snapshot the REAL machine state for the <c>ST</c> status frame (design D14): the SoftCard
    /// board name, the live drive-1 motor + CP/M image label, and the mode label read from the LIVE
    /// display multiplexer — when the Videx is the active source (CP/M's terminal driver enabled it) the
    /// mode is the Videx 80-col label, else the Apple 40-col video-mode label.</summary>
    public MachineStatus Status() => new(
        Board: "Apple ][+ SoftCard",
        Asset: "softcard-cpm-videx",
        Mode: Display.ActiveIndex == VidexIndex ? "Videx 80×24 · CP/M" : Video.ModeLabel,
        Drives: [new DriveStatus(Disk.MotorOn, Drive1Label)]);
}
