using CpuEmulator.Surface.Web;

namespace CpuEmulator.Tests.Surface;

public class StatusPushOnChangeTests
{
    [Fact]
    public void Pushes_once_initially_then_only_when_the_snapshot_changes()
    {
        var sent = new List<byte[]>();
        bool motor = false;
        // The provider reads "live" state each tick (here, a mutable local standing in for the machine).
        MachineStatus Provider() => new(
            "Apple ][+", "apple", "TEXT · 40×24 · page 1",
            [new DriveStatus(motor, "—")]);

        var pusher = new StatusPusher(Provider, frame => sent.Add(frame));

        pusher.Tick();                 // first tick -> initial push
        Assert.Single(sent);

        pusher.Tick();                 // no change -> no push
        Assert.Single(sent);

        motor = true;                  // the REAL motor turned on
        pusher.Tick();                 // change -> exactly one more push
        Assert.Equal(2, sent.Count);

        // The second frame's JSON carries the new motor=true (the change is the real flag, not faked).
        string text = System.Text.Encoding.UTF8.GetString(sent[1]);
        Assert.Contains("\"motor\":true", text);

        pusher.Tick();                 // still on, unchanged -> no push
        Assert.Equal(2, sent.Count);
    }
}
