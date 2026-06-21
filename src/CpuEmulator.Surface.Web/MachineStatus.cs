namespace CpuEmulator.Surface.Web;

/// <summary>One read-only Disk II drive indicator for the <c>ST</c> status frame: the REAL motor flag
/// (the $C0E8/$C0E9 motor switches + the shipped ~1 s 556 off-delay, ADR 0014 Decision 6 — NOT faked on
/// insert) and the loaded-image label the surface holds ("—" when empty/synthetic). The host reads the
/// motor flag live each push; the surface stays a dumb reflector of the controller's truth.</summary>
public sealed record DriveStatus(bool MotorOn, string Label);

/// <summary>The host→client read-only machine-status snapshot (design D14 / task T-A). Carries the board
/// name, the asset-state string (the existing banner contract), the derived video-mode label, and the
/// per-drive motor + image indicators. Every field is REAL machine state read at push time — no field is
/// fabricated client-side. Pushed (as the <c>ST</c> text frame) only when the snapshot changes.</summary>
public sealed record MachineStatus(
    string Board, string Asset, string Mode, IReadOnlyList<DriveStatus> Drives);
