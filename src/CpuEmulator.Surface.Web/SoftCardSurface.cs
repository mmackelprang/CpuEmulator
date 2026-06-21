using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;

namespace CpuEmulator.Surface.Web;

/// <summary>Composes the Apple ][+ SoftCard (dual-CPU CP/M) for the web surface — the analogue of
/// <see cref="Apple2Surface"/>. Identical to Apple2Surface EXCEPT (1) drive 1 holds the CP/M .dsk
/// (re-nibblized via <see cref="DskFluxImage"/> with the CP/M data-track skew, <see cref="SectorOrderKind.Cpm"/>)
/// and (2) the board is the dual-CPU <see cref="SoftCardBoard"/> (the base Apple board + the Z80 coprocessor
/// + the $C500 SoftCard control port). The Z80 is dormant at reset; the 6502 $C600 boot loads the CP/M
/// cold-boot loader from the .dsk, the on-disk code issues the $CnXX write that starts the Z80, and CP/M
/// runs translated. On the bare SoftCard board the display is the Apple 40-col video (the Videx 80-col is
/// PR-N/O); the triad + MachineHost wiring is the Apple2Surface body verbatim.</summary>
public sealed record SoftCardSurface(
    Machine Machine, Apple2Video Video, Apple2Keyboard Keyboard, Apple2Speaker Speaker,
    MachineHost Host, Apple2DiskII Disk, string Drive1Label)
{
    public static SoftCardSurface Create(byte[] systemRom, byte[] diskBootRom, byte[]? charRom,
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
        // Drive 1 = the CP/M .dsk, re-nibblized with the CP/M data-track skew onto the unchanged Disk II head.
        var drive1 = new DskFluxImage(cpmDisk, SectorOrderKind.Cpm);
        var disk = new Apple2DiskII(drive1);
        var iou = new Apple2Iou(state, lc, disk);

        BoardSpec spec = SoftCardBoard.Spec(systemRom, iou, disk, diskBootRom);
        Machine machine = BoardMachineFactory.Build(spec, tier);

        video.Realize(machine);
        speaker.Realize(machine);
        machine.Reset();

        var host = new MachineHost(machine, video, keyboard, frameSink, speaker, audioSink);
        var surface = new SoftCardSurface(machine, video, keyboard, speaker, host, disk, drive1Label);
        surface._labels.Set(1, drive1Label);   // drive 1 starts at the ctor label ("CP/M")
        return surface;
    }

    // Mutable per-drive labels for the ST frame (design D9/D14): the immutable record can't hold runtime
    // label state, so a tiny holder tracks each drive's current image label, updated on insert/eject.
    private readonly DriveLabels _labels = new();

    /// <summary>Snapshot the REAL machine state for the <c>ST</c> status frame (design D14): the SoftCard
    /// board name, the live Apple 40-col video-mode label, and the live per-drive motor + image label.
    /// Both modeled drives (PR-Q made drive 2 real) report the shared motor line + their tracked label.</summary>
    public MachineStatus Status() => new(
        Board: "Apple ][+ SoftCard",
        Asset: "softcard-cpm",
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
}
