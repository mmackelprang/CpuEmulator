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
