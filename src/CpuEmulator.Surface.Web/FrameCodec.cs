using System.Buffers.Binary;
using System.Text.Json;
using CpuEmulator.Core;

namespace CpuEmulator.Surface.Web;

/// <summary>
/// The SP0 WebSocket wire format. Frames OUT: a small binary header ('F','B', version, reserved,
/// uint16 width LE, uint16 height LE) followed by width*height little-endian RGBA8888 pixels (raw —
/// no delta/RLE in SP0; one local client at 256×192 is well within bandwidth). Keys IN: JSON text
/// {"action","code","char"} where "code" is the DOM KeyboardEvent.code; <see cref="MapDomCode"/>
/// normalizes it to a portable <see cref="KeyCode"/> (unknown -> <see cref="KeyCode.None"/>).
/// </summary>
public static class FrameCodec
{
    private const int HeaderBytes = 8;

    public static byte[] EncodeFrame(int width, int height, ReadOnlySpan<uint> pixels)
    {
        if (pixels.Length < width * height)
            throw new ArgumentException("pixel buffer smaller than width*height", nameof(pixels));

        var frame = new byte[HeaderBytes + width * height * 4];
        frame[0] = (byte)'F';
        frame[1] = (byte)'B';
        frame[2] = 0x01; // version
        frame[3] = 0x00; // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6, 2), (ushort)height);

        Span<byte> body = frame.AsSpan(HeaderBytes);
        for (int i = 0; i < width * height; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(body.Slice(i * 4, 4), pixels[i]);
        return frame;
    }

    public static bool TryDecodeKey(string json, out KeyEvent e)
    {
        e = default;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;

            string action = root.TryGetProperty("action", out JsonElement a) ? a.GetString() ?? "" : "";
            string code = root.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? "" : "";
            string charStr = root.TryGetProperty("char", out JsonElement ch) ? ch.GetString() ?? "" : "";

            KeyAction keyAction = action == "up" ? KeyAction.Up : KeyAction.Down;
            KeyCode key = MapDomCode(code);
            char? typed = charStr.Length == 1 ? charStr[0] : null;
            e = new KeyEvent(keyAction, key, typed);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Map a DOM <c>KeyboardEvent.code</c> to a portable <see cref="KeyCode"/>. Unknown
    /// codes return <see cref="KeyCode.None"/> (the machine ignores them).</summary>
    public static KeyCode MapDomCode(string code) => code switch
    {
        "KeyA" => KeyCode.A, "KeyB" => KeyCode.B, "KeyC" => KeyCode.C, "KeyD" => KeyCode.D,
        "KeyE" => KeyCode.E, "KeyF" => KeyCode.F, "KeyG" => KeyCode.G, "KeyH" => KeyCode.H,
        "KeyI" => KeyCode.I, "KeyJ" => KeyCode.J, "KeyK" => KeyCode.K, "KeyL" => KeyCode.L,
        "KeyM" => KeyCode.M, "KeyN" => KeyCode.N, "KeyO" => KeyCode.O, "KeyP" => KeyCode.P,
        "KeyQ" => KeyCode.Q, "KeyR" => KeyCode.R, "KeyS" => KeyCode.S, "KeyT" => KeyCode.T,
        "KeyU" => KeyCode.U, "KeyV" => KeyCode.V, "KeyW" => KeyCode.W, "KeyX" => KeyCode.X,
        "KeyY" => KeyCode.Y, "KeyZ" => KeyCode.Z,
        "Digit0" => KeyCode.Digit0, "Digit1" => KeyCode.Digit1, "Digit2" => KeyCode.Digit2,
        "Digit3" => KeyCode.Digit3, "Digit4" => KeyCode.Digit4, "Digit5" => KeyCode.Digit5,
        "Digit6" => KeyCode.Digit6, "Digit7" => KeyCode.Digit7, "Digit8" => KeyCode.Digit8,
        "Digit9" => KeyCode.Digit9,
        "Space" => KeyCode.Space,
        "Enter" => KeyCode.Enter,
        "Backspace" => KeyCode.Backspace,
        "Tab" => KeyCode.Tab,
        "Escape" => KeyCode.Escape,
        "ArrowLeft" => KeyCode.ArrowLeft,
        "ArrowRight" => KeyCode.ArrowRight,
        "ArrowUp" => KeyCode.ArrowUp,
        "ArrowDown" => KeyCode.ArrowDown,
        _ => KeyCode.None,
    };
}
