using CpuEmulator.Core;

namespace CpuEmulator.Tests.Surface;

public class KeyboardContractTests
{
    private sealed class StubSink : IKeyboardSink
    {
        public readonly List<KeyEvent> Seen = [];
        public void PostKey(in KeyEvent e) => Seen.Add(e);
    }

    [Fact]
    public void KeyEvent_carries_action_keycode_and_optional_char()
    {
        var sink = new StubSink();
        sink.PostKey(new KeyEvent(KeyAction.Down, KeyCode.A, 'a'));
        sink.PostKey(new KeyEvent(KeyAction.Up, KeyCode.A, null));

        Assert.Equal(2, sink.Seen.Count);
        Assert.Equal(KeyAction.Down, sink.Seen[0].Action);
        Assert.Equal(KeyCode.A, sink.Seen[0].Key);
        Assert.Equal('a', sink.Seen[0].Char);
        Assert.Equal(KeyAction.Up, sink.Seen[1].Action);
        Assert.Null(sink.Seen[1].Char);
    }

    [Fact]
    public void KeyCode_None_is_the_zero_value_for_unknown_keys()
    {
        Assert.Equal(0, (int)KeyCode.None);
    }
}
