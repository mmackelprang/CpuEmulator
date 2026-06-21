namespace CpuEmulator.Surface.Web;

/// <summary>Mutable per-drive image labels for the ST status frame (design D9/D14). The surface records
/// are immutable, but the drive labels change as disks insert/eject at runtime (R's library + S's upload),
/// so a tiny holder tracks them. The motor is the controller's shared one-motor line (Apple2DiskII.MotorOn);
/// only the labels are per-drive here.
/// <para>Thread note: <see cref="Set"/> runs on the WS receive thread (a library insert/eject) while the
/// pump thread reads <see cref="Label1"/>/<see cref="Label2"/> through <c>Status()</c>. The backing fields
/// are <c>volatile</c> so the pump thread always observes the latest published reference (a string
/// reference write is atomic; <c>volatile</c> adds the ordering guarantee so a stale label can't linger).</para></summary>
internal sealed class DriveLabels
{
    private volatile string _label1 = "—";
    private volatile string _label2 = "—";

    public string Label1 => _label1;
    public string Label2 => _label2;

    public void Set(int drive, string label)
    {
        if (drive == 1) _label1 = label;
        else if (drive == 2) _label2 = label;
    }
}
